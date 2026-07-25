using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Pets;

namespace DfoServer.Game.Quests
{
    public sealed class QuestManager
    {
        private readonly ISessionPacketSender _sender;
        private readonly string _connStr;
        private readonly string _databasePath;
        private readonly QuestService _service;
        private readonly SqliteCharacterRepository _characterRepository;
        private readonly SqliteCharacterProgressRepository _progressRepository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly GrowthCapsuleProgressRepository _growthCapsuleRepository;

        public QuestManager(ISessionPacketSender sender, string connStr)
        {
            _sender = sender;
            _connStr = connStr;
            _databasePath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connStr).DataSource;
            _service = new QuestService(connStr);
            _characterRepository = new SqliteCharacterRepository(_databasePath, ServerPaths.SchemaFilePath);
            _progressRepository = SqliteCharacterProgressRepository.FromConnectionString(connStr);
            _honorLevel = new HonorLevelSyncService(
                _characterRepository,
                _databasePath,
                ServerPaths.SchemaFilePath);
            _subtype0Repository = new SqliteSubtype0FieldsRepository(
                _databasePath, ServerPaths.SchemaFilePath);
            _growthCapsuleRepository = new GrowthCapsuleProgressRepository(
                _databasePath, ServerPaths.SchemaFilePath);
        }

        private static byte[] StripEcho(byte[] body)
        {
            if (body == null || body.Length <= 2) return body;
            var stripped = new byte[body.Length - 2];
            Buffer.BlockCopy(body, 2, stripped, 0, stripped.Length);
            return stripped;
        }

