using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;
using System;

namespace DfoServer.Network.Builders
{
    
    
    
    
    
    
    
    
    
    
    public sealed class UserInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0002;

        /// <summary>
        /// 将 subtype1 头部的角色键改写为城镇对象使用的 UserId。
        /// 仅接受已验证的 subtype1 头部，避免把未知包体当作可变字段写坏。
        /// </summary>
        /// <param name="body">已经构造出的 subtype1 包体。</param>
        /// <param name="userId">需要写入的城镇对象 UserId。</param>
        /// <returns>包体头部符合 subtype1 布局且改写成功时返回 true。</returns>
        internal static bool TryRewriteUserId(byte[] body, ushort userId)
        {
            if (body == null || body.Length < 5
                || body[0] != 1 || body[1] != 1 || body[2] != 0)
                return false;

            Buffer.BlockCopy(BitConverter.GetBytes(userId), 0, body, 3, sizeof(ushort));
            return true;
        }

        /// <summary>
        /// 将 subtype0 包头中的角色键改写为城镇名册使用的 UserId。
        /// subtype0 与 subtype1 共用前五字节布局，但必须单独校验 subtype。
        /// </summary>
        /// <param name="body">已经构造出的 subtype0 包体。</param>
        /// <param name="userId">需要写入的城镇对象 UserId。</param>
        /// <returns>包体头部符合 subtype0 布局且改写成功时返回 true。</returns>
        internal static bool TryRewriteSubtype0UserId(byte[] body, ushort userId)
        {
            if (body == null || body.Length < 5
                || body[0] != 0 || body[1] != 1 || body[2] != 0)
                return false;

            Buffer.BlockCopy(BitConverter.GetBytes(userId), 0, body, 3, sizeof(ushort));
            return true;
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var c = snapshot.CharacterRecord;
            if (c == null) { body = null; return false; }

            if (occurrenceIndex == 1)
            {
                var addition = snapshot.InitializationSnapshot.UserInfoAddition;
                if (addition == null)
                {
                    DfoServer.FileLogger.Log("[UserInfoBodyBuilder] ERROR: occ1 UserInfoAddition is null — 结构化表未迁移。不兜底 blob。");
                    body = null; return false;
                }
                var w = new GamePacketWriter();
                w.WriteByte(1); w.WriteUInt16(1);
                w.WriteUInt16((ushort)c.CharacterId);
                w.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(
                    addition, snapshot.InitializationSnapshot.SkillInfo));
                body = w.ToArray(); return true;
            }

            if (occurrenceIndex == 0 || occurrenceIndex == 2)
            {
                body = UserInfoSubtype0Builder.BuildNotificationBody(c);
                return true;
            }

            
            DfoServer.FileLogger.Log($"[UserInfoBodyBuilder] ERROR: 不支持的 occurrence {occurrenceIndex} — init 流只有 occ0/1/2。");
            body = null;
            return false;
        }
    }
}
