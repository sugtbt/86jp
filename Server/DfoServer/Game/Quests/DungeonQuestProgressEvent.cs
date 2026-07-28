using System;
using DfoServer.Game.Dungeon;

namespace DfoServer.Game.Quests
{
    public enum DungeonQuestProgressKind
    {
        HuntMonster = 0,
        ClearMap = 1,
        ClearDungeon = 2,
    }

    public sealed class DungeonQuestProgressEvent
    {
        private DungeonQuestProgressEvent(
            DungeonEventEnvelope envelope,
            DungeonQuestProgressKind kind,
            int dungeonId,
            int difficulty,
            int mapId,
            int monsterCode)
        {
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            Kind = kind;
            DungeonId = dungeonId;
            Difficulty = difficulty;
            MapId = mapId;
            MonsterCode = monsterCode;
        }

        public DungeonEventEnvelope Envelope { get; }
        public Guid SourceEventId => Envelope.SourceEventId;
        public DungeonQuestProgressKind Kind { get; }
        public int DungeonId { get; }
        public int Difficulty { get; }
        public int MapId { get; }
        public int MonsterCode { get; }

        public static DungeonQuestProgressEvent HuntMonster(
            DungeonEventEnvelope envelope,
            int dungeonId,
            int difficulty,
            int monsterCode) =>
            new DungeonQuestProgressEvent(
                envelope,
                DungeonQuestProgressKind.HuntMonster,
                dungeonId,
                difficulty,
                mapId: 0,
                monsterCode);

        public static DungeonQuestProgressEvent ClearMap(
            DungeonEventEnvelope envelope,
            int dungeonId,
            int mapId) =>
            new DungeonQuestProgressEvent(
                envelope,
                dungeonId > 0
                    ? DungeonQuestProgressKind.ClearDungeon
                    : DungeonQuestProgressKind.ClearMap,
                dungeonId,
                difficulty: 0,
                mapId,
                monsterCode: 0);
    }
}