        public async Task HandleAcceptQuestAsync(ushort wireType, byte[] body)
        {
            var qBody = StripEcho(body);
            FileLogger.Log($"[GameProtocol] ACCEPT_QUEST payload: {(qBody != null ? BitConverter.ToString(qBody) : "null")} ({qBody?.Length ?? 0}B)");
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var result = _service.HandleAcceptQuest(cid, qBody, _sender.AccountId);
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildAccept(result));
        }

        public async Task HandleGiveupQuestAsync(ushort wireType, byte[] body)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var result = _service.HandleGiveupQuest(cid, qBody);
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildGiveup(result));
        }

        public async Task<QuestSetTriggerResult> HandleSetTriggerAsync(
            ushort wireType,
            byte[] body)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return null;

            QuestSetTriggerResult deferred;
            if (TryBuildDeferredClearMapSetTrigger(cid, qBody, out deferred))
            {
                await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildSetTrigger(deferred));
                return deferred;
            }

            var result = _service.HandleSetTrigger(cid, qBody);
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildSetTrigger(result));
            return result;
        }

        public async Task HandleFinishQuestAsync(ushort wireType, byte[] body)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var result = _service.HandleFinishQuest(cid, qBody, _sender.Player?.Exp);

            // 转职/觉醒后在 ACK 之前重发全量技能表(0x0013), 客户端整体替换, 面板即时刷新。
            if (result.Success && (result.ChainType == 1 || result.ChainType == 2))
                await SendSkillInfoRefreshAsync(cid);

            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildFinish(result));

            if (!result.Success)
                return;

            var player = _sender.Player;
            var prevLevel = player.Level;

            // 经验/等级已在完成事务内落库(见 QuestService), 这里只同步会话内存。
            if (result.Exp > 0)
            {
                player.Exp = result.NewExp;
                player.Level = result.NewLevel;
            }

            var leveledUp = player.Level > prevLevel;
            var inDungeon = player.CurrentRun != null;
            var needsExpNotification = result.Exp > 0 || leveledUp;
            SkillPointProtocolState? skillPoints = null;
            if (needsExpNotification)
            {
                try
                {
                    var rec = _characterRepository.GetById(cid);
                    if (rec != null)
                    {
                        Characters.CharacterStatComputer.DecodeGrowType(rec.GrowType, out var fGrow, out var sGrow);
                        skillPoints = SkillStateService.LoadProtocolState(
                            _progressRepository,
                            cid,
                            rec.Job,
                            player.Level,
                            rec.BonusSp,
                            rec.BonusTp,
                            persist: leveledUp,
                            growType: fGrow,
                            secondGrowType: sGrow);
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[QuestManager] SP calc ERROR: {ex.Message}");
                }
            }

            var refreshesCharacterState = leveledUp
                || result.ChainType == 1
                || result.ChainType == 2
                || result.ChainType == 20
                || result.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion;
            HonorLevelSummary honorLevel = null;
            GrowthCapsuleSummary growthCapsule = null;
            if (result.HonorExp > 0)
            {
                honorLevel = HonorLevelDataProvider.CalculateFromHonorExp(result.TotalHonorExp, 0);
                growthCapsule = GrowthCapsuleDataProvider.Calculate(result.TotalGrowthCapsuleExp);
            }
            else if (needsExpNotification || refreshesCharacterState)
                honorLevel = ResolveHonorLevelForExp();
            if (needsExpNotification && player.Level >= ExpTableProvider.MaxLevel && growthCapsule == null)
                growthCapsule = _growthCapsuleRepository.LoadSummary(_sender.AccountId);
            if (result.HonorExp > 0 && player.Subtype0Tail != null)
                HonorLevelDataProvider.ApplyToSubtype0Tail(player.Subtype0Tail, honorLevel);
            // 城镇内升级: 先推角色状态(subtype0)+属性(subtype1)再发经验包, 面板即时刷新。
            // 副本内升级绝不能发角色状态包(subtype0) -- 它会打乱客户端的副本内角色状态,
            // 实测导致清房后无法进下一个门; 副本内沿用旧时序(经验包之后只补属性)。
            if (leveledUp && !inDungeon)
            {
                await SendUserInfoSubtype0Broadcast("LevelUp", honorLevel);
                await SendUserInfoBroadcast(cid, honorLevel);
            }

            if (needsExpNotification && skillPoints.HasValue)
            {
                await _sender.SendNotiAsync(0x0025,
                    ExpNotificationBuilder.Build(
                        player.Level, player.Exp, skillPoints.Value, honorLevel,
                        growthCapsuleExp: GrowthCapsuleDataProvider.GetDisplayProgress(
                            player.Level, growthCapsule)));
            }
            else if (needsExpNotification)
            {
                FileLogger.Log($"[QuestManager] EXP notification skipped: skill-point protocol state unavailable for cid={cid}");
            }

            if (leveledUp)
            {
                FileLogger.Log($"[QuestManager] LEVEL UP from quest: cid={cid} {prevLevel}->{player.Level} exp={player.Exp} inDungeon={inDungeon}");
                if (inDungeon)
                    await SendUserInfoBroadcast(cid, honorLevel);
            }

            if (result.HonorExp > 0)
            {
                FileLogger.Log($"[QuestManager] HONOR_EXP_GAIN quest: account={_sender.AccountId} cid={cid} gain={result.HonorExp} total={result.TotalHonorExp}");
                FileLogger.Log($"[QuestManager] GROWTH_CAPSULE_EXP_GAIN quest: account={_sender.AccountId} cid={cid} gain={result.GrowthCapsuleExp} total={result.TotalGrowthCapsuleExp}");
            }

            if (result.ChainType == 1 || result.ChainType == 2)
            {
                await SendJobChangeNotification(cid, honorLevel);
                await SendUserInfoBroadcast(cid, honorLevel);
            }
            else if (result.ChainType == 20)
            {
                await SendExpertJobChangeNotification(cid, result.GrowNumber, honorLevel);
                await SendUserInfoBroadcast(cid, honorLevel);
            }
            else if (result.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion)
            {
                // The ACK completes the quest, but the client opens the visual slot from refreshed subtype1 data.
                await SendUserInfoBroadcast(cid, honorLevel);
            }
            else if ((result.ChainType == 10 || result.ChainType == 25) && result.PetCreatureEvolution.Changed)
            {
                await PetCreatureRuntimeService.SendPetCreatureEvolutionAsync(_sender, result.PetCreatureEvolution);
            }

            var noti = BuildAcceptedQuestNoti(cid);
            await _sender.SendNotiAsync(0x023F, noti);
            await SendAcceptableQuestListAsync();
        }

        public async Task SendActiveQuestListAsync()
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var noti = BuildAcceptedQuestNoti(cid);
            await _sender.SendNotiAsync(0x023F, noti);
        }

        public async Task SyncMonsterRewardItemProgressAsync(ICollection<int> itemFilter)
        {
            await SyncItemSeekingQuestProgressAsync(itemFilter);
        }

        public async Task SyncItemSeekingQuestProgressAsync(
            ICollection<int> itemFilter,
            IReadOnlyDictionary<int, int> temporaryHeldCounts = null)
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;

            bool matched = _service.SyncItemSeekingQuestProgress(
                cid,
                _sender.AccountId,
                itemFilter,
                temporaryHeldCounts);
            if (!matched)
                return;

            var noti = BuildAcceptedQuestNoti(cid);
            await _sender.SendNotiAsync(0x023F, noti);
        }

        public void RecalibrateItemSeekingQuestProgressWithoutNotification(
            ICollection<int> itemFilter)
        {
            var cid = _sender.CharacterId;
            if (cid <= 0)
                return;

            _service.SyncItemSeekingQuestProgress(
                cid,
                _sender.AccountId,
                itemFilter);
        }

        public async Task SyncClearMapQuestProgressAsync(int dungeonId, int mapId)
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;

            bool changed = _service.SyncClearMapQuestProgress(cid, dungeonId, mapId);
            if (!changed)
                return;

            var noti = BuildAcceptedQuestNoti(cid);
            await _sender.SendNotiAsync(0x023F, noti);
        }

        private bool TryBuildDeferredClearMapSetTrigger(int characterId, byte[] qBody, out QuestSetTriggerResult result)
        {
            result = null;
            if (qBody == null || qBody.Length < 3)
                return false;

            var run = _sender.Player?.CurrentRun;
            if (run == null || run.DungeonId <= 0)
                return false;

            ushort questId = BitConverter.ToUInt16(qBody, 0);
            byte triggerType = qBody[2];
            bool isIncrement = qBody.Length >= 4 && qBody[3] != 0;
            if (!ShouldDeferQuestConnectedStartMapSetTrigger(
                    questId,
                    triggerType,
                    isIncrement,
                    run.Phase >= Dungeon.DungeonRunPhase.Cleared,
                    run.MazeQuestConnected,
                    run.MazeStartMapId))
                return false;

            var active = QuestService.LoadActiveQuests(_connStr, characterId);
            var quest = QuestService.FindByQuestId(active, questId);
            if (quest == null || quest.TriggerValue == 0)
                return false;

            result = new QuestSetTriggerResult
            {
                QuestId = questId,
                PreviousTriggerValue = quest.TriggerValue,
                TriggerValue = quest.TriggerValue,
            };
            FileLogger.Log($"[QuestManager] SET_TRIGGER deferred clear-map start target: cid={characterId} quest={questId} trigger={quest.TriggerValue} dungeon={run.DungeonId} maze={run.MazeIndex} map={run.MazeStartMapId}");
            return true;
        }

        internal static bool ShouldDeferQuestConnectedStartMapSetTrigger(
            ushort questId,
            byte triggerType,
            bool isIncrement,
            bool dungeonCleared,
            bool mazeQuestConnected,
            int mazeStartMapId)
        {
            if (questId == 0 || dungeonCleared || !mazeQuestConnected || mazeStartMapId <= 0)
                return false;
            if (triggerType != 0 || isIncrement)
                return false;

            return ShouldDeferQuestConnectedStartMapQuest(questId, mazeStartMapId);
        }

        internal bool HasDeferredQuestConnectedStartMapClearQuest(int characterId, int mazeStartMapId)
        {
            if (characterId <= 0 || mazeStartMapId <= 0)
                return false;

            var active = QuestService.LoadActiveQuests(_connStr, characterId);
            foreach (var quest in active)
            {
                if (quest.TriggerValue == 0)
                    continue;
                if (ShouldDeferQuestConnectedStartMapQuest(quest.QuestId, mazeStartMapId))
                    return true;
            }

            return false;
        }

        private static bool ShouldDeferQuestConnectedStartMapQuest(ushort questId, int mazeStartMapId)
        {
            var qst = GameWorld.QuestData.GetQuestFile(questId);
            if (qst == null || qst.CompleteNpcIndex < 0)
                return false;

            return GameWorld.QuestData.MatchesClearMapTarget(qst, dungeonId: 0, mapId: mazeStartMapId);
        }

        public async Task SendAcceptableQuestListAsync()
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var character = _sender.Player;
            int level = character != null ? character.Level : 1;
            int job = character != null ? character.Job : 0;
            int growType = character != null ? character.GrowType : -1;

            var clearedFlags = new QuestRepository(_connStr).LoadClearedFlags(cid);
            var allowedCreatureKinds = InventoryContext.TryGetLease(cid, out var lease)
                ? PetCreatureEvolutionRuntimeService.LoadEligiblePetCreatureEvolutionQuestKinds(lease.Inventory)
                : new HashSet<int>();
            await _sender.SendNotiAsync(
                0x0015,
                QuestListBodyBuilder.BuildBody(level, job, growType, clearedFlags, allowedCreatureKinds));
        }

        private async Task SendUserInfoBroadcast(int characterId, HonorLevelSummary honorLevel = null)
        {
            try
            {
                var record = _characterRepository.GetById(characterId);
                var addition = SqliteSubtype1Repository.FromConnectionString(_connStr).Load(characterId);

                if (record != null && addition != null)
                {
                    var accountCharacters = _characterRepository.ListByAccount(record.AccountId);
                    honorLevel = honorLevel
                        ?? _honorLevel.LoadSummary(record.AccountId, accountCharacters);
                    Characters.CharacterStatComputer.DecodeGrowType(record.GrowType, out var fGrow2, out var sGrow2);
                    var synced = SkillStateService.LoadAndSync(
                        _progressRepository, characterId, record.Job, record.Level, record.BonusSp, record.BonusTp, persist: false,
                        growType: fGrow2, secondGrowType: sGrow2);
                    await _sender.SendNotiAsync(0x0002,
                        Network.Handlers.UserInfoBroadcastService.BuildSubtype1Body(
                            record, addition, accountCharacters, honorLevel, synced.Skills));
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestManager] SendUserInfoBroadcast ERROR: {ex.Message}");
            }
        }

        // 广播对象固定是当前会话角色(_sender.Player), 不接受外部 cid。
        private async Task SendUserInfoSubtype0Broadcast(
            string reason,
            HonorLevelSummary honorLevel = null)
        {
            var sent = await Network.Handlers.UserInfoBroadcastService.SendSubtype0Async(
                _sender.Player,
                _sender.AccountId,
                body => _sender.SendNotiAsync(0x0002, body),
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                "QuestManager subtype0",
                honorLevel);
            if (sent)
                FileLogger.Log($"[QuestManager] {reason} NOTI 2 subtype0 sent: cid={_sender.CharacterId}");
        }

        // 会话内重发全量技能表(NOTI 0x0013), 客户端整体替换技能状态。
        private async Task SendSkillInfoRefreshAsync(int characterId)
        {
            try
            {
                var dataSource = new SqliteSelectCharacterDataSource(
                    _databasePath, ServerPaths.SchemaFilePath, _characterRepository);
                var snapshot = dataSource.Load(characterId, _sender.AccountId);
                var skillBytes = SkillInfoBodyBuilder.BuildFrom(snapshot.InitializationSnapshot.SkillInfo);
                await _sender.SendNotiAsync(0x0013, skillBytes);
                FileLogger.Log($"[QuestManager] JobChange skill info refresh sent: cid={characterId} len={skillBytes.Length}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestManager] SendSkillInfoRefresh ERROR: {ex.Message}");
            }
        }

        private async Task SendJobChangeNotification(
            int characterId,
            HonorLevelSummary honorLevel = null)
        {
            try
            {
                var record = _characterRepository.GetById(characterId);
                if (record == null) return;
                record.Subtype0Tail = new SqliteSubtype0FieldsRepository(_databasePath, ServerPaths.SchemaFilePath)
                    .Load(characterId)
                    ?? new UserInfoMinimumTailSnapshot();
                honorLevel = honorLevel
                    ?? _honorLevel.LoadSummary(record.AccountId);
                HonorLevelDataProvider.ApplyToSubtype0Tail(record.Subtype0Tail, honorLevel);

                _sender.Player.GrowType = record.GrowType;

                await _sender.SendNotiAsync(0x0002, UserInfoSubtype0Builder.BuildNotificationBody(record));

                FileLogger.Log($"[QuestManager] JobChange NOTI 2 subtype0 sent: cid={characterId} growType=0x{record.GrowType:X2}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestManager] SendJobChangeNotification ERROR: {ex.Message}");
            }
        }

        private async Task SendExpertJobChangeNotification(
            int characterId,
            int expertJobType,
            HonorLevelSummary honorLevel = null)
        {
            try
            {
                var record = _characterRepository.GetById(characterId);
                if (record == null || _sender.Player == null) return;

                var tail = new SqliteSubtype0FieldsRepository(_databasePath, ServerPaths.SchemaFilePath)
                    .Load(characterId)
                    ?? _sender.Player.Subtype0Tail
                    ?? new UserInfoMinimumTailSnapshot();
                tail.ExpertJobType = (byte)expertJobType;
                _sender.Player.Subtype0Tail = tail;
                honorLevel = honorLevel
                    ?? _honorLevel.LoadSummary(record.AccountId);
                HonorLevelDataProvider.ApplyToSubtype0Tail(tail, honorLevel);
                record.Subtype0Tail = tail;

                // NOTI 0x00CD ExpertJobInfo
                var ejw = new Network.GamePacketWriter();
                ejw.WriteByte(1);          // State0
                ejw.WriteByte(1);          // Mode
                ejw.WriteByte(1);          // Count
                ejw.WriteInt32(expertJobType);
                await _sender.SendNotiAsync(0x00CD, ejw.ToArray());

                // NOTI 0x0002 subtype 0 USERINFO Minimum
                var w = new Network.GamePacketWriter();
                w.WriteByte(0);
                w.WriteUInt16(1);
                w.WriteUInt16((ushort)record.CharacterId);
                w.WriteDstr(record.Name);
                w.WriteBytes(UserInfoSubtype0Builder.BuildRemainingBytes(record));
                await _sender.SendNotiAsync(0x0002, w.ToArray());

                FileLogger.Log($"[QuestManager] ExpertJobChange NOTI sent: cid={characterId} expertJobType={expertJobType}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestManager] SendExpertJobChangeNotification ERROR: {ex.Message}");
            }
        }

        private HonorLevelSummary ResolveHonorLevelForExp()
        {
            var tail = _sender.Player?.Subtype0Tail;
            if (tail != null)
            {
                return new HonorLevelSummary
                {
                    HonorLevel = (byte)Math.Min(byte.MaxValue, tail.ProgressA),
                    HonorExp = tail.ProgressB,
                };
            }

            return _honorLevel.LoadSummary(_sender.AccountId);
        }

        private byte[] BuildAcceptedQuestNoti(int characterId)
        {
            var active = QuestService.LoadActiveQuests(_connStr, characterId);
            var w = new Network.GamePacketWriter();
            w.WriteUInt32((uint)active.Count);
            foreach (var q in active)
            {
                w.WriteUInt16(q.QuestId);
                w.WriteUInt32(q.TriggerValue);
            }
            return w.ToArray();
        }
    }
}
