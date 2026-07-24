using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    public sealed class SqliteCharacterStateRepository : ICharacterStateRepository
    {
        private readonly string _connectionString;
        private readonly CharacterAchievementRepository _achievement;
        private readonly CharacterItemValueRepository _itemValue;
        private readonly CharacterMiscStateRepository _miscState;

        public SqliteCharacterStateRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            _achievement = new CharacterAchievementRepository(_connectionString);
            _itemValue = new CharacterItemValueRepository(_connectionString);
            _miscState = new CharacterMiscStateRepository(_connectionString);
        }



        public void LoadFlags(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    @"SELECT pc_room_state, expert_job_blob,
                             champion_break_key_id, champion_break_mode, champion_break_value,
                             character_option_blob, charac_invisible_falgs_payload_len,
                             racing_dungeon_current_enter_count,
                             ack_char_slot_index, ack_fatigue_battery, ack_fatigue_grownup_buff,
                             ack_trade_punish_flag, ack_extra_field_86jp,
                             ack_tutorial_skipable
                      FROM character_init_flags WHERE character_id = @cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return;
                        snapshot.PcRoomPlayTimeState = (byte)reader.GetInt32(0);

                        var expertBlob = reader.IsDBNull(1) ? null : (byte[])reader[1];
                        if (expertBlob != null)
                            DeserializeExpertJobInfo(expertBlob, snapshot.ExpertJobInfo);

                        snapshot.ChampionBreakSystem.KeyId = reader.GetInt32(2);
                        snapshot.ChampionBreakSystem.Mode = (byte)reader.GetInt32(3);
                        snapshot.ChampionBreakSystem.Value = reader.GetInt32(4);

                        snapshot.CharacterOptionBlob = reader.IsDBNull(5) ? null : (byte[])reader[5];
                        snapshot.CharacInvisibleFalgsPayloadLen = reader.IsDBNull(6) ? 0u : (uint)reader.GetInt64(6);
                        snapshot.RacingDungeonCurrentEnterCount = reader.IsDBNull(7) ? 0u : (uint)reader.GetInt64(7);

                        snapshot.AckCharSlotIndex = reader.IsDBNull(8) ? (byte)0 : (byte)reader.GetInt32(8);
                        snapshot.AckFatigueBattery = reader.IsDBNull(9) ? (ushort)0 : (ushort)reader.GetInt32(9);
                        snapshot.AckFatigueGrownUpBuff = reader.IsDBNull(10) ? (ushort)0 : (ushort)reader.GetInt32(10);
                        snapshot.AckTradePunishFlag = reader.IsDBNull(11) ? (byte)0 : (byte)reader.GetInt32(11);
                        snapshot.AckExtraField86JP = reader.IsDBNull(12) ? (ushort)0 : (ushort)reader.GetInt32(12);
                        snapshot.AckTutorialSkipable = reader.IsDBNull(13) ? (byte)0 : (byte)reader.GetInt32(13);
                    }
                }

                snapshot.GrowthWeaponStageIds.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT stage_id FROM character_growth_weapon_stages WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.GrowthWeaponStageIds.Add((byte)reader.GetInt32(0));
                    }
                }



                snapshot.PvpMissions.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT mission_id, progress_value FROM character_pvp_missions WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshot.PvpMissions.Add(new PvpMissionEntrySnapshot
                            {
                                MissionId = (uint)reader.GetInt64(0),
                                ProgressValue = (uint)reader.GetInt64(1),
                            });
                        }
                    }
                }

                snapshot.DungeonPermissions.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT dungeon_id, clear_state FROM character_dungeon_permissions WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshot.DungeonPermissions.Add(new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = (ushort)reader.GetInt32(0),
                                ClearState = (byte)reader.GetInt32(1),
                            });
                        }
                    }
                }

                snapshot.HotkeyConfigSlots.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT hotkey_value FROM character_hotkey_slots WHERE character_id = @cid ORDER BY slot_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.HotkeyConfigSlots.Add((ushort)reader.GetInt32(0));
                    }
                }

                snapshot.CharacInvisibleFalgs.Clear();
                foreach (var entry in Game.Quests.QuestRepository.LoadAllFlagEntries(conn, null, characterId))
                {
                    snapshot.CharacInvisibleFalgs.Add(new CharacInvisibleFalgEntrySnapshot
                    {
                        SlotIndex = (ushort)entry.Key,
                        FlagValue = (byte)entry.Value,
                    });
                }

                snapshot.RacingDungeonGroups.Clear();
                var racingGroupsByIndex = new Dictionary<int, RacingDungeonGroupSnapshot>();
                using (var cmd = new SqliteCommand(
                    "SELECT group_index, group_id FROM character_daily_challenge_groups WHERE character_id = @cid ORDER BY group_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var groupIndex = reader.GetInt32(0);
                            var group = new RacingDungeonGroupSnapshot { GroupId = (uint)reader.GetInt64(1) };
                            racingGroupsByIndex[groupIndex] = group;
                            snapshot.RacingDungeonGroups.Add(group);
                        }
                    }
                }
                using (var cmd = new SqliteCommand(
                    "SELECT group_index, entry_index, track_like_id, value_a, value_b FROM character_daily_challenge_entries WHERE character_id = @cid ORDER BY group_index, entry_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var groupIndex = reader.GetInt32(0);
                            if (!racingGroupsByIndex.TryGetValue(groupIndex, out var group))
                                continue;
                            group.Entries.Add(new RacingDungeonEntrySnapshot
                            {
                                TrackLikeId = (uint)reader.GetInt64(2),
                                ValueA = (uint)reader.GetInt64(3),
                                ValueB = (uint)reader.GetInt64(4),
                            });
                        }
                    }
                }

                snapshot.RacingDungeonTailIds.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT id_value FROM character_daily_challenge_tail_ids WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.RacingDungeonTailIds.Add((uint)reader.GetInt64(0));
                    }
                }
            }
        }

        public bool UpsertDungeonPermission(int characterId, int dungeonId, byte newClearState)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                int currentState = 0;
                using (var cmd = new SqliteCommand(
                    "SELECT clear_state FROM character_dungeon_permissions WHERE character_id = @cid AND dungeon_id = @did", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@did", dungeonId);
                    var existing = cmd.ExecuteScalar();
                    if (existing != null && existing != DBNull.Value)
                        currentState = Convert.ToInt32(existing);
                }
                if (currentState >= newClearState) return false;

                if (currentState > 0)
                {
                    using (var cmd = new SqliteCommand(
                        "UPDATE character_dungeon_permissions SET clear_state = @cs WHERE character_id = @cid AND dungeon_id = @did", conn))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@did", dungeonId);
                        cmd.Parameters.AddWithValue("@cs", (int)newClearState);
                        cmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    using (var cmd = new SqliteCommand(@"
INSERT INTO character_dungeon_permissions (character_id, sort_order, dungeon_id, clear_state)
VALUES (@cid, (SELECT COALESCE(MAX(sort_order),0)+1 FROM character_dungeon_permissions WHERE character_id=@cid), @did, @cs)", conn))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@did", dungeonId);
                        cmd.Parameters.AddWithValue("@cs", (int)newClearState);
                        cmd.ExecuteNonQuery();
                    }
                }
                return true;
            }
        }

        public List<DungeonPermissionEntrySnapshot> LoadDungeonPermissions(
            int characterId)
        {
            var result = new List<DungeonPermissionEntrySnapshot>();
            if (characterId <= 0)
                return result;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    @"SELECT dungeon_id, clear_state
                      FROM character_dungeon_permissions
                      WHERE character_id = @cid
                      ORDER BY sort_order",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = (ushort)reader.GetInt32(0),
                                ClearState = (byte)reader.GetInt32(1),
                            });
                        }
                    }
                }
            }

            return result;
        }

        public void SaveFlags(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand(
                        @"INSERT INTO character_init_flags
                          (character_id, pc_room_state, expert_job_blob,
                           champion_break_key_id, champion_break_mode, champion_break_value,
                           character_option_blob, charac_invisible_falgs_payload_len,
                           racing_dungeon_current_enter_count,
                           ack_char_slot_index, ack_fatigue_battery, ack_fatigue_grownup_buff,
                           ack_trade_punish_flag, ack_extra_field_86jp,
                           ack_tutorial_skipable)
                          VALUES (@cid, @pcr, @expert,
                                  @champKey, @champMode, @champValue,
                                  @charOpt, @ciplen,
                                  @rdcc,
                                  @ackSlot, @ackFatBat, @ackFatGrown,
                                  @ackTrade, @ackExtra86,
                                  @ackTutSkip)
                          ON CONFLICT(character_id) DO UPDATE SET
                            pc_room_state=excluded.pc_room_state,
                            expert_job_blob=excluded.expert_job_blob,
                            champion_break_key_id=excluded.champion_break_key_id,
                            champion_break_mode=excluded.champion_break_mode,
                            champion_break_value=excluded.champion_break_value,
                            character_option_blob=COALESCE(excluded.character_option_blob, character_init_flags.character_option_blob),
                            charac_invisible_falgs_payload_len=excluded.charac_invisible_falgs_payload_len,
                            racing_dungeon_current_enter_count=excluded.racing_dungeon_current_enter_count,
                            ack_char_slot_index=excluded.ack_char_slot_index,
                            ack_fatigue_battery=excluded.ack_fatigue_battery,
                            ack_fatigue_grownup_buff=excluded.ack_fatigue_grownup_buff,
                            ack_trade_punish_flag=excluded.ack_trade_punish_flag,
                            ack_extra_field_86jp=excluded.ack_extra_field_86jp,
                            ack_tutorial_skipable=excluded.ack_tutorial_skipable", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@pcr", (int)snapshot.PcRoomPlayTimeState);
                        cmd.Parameters.AddWithValue("@expert", SerializeExpertJobInfo(snapshot.ExpertJobInfo));
                        cmd.Parameters.AddWithValue("@champKey", snapshot.ChampionBreakSystem.KeyId);
                        cmd.Parameters.AddWithValue("@champMode", (int)snapshot.ChampionBreakSystem.Mode);
                        cmd.Parameters.AddWithValue("@champValue", snapshot.ChampionBreakSystem.Value);
                        cmd.Parameters.AddWithValue("@charOpt", (object)snapshot.CharacterOptionBlob ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ciplen", (long)snapshot.CharacInvisibleFalgsPayloadLen);
                        cmd.Parameters.AddWithValue("@rdcc", (long)snapshot.RacingDungeonCurrentEnterCount);
                        cmd.Parameters.AddWithValue("@ackSlot", (int)snapshot.AckCharSlotIndex);
                        cmd.Parameters.AddWithValue("@ackFatBat", (int)snapshot.AckFatigueBattery);
                        cmd.Parameters.AddWithValue("@ackFatGrown", (int)snapshot.AckFatigueGrownUpBuff);
                        cmd.Parameters.AddWithValue("@ackTrade", (int)snapshot.AckTradePunishFlag);
                        cmd.Parameters.AddWithValue("@ackExtra86", (int)snapshot.AckExtraField86JP);
                        cmd.Parameters.AddWithValue("@ackTutSkip", (int)snapshot.AckTutorialSkipable);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_growth_weapon_stages WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var stages = snapshot.GrowthWeaponStageIds;
                    for (int i = 0; i < stages.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_growth_weapon_stages (character_id, sort_order, stage_id) VALUES (@cid, @ord, @sid)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@sid", (int)stages[i]);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_pvp_missions WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var missions = snapshot.PvpMissions;
                    for (int i = 0; i < missions.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_pvp_missions (character_id, sort_order, mission_id, progress_value) VALUES (@cid, @ord, @mid, @pv)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@mid", (long)missions[i].MissionId);
                            cmd.Parameters.AddWithValue("@pv", (long)missions[i].ProgressValue);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_dungeon_permissions WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var dungeons = snapshot.DungeonPermissions;
                    for (int i = 0; i < dungeons.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_dungeon_permissions (character_id, sort_order, dungeon_id, clear_state) VALUES (@cid, @ord, @did, @cs)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@did", (int)dungeons[i].DungeonId);
                            cmd.Parameters.AddWithValue("@cs", (int)dungeons[i].ClearState);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    ReplaceHotkeySlots(conn, tx, characterId, snapshot.HotkeyConfigSlots);

                    Game.Quests.QuestRepository.ReplaceAllClearedFlags(conn, tx, characterId,
                        snapshot.CharacInvisibleFalgs.ConvertAll(
                            entry => new KeyValuePair<int, int>(entry.SlotIndex, entry.FlagValue)));
                    using (var cmd = new SqliteCommand("DELETE FROM character_daily_challenge_groups WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new SqliteCommand("DELETE FROM character_daily_challenge_entries WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new SqliteCommand("DELETE FROM character_daily_challenge_tail_ids WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var racingGroups = snapshot.RacingDungeonGroups;
                    for (int i = 0; i < racingGroups.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_daily_challenge_groups (character_id, group_index, group_id) VALUES (@cid, @gi, @gid)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@gi", i);
                            cmd.Parameters.AddWithValue("@gid", (long)racingGroups[i].GroupId);
                            cmd.ExecuteNonQuery();
                        }
                        var entries = racingGroups[i].Entries;
                        for (int j = 0; j < entries.Count; j++)
                        {
                            using (var cmd = new SqliteCommand(
                                "INSERT INTO character_daily_challenge_entries (character_id, group_index, entry_index, track_like_id, value_a, value_b) VALUES (@cid, @gi, @ei, @tid, @va, @vb)", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@cid", characterId);
                                cmd.Parameters.AddWithValue("@gi", i);
                                cmd.Parameters.AddWithValue("@ei", j);
                                cmd.Parameters.AddWithValue("@tid", (long)entries[j].TrackLikeId);
                                cmd.Parameters.AddWithValue("@va", (long)entries[j].ValueA);
                                cmd.Parameters.AddWithValue("@vb", (long)entries[j].ValueB);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    var tailIds = snapshot.RacingDungeonTailIds;
                    for (int i = 0; i < tailIds.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_daily_challenge_tail_ids (character_id, sort_order, id_value) VALUES (@cid, @ord, @v)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@v", (long)tailIds[i]);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }
            }
        }

        public void SaveCharacterOption(int characterId, byte[] body)
        {
            if (characterId <= 0 || body == null)
                return;

            var copy = new byte[body.Length];
            Buffer.BlockCopy(body, 0, copy, 0, body.Length);

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(@"
INSERT INTO character_init_flags (character_id, character_option_blob)
VALUES (@cid, @body)
ON CONFLICT(character_id) DO UPDATE SET character_option_blob = @body", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@body", copy);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void SaveMoodValue(int characterId, ushort moodValue)
        {
            if (characterId <= 0)
                return;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(@"
INSERT INTO character_subtype0_fields (character_id, mood_value)
VALUES (@cid, @mood)
ON CONFLICT(character_id) DO UPDATE SET
    mood_value = @mood", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@mood", (int)moodValue);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void SaveHotkeyConfig(int characterId, byte[] hotkeys)
        {
            if (characterId <= 0 || hotkeys == null)
                return;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    ReplaceHotkeySlots(conn, tx, characterId, DecodeHotkeySlots(hotkeys));
                    tx.Commit();
                }
            }
        }

        private static List<ushort> DecodeHotkeySlots(byte[] hotkeys)
        {
            var slots = new List<ushort>();
            if (hotkeys == null)
                return slots;

            for (var offset = 0; offset + 1 < hotkeys.Length; offset += 2)
                slots.Add(BitConverter.ToUInt16(hotkeys, offset));
            return slots;
        }

        private static void ReplaceHotkeySlots(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            IReadOnlyList<ushort> slots)
        {
            using (var cmd = new SqliteCommand("DELETE FROM character_hotkey_slots WHERE character_id = @cid", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }

            slots = slots ?? Array.Empty<ushort>();
            for (var i = 0; i < slots.Count; i++)
            {
                using (var cmd = new SqliteCommand(
                    "INSERT INTO character_hotkey_slots (character_id, slot_index, hotkey_value) VALUES (@cid, @si, @hv)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@si", i);
                    cmd.Parameters.AddWithValue("@hv", (int)slots[i]);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public bool HasFlags(int characterId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM character_init_flags WHERE character_id = @cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
        }



        public void SeedFromSnapshot(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            if (!HasFlags(characterId))
                SaveFlags(characterId, snapshot);

            _itemValue.SaveItemValueListIfEmpty(characterId, "cooltime", snapshot.CooltimeItems);
            _itemValue.SaveItemValueListIfEmpty(characterId, "effect", snapshot.EffectItems);

            if (_achievement.LoadAchievementComplete(characterId).Entries.Count == 0 && snapshot.AchievementComplete.Entries.Count > 0)
                _achievement.SaveAchievementComplete(characterId, snapshot.AchievementComplete);

            if (_miscState.LoadUnknown725(characterId).Count == 0 && snapshot.Unknown725Packets.Count > 0)
                _miscState.SaveUnknown725(characterId, snapshot.Unknown725Packets);

            if (_miscState.LoadUnknown730(characterId).Entries.Count == 0 && snapshot.Unknown730.Entries.Count > 0)
                _miscState.SaveUnknown730(characterId, snapshot.Unknown730);
        }

        public void LoadAll(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            LoadFlags(characterId, snapshot);

            var cooltime = _itemValue.LoadItemValueList(characterId, "cooltime");
            snapshot.CooltimeItems.Clear();
            snapshot.CooltimeItems.AddRange(cooltime);

            var effect = _itemValue.LoadItemValueList(characterId, "effect");
            snapshot.EffectItems.Clear();
            snapshot.EffectItems.AddRange(effect);

            snapshot.AchievementComplete = _achievement.LoadAchievementComplete(characterId);

            var u725 = _miscState.LoadUnknown725(characterId);
            snapshot.Unknown725Packets.Clear();
            snapshot.Unknown725Packets.AddRange(u725);

            snapshot.Unknown730 = _miscState.LoadUnknown730(characterId);
        }






        private static byte[] SerializeExpertJobInfo(ExpertJobInfoSnapshot info)
        {
            var list = new List<byte>();
            list.Add(info.State0);
            list.Add(info.Mode);
            list.AddRange(BitConverter.GetBytes(info.ValueA));
            list.AddRange(BitConverter.GetBytes(info.ValueB));
            list.Add((byte)info.Entries.Count);
            foreach (var entry in info.Entries)
                list.AddRange(BitConverter.GetBytes(entry));
            return list.ToArray();
        }

        private static void DeserializeExpertJobInfo(byte[] blob, ExpertJobInfoSnapshot info)
        {
            if (blob.Length < 2) return;
            info.State0 = blob[0];
            info.Mode = blob[1];
            int offset = 2;
            if (offset + 8 <= blob.Length)
            {
                info.ValueA = BitConverter.ToInt32(blob, offset); offset += 4;
                info.ValueB = BitConverter.ToInt32(blob, offset); offset += 4;
            }
            if (offset < blob.Length)
            {
                var count = blob[offset++];
                info.Entries.Clear();
                for (int i = 0; i < count && offset + 4 <= blob.Length; i++)
                {
                    info.Entries.Add(BitConverter.ToInt32(blob, offset));
                    offset += 4;
                }
            }
        }

    }
}
