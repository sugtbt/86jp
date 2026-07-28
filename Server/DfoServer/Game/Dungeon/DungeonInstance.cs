using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.Dungeon
{
    public sealed class DungeonSelectionSnapshot
    {
        private int[] _bossMapPosition;
        private IReadOnlyList<RidableObjectSpawnEntry> _ridableObjects =
            Array.Empty<RidableObjectSpawnEntry>();
        private ClearConditionState _clearConditionTemplate;

        public int MazeIndex { get; init; } = -1;
        public bool MazeQuestConnected { get; init; }
        public int MazeStartMapId { get; init; }
        public int MazeStartX { get; init; } = -1;
        public int MazeStartY { get; init; } = -1;
        public int TotalRoomCount { get; init; } = 1;
        public int PartyMemberCount { get; init; } = 1;
        public int[] BossMapPosition
        {
            get => _bossMapPosition == null
                ? null
                : (int[])_bossMapPosition.Clone();
            init => _bossMapPosition = value == null
                ? null
                : (int[])value.Clone();
        }
        public IReadOnlyList<RidableObjectSpawnEntry> RidableObjects
        {
            get => _ridableObjects;
            init
            {
                if (value == null || value.Count == 0)
                {
                    _ridableObjects = Array.Empty<RidableObjectSpawnEntry>();
                    return;
                }

                var copy = new RidableObjectSpawnEntry[value.Count];
                for (var i = 0; i < value.Count; i++)
                    copy[i] = value[i];
                _ridableObjects = new ReadOnlyCollection<RidableObjectSpawnEntry>(copy);
            }
        }
        public ClearConditionState ClearConditionTemplate
        {
            get => _clearConditionTemplate?.CloneFresh();
            init => _clearConditionTemplate = value?.CloneFresh();
        }

        internal void ApplyTo(DungeonRun run)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));

            run.MazeIndex = MazeIndex;
            run.MazeQuestConnected = MazeQuestConnected;
            run.MazeStartMapId = MazeStartMapId;
            run.MazeStartX = MazeStartX;
            run.MazeStartY = MazeStartY;
            run.TotalRoomCount = Math.Max(1, TotalRoomCount);
            run.EntryPartyMemberCount = Math.Max(1, Math.Min(4, PartyMemberCount));
            run.BossMapPos = _bossMapPosition == null
                ? null
                : (int[])_bossMapPosition.Clone();
            run.RidableObjects = _ridableObjects == null
                ? new List<RidableObjectSpawnEntry>()
                : new List<RidableObjectSpawnEntry>(_ridableObjects);
            run.ClearCondition = _clearConditionTemplate?.CloneFresh();
        }
    }

    public readonly struct DungeonKillStatistics
    {
        internal DungeonKillStatistics(
            int normalKillCount,
            int championKillCount,
            int bossKillCount)
        {
            NormalKillCount = normalKillCount;
            ChampionKillCount = championKillCount;
            BossKillCount = bossKillCount;
        }

        public int NormalKillCount { get; }
        public int ChampionKillCount { get; }
        public int BossKillCount { get; }
    }

    public sealed class DungeonInstanceRoom
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, DungeonEncounterRuntime> _encounters =
            new Dictionary<string, DungeonEncounterRuntime>(StringComparer.Ordinal);
        private DungeonRoomState _state = DungeonRoomState.Created;

        internal DungeonInstanceRoom(
            long roomInstanceId,
            RoomKey key,
            GameWorld.Dungeon.MazeSumInfo maze,
            uint seed,
            ushort firstActorSequenceId = 1)
        {
            RoomInstanceId = roomInstanceId;
            Key = key;
            Maze = maze;
            Seed = seed;
            FirstActorSequenceId = firstActorSequenceId;
        }

        public long RoomInstanceId { get; }
        public RoomKey Key { get; }
        public GameWorld.Dungeon.MazeSumInfo Maze { get; }
        public uint Seed { get; }
        public ushort FirstActorSequenceId { get; }
        public DungeonEffectLedger Effects { get; } = new DungeonEffectLedger();
        public DungeonRoomState State { get { lock (_syncRoot) return _state; } }
        public DungeonEncounterState EncounterState
        {
            get
            {
                lock (_syncRoot)
                {
                    return _encounters.TryGetValue(
                        DungeonEncounterDirective.DefaultEncounterKey,
                        out var runtime)
                        ? runtime.State
                        : DungeonEncounterState.NotStarted;
                }
            }
        }

        public bool TryActivate()
        {
            lock (_syncRoot)
            {
                if (_state == DungeonRoomState.Active)
                    return false;
                if (_state != DungeonRoomState.Created)
                    return false;
                _state = DungeonRoomState.Active;
                return true;
            }
        }

        public bool TryClear()
        {
            lock (_syncRoot)
            {
                if (_state == DungeonRoomState.Cleared)
                    return false;
                if (_state != DungeonRoomState.Active)
                    return false;
                _state = DungeonRoomState.Cleared;
                return true;
            }
        }

        public bool TryClose()
        {
            lock (_syncRoot)
            {
                if (_state == DungeonRoomState.Closed)
                    return false;
                _state = DungeonRoomState.Closed;
                return true;
            }
        }

        public bool TryStartEncounter()
        {
            lock (_syncRoot)
                return GetOrCreateEncounterLocked(
                    DungeonEncounterDirective.DefaultEncounterKey)
                    .TryApplyLegacy(DungeonEncounterDirectiveKind.Start);
        }

        public bool TryCompleteEncounter(bool succeeded)
        {
            lock (_syncRoot)
                return GetOrCreateEncounterLocked(
                    DungeonEncounterDirective.DefaultEncounterKey)
                    .TryApplyLegacy(
                        succeeded
                            ? DungeonEncounterDirectiveKind.Succeed
                            : DungeonEncounterDirectiveKind.Fail);
        }

        internal DungeonEncounterTransition ApplyEncounterDirective(
            DungeonEncounterDirective directive)
        {
            if (directive == null)
                throw new ArgumentNullException(nameof(directive));
            lock (_syncRoot)
                return GetOrCreateEncounterLocked(directive.EncounterKey)
                    .Apply(directive);
        }

        private DungeonEncounterRuntime GetOrCreateEncounterLocked(string key)
        {
            if (!_encounters.TryGetValue(key, out var runtime))
            {
                runtime = new DungeonEncounterRuntime();
                _encounters.Add(key, runtime);
            }
            return runtime;
        }
    }

    public sealed class DungeonInstance
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<RoomKey, DungeonInstanceRoom> _rooms =
            new Dictionary<RoomKey, DungeonInstanceRoom>();
        private readonly HashSet<(
            long RoomInstanceId,
            RoomKey RoomKey,
            ushort SequenceId)> _recordedKillActors =
                new HashSet<(long, RoomKey, ushort)>();
        private DungeonSelectionSnapshot _selection;
        private DungeonClearedFact _clearedFact;
        private int _normalKillCount;
        private int _championKillCount;
        private int _bossKillCount;

        public DungeonInstance(short dungeonId, byte difficulty)
            : this(
                dungeonId,
                difficulty,
                DungeonRewardPolicy.Standard,
                DungeonDropDefinition.CreateStandard(dungeonId))
        {
        }

        internal DungeonInstance(
            short dungeonId,
            byte difficulty,
            DungeonRewardPolicy rewardPolicy)
            : this(
                dungeonId,
                difficulty,
                rewardPolicy,
                DungeonDropDefinition.CreateStandard(dungeonId))
        {
        }

        internal DungeonInstance(
            short dungeonId,
            byte difficulty,
            DungeonRewardPolicy rewardPolicy,
            DungeonDropDefinition dropDefinition)
        {
            PartyDungeonInstanceId = DungeonIdentityGenerator.NextInstanceId();
            DungeonId = dungeonId;
            Difficulty = difficulty;
            RewardPolicy = rewardPolicy ?? throw new ArgumentNullException(nameof(rewardPolicy));
            DropDefinition = dropDefinition
                ?? throw new ArgumentNullException(nameof(dropDefinition));
            CreatedUtc = DateTime.UtcNow;
        }

        public long PartyDungeonInstanceId { get; }
        public short DungeonId { get; }
        public byte Difficulty { get; }
        public DungeonRewardPolicy RewardPolicy { get; }
        public DungeonDropDefinition DropDefinition { get; }
        public DateTime CreatedUtc { get; }
        public DungeonEffectLedger Effects { get; } = new DungeonEffectLedger();
        internal DungeonDiagnosticJournal Diagnostics { get; } =
            new DungeonDiagnosticJournal();
        public DungeonSelectionSnapshot Selection { get { lock (_syncRoot) return _selection; } }
        public DungeonClearedFact ClearedFact { get { lock (_syncRoot) return _clearedFact; } }
        public int VisitedRoomCount { get { lock (_syncRoot) return _rooms.Count; } }
        public DungeonKillStatistics KillStatistics
        {
            get
            {
                lock (_syncRoot)
                {
                    return new DungeonKillStatistics(
                        _normalKillCount,
                        _championKillCount,
                        _bossKillCount);
                }
            }
        }

        public bool TryFreezeSelection(DungeonSelectionSnapshot selection)
        {
            if (selection == null)
                throw new ArgumentNullException(nameof(selection));

            lock (_syncRoot)
            {
                if (_selection != null)
                    return false;
                _selection = selection;
                return true;
            }
        }

        public DungeonInstanceRoom GetOrCreateRoom(
            RoomKey key,
            Func<long, DungeonInstanceRoom> factory,
            out bool created)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            lock (_syncRoot)
            {
                if (_rooms.TryGetValue(key, out var existing))
                {
                    created = false;
                    return existing;
                }

                var room = factory(DungeonIdentityGenerator.NextRoomId());
                if (room == null || !room.Key.Equals(key))
                    throw new InvalidOperationException("Dungeon room factory returned an invalid room.");
                _rooms.Add(key, room);
                created = true;
                return room;
            }
        }

        public bool TryGetRoom(RoomKey key, out DungeonInstanceRoom room)
        {
            lock (_syncRoot)
                return _rooms.TryGetValue(key, out room);
        }

        internal bool TryGetRoom(
            long roomInstanceId,
            out DungeonInstanceRoom room)
        {
            lock (_syncRoot)
            {
                foreach (var candidate in _rooms.Values)
                {
                    if (candidate.RoomInstanceId == roomInstanceId)
                    {
                        room = candidate;
                        return true;
                    }
                }
            }

            room = null;
            return false;
        }

        public bool IsRoomCleared(int x, int y)
        {
            lock (_syncRoot)
            {
                foreach (var pair in _rooms)
                {
                    if (pair.Key.X == x
                        && pair.Key.Y == y
                        && pair.Value.State == DungeonRoomState.Cleared)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        internal bool TryRecordMonsterKill(
            long roomInstanceId,
            RoomKey roomKey,
            ushort sequenceId,
            byte actorType)
        {
            if (sequenceId == 0 || actorType == 9)
                return false;

            lock (_syncRoot)
            {
                if (!_recordedKillActors.Add((
                        roomInstanceId,
                        roomKey,
                        sequenceId)))
                {
                    return false;
                }

                if (actorType == 3 || actorType == 8)
                    _bossKillCount++;
                else if (actorType == 1)
                    _championKillCount++;
                else
                    _normalKillCount++;
                return true;
            }
        }

        public DungeonClearedFact GetOrCreateClearedFact(
            DungeonClearIntent intent,
            out bool created)
        {
            if (intent == null)
                throw new ArgumentNullException(nameof(intent));
            if (intent.Source.PartyDungeonInstanceId != PartyDungeonInstanceId)
                throw new InvalidOperationException(
                    "A clear intent must belong to this dungeon instance.");

            lock (_syncRoot)
            {
                if (_clearedFact != null)
                {
                    created = false;
                    return _clearedFact;
                }

                _clearedFact = new DungeonClearedFact(intent);
                created = true;
                return _clearedFact;
            }
        }
    }
}
