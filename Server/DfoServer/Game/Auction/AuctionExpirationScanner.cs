using DfoServer.Infrastructure;
using System;
using System.Threading;

namespace DfoServer.Game.Auction
{
    internal sealed class AuctionExpirationScanner
    {
        private const int MaximumBatchSize = 100;
        private const string ClockTimerName =
            "auction:expire-next-active-listing";

        private static readonly int[] RetryDelaySeconds =
        {
            1,
            5,
            30,
            300,
        };

        private readonly IAuctionExpiredListingSource _listingSource;
        private readonly AuctionReturnService _returnService;
        private readonly IAuctionTimeProvider _timeProvider;
        private readonly object _scheduleSync = new object();

        private ClockService _clock;
        private DateTime? _scheduledDueUtc;
        private long _scheduleGeneration;
        private long _lifecycleVersion;
        private int _retryAttempt;
        private int _scanning;
        private bool _reconciling;
        private bool _reconcilePending;
        private bool _clockRegistered;

        public AuctionExpirationScanner(
            IAuctionExpiredListingSource listingSource,
            AuctionReturnService returnService,
            IAuctionTimeProvider timeProvider = null)
        {
            _listingSource = listingSource
                ?? throw new ArgumentNullException(nameof(listingSource));
            _returnService = returnService
                ?? throw new ArgumentNullException(nameof(returnService));
            _timeProvider = timeProvider
                ?? SystemAuctionTimeProvider.Instance;
        }

        public AuctionExpirationScanResult Scan(
            long nowUnixSeconds,
            int limit)
        {
            if (Interlocked.Exchange(ref _scanning, 1) != 0)
            {
                return new AuctionExpirationScanResult
                {
                    SkippedBecauseRunning = true,
                };
            }

            try
            {
                var boundedLimit = Math.Max(
                    1,
                    Math.Min(MaximumBatchSize, limit));
                var candidates = _listingSource.LoadExpiredCandidates(
                    nowUnixSeconds,
                    boundedLimit);
                var completed = 0;
                foreach (var candidate in candidates)
                {
                    if (_returnService.TryExpire(
                            candidate.ListingId,
                            nowUnixSeconds).Success)
                    {
                        completed++;
                    }
                }

                return new AuctionExpirationScanResult
                {
                    CandidateCount = candidates.Count,
                    CompletedCount = completed,
                };
            }
            finally
            {
                Interlocked.Exchange(ref _scanning, 0);
            }
        }

        public void RegisterClock(ClockService clock)
        {
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            lock (_scheduleSync)
            {
                if (_clockRegistered)
                    return;

                _clock = clock;
                _clockRegistered = true;
            }

            Reconcile(_timeProvider.UtcNowUnixSeconds());
        }

        public void NotifyListingCommitted(long expiresAtUnixSeconds)
        {
            if (expiresAtUnixSeconds <= 0)
                return;

            var dueUtc = UnixSecondsToUtc(expiresAtUnixSeconds);
            lock (_scheduleSync)
            {
                if (!_clockRegistered || _clock == null)
                    return;
                _lifecycleVersion++;
                if (_scheduledDueUtc.HasValue
                    && _scheduledDueUtc.Value <= dueUtc)
                {
                    return;
                }

                ScheduleAtLocked(dueUtc);
            }
        }

        public void NotifyActiveListingRemoved()
        {
            lock (_scheduleSync)
            {
                if (!_clockRegistered || _clock == null)
                    return;
                _lifecycleVersion++;
            }

            ThreadPool.QueueUserWorkItem(
                _ => Reconcile(_timeProvider.UtcNowUnixSeconds()));
        }

        private void Reconcile(long nowUnixSeconds)
        {
            lock (_scheduleSync)
            {
                if (_reconciling)
                {
                    _reconcilePending = true;
                    return;
                }
                _reconciling = true;
            }

            try
            {
                while (true)
                {
                    long observedLifecycleVersion;
                    lock (_scheduleSync)
                        observedLifecycleVersion = _lifecycleVersion;

                    while (true)
                    {
                        var result = Scan(
                            nowUnixSeconds,
                            MaximumBatchSize);
                        if (result.SkippedBecauseRunning)
                        {
                            ScheduleRetry(nowUnixSeconds);
                            return;
                        }
                        if (result.CompletedCount < result.CandidateCount)
                        {
                            FileLogger.Log(
                                $"[Auction] expiry batch incomplete completed={result.CompletedCount} candidates={result.CandidateCount}; scheduling retry");
                            ScheduleRetry(nowUnixSeconds);
                            return;
                        }
                        if (result.CandidateCount < MaximumBatchSize)
                            break;
                    }

                    var nextExpiry =
                        _listingSource.LoadNextActiveExpiryUnixSeconds();
                    lock (_scheduleSync)
                    {
                        if (observedLifecycleVersion
                            != _lifecycleVersion)
                        {
                            continue;
                        }

                        _retryAttempt = 0;
                        if (nextExpiry.HasValue)
                        {
                            ScheduleAtLocked(
                                UnixSecondsToUtc(nextExpiry.Value));
                        }
                        else
                        {
                            CancelScheduledLocked();
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Auction] expiry reconcile failed: {ex.Message}");
                ScheduleRetry(nowUnixSeconds);
            }
            finally
            {
                bool rerun;
                lock (_scheduleSync)
                {
                    _reconciling = false;
                    rerun = _reconcilePending;
                    _reconcilePending = false;
                }
                if (rerun)
                {
                    ThreadPool.QueueUserWorkItem(
                        _ => Reconcile(
                            _timeProvider.UtcNowUnixSeconds()));
                }
            }
        }

        private void ScheduleRetry(long nowUnixSeconds)
        {
            lock (_scheduleSync)
            {
                if (!_clockRegistered || _clock == null)
                    return;

                var delayIndex = Math.Min(
                    _retryAttempt,
                    RetryDelaySeconds.Length - 1);
                if (_retryAttempt < int.MaxValue)
                    _retryAttempt++;
                var retryAt = checked(
                    nowUnixSeconds + RetryDelaySeconds[delayIndex]);
                ScheduleAtLocked(UnixSecondsToUtc(retryAt));
            }
        }

        private void ScheduleAtLocked(DateTime dueUtc)
        {
            if (_clock == null)
                return;

            _scheduleGeneration++;
            var generation = _scheduleGeneration;
            _scheduledDueUtc = dueUtc;
            _clock.ScheduleOneShot(
                ClockTimerName,
                dueUtc,
                utcNow => OnTimer(generation, utcNow));
        }

        private void CancelScheduledLocked()
        {
            _scheduleGeneration++;
            _scheduledDueUtc = null;
            _clock?.CancelOneShot(ClockTimerName);
        }

        private void OnTimer(long generation, DateTime utcNow)
        {
            lock (_scheduleSync)
            {
                if (generation == _scheduleGeneration)
                    _scheduledDueUtc = null;
            }

            ThreadPool.QueueUserWorkItem(
                _ => Reconcile(
                    new DateTimeOffset(
                        DateTime.SpecifyKind(
                            utcNow,
                            DateTimeKind.Utc))
                        .ToUnixTimeSeconds()));
        }

        private static DateTime UnixSecondsToUtc(long unixSeconds)
            => DateTimeOffset
                .FromUnixTimeSeconds(unixSeconds)
                .UtcDateTime;
    }
}
