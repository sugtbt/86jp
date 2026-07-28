using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Dungeon
{
    internal static class EquipmentDropPoolProvider
    {
        private static readonly object LockObj = new object();
        private static Dictionary<long, List<(int Id, int Weight)>> _pool;
        private static Dictionary<long, List<(int Id, int Weight)>> _normalPool;
        private static Dictionary<long, List<(int Id, int Weight)>> _avatarPool;
        private static bool _loaded;

        internal static Dictionary<long, List<(int Id, int Weight)>> GetPool()
        {
            EnsureLoaded();
            return _pool;
        }

        internal static Dictionary<long, List<(int Id, int Weight)>>
            GetClearRewardPool(bool avatar)
        {
            EnsureLoaded();
            return avatar ? _avatarPool : _normalPool;
        }

        internal static void WarmUp()
        {
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (LockObj)
            {
                if (_loaded) return;
                _pool = LoadEquipmentPool();
                _loaded = true;
            }
        }

        private static Dictionary<long, List<(int Id, int Weight)>> LoadEquipmentPool()
        {
            var pool = new Dictionary<long, List<(int Id, int Weight)>>();
            _normalPool = new Dictionary<long, List<(int Id, int Weight)>>();
            _avatarPool = new Dictionary<long, List<(int Id, int Weight)>>();
            try
            {
                var equipmentListText = GameWorld.PvfArchiveAccessor.ReadText("equipment/equipment.lst");
                var equipmentList = LstFile.Parse(equipmentListText);
                if (equipmentList == null || equipmentList.Entries.Count == 0)
                {
                    FileLogger.Log("[EquipmentDropPoolProvider] equipment.lst empty/not found");
                    return pool;
                }

                var added = 0;
                var errors = 0;
                for (var i = 0; i < equipmentList.Entries.Count; i++)
                {
                    var entry = equipmentList.Entries[i];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                        continue;

                    try
                    {
                        var equipment = EquipmentFile.Parse(GameWorld.PvfArchiveAccessor.ReadText(Path.Combine("equipment", entry.FilePath)));
                        if (TryAddEquipment(
                                pool,
                                _normalPool,
                                _avatarPool,
                                entry.Id,
                                entry.FilePath,
                                equipment))
                            added++;
                    }
                    catch
                    {
                        errors++;
                    }
                }

                FileLogger.Log(
                    $"[EquipmentDropPoolProvider] equipment pool from .equ: " +
                    $"items={added} errors={errors} buckets={pool.Count} " +
                    $"normalBuckets={_normalPool.Count} avatarBuckets={_avatarPool.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[EquipmentDropPoolProvider] equipment pool parse error: {ex.Message}");
            }

            return pool;
        }

        private static bool TryAddEquipment(
            Dictionary<long, List<(int Id, int Weight)>> pool,
            Dictionary<long, List<(int Id, int Weight)>> normalPool,
            Dictionary<long, List<(int Id, int Weight)>> avatarPool,
            int itemId,
            string filePath,
            EquipmentFile equipment)
        {
            if (itemId <= 0 || equipment == null)
                return false;

            var rarity = equipment.Rarity;
            var grade = equipment.Grade;
            var creationRate = equipment.CreationRate;

            if (creationRate <= 0 || grade <= 0 || rarity < 0 || rarity > 5)
                return false;

            var key = (long)grade * 10 + rarity;
            AddToPool(pool, key, itemId, creationRate);
            var categoryPool = IsAvatar(filePath, equipment)
                ? avatarPool
                : normalPool;
            AddToPool(categoryPool, key, itemId, creationRate);
            return true;
        }

        private static void AddToPool(
            Dictionary<long, List<(int Id, int Weight)>> pool,
            long key,
            int itemId,
            int weight)
        {
            if (!pool.TryGetValue(key, out var list))
            {
                list = new List<(int Id, int Weight)>();
                pool[key] = list;
            }
            list.Add((itemId, weight));
        }

        private static bool IsAvatar(string filePath, EquipmentFile equipment)
        {
            var normalizedPath = "/" + (filePath ?? string.Empty)
                .Replace('\\', '/')
                .Trim('/');
            return normalizedPath.IndexOf(
                       "/avatar/",
                       StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedPath.IndexOf(
                       "/at_avatar/",
                       StringComparison.OrdinalIgnoreCase) >= 0
                || (equipment.EquipmentType?.IndexOf(
                        "avatar",
                        StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }
    }
}
