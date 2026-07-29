using System;
using DfoServer.Game.ExpertJob;

namespace DfoServer.Network.Builders.ExpertJob
{
    internal static class ExpertJobStorePacketBuilder
    {
        internal static byte[] BuildCreateNotification(ExpertJobStoreSession store)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(store.OwnerUserId);
            writer.WriteDstr(store.NameBytes);
            writer.WriteByte(store.TownId);
            writer.WriteByte(store.AreaId);
            writer.WriteInt16(store.PositionX);
            writer.WriteInt16(store.PositionY);
            writer.WriteInt32(store.Cost);
            return writer.ToArray();
        }

        internal static byte[] BuildCloseNotification(ushort ownerUserId)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(ownerUserId);
            return writer.ToArray();
        }

        internal static byte[] BuildEnterSuccess(ExpertJobStoreSession store)
        {
            if (store?.Kind != ExpertJobStoreKind.DisjointMachine
                || store.DisjointMachine == null)
            {
                throw new ArgumentException("a disjoint machine store is required", nameof(store));
            }

            var machine = store.DisjointMachine;
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte((byte)store.Kind);
            writer.WriteByte(machine.MachineGrade);
            writer.WriteInt32(store.Cost);
            writer.WriteInt32(machine.Endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildDisjointSuccess(DisjointMachineOperationResult result)
        {
            var writer = new GamePacketWriter();
            var disjoint = result.DisjointResult;
            writer.WriteByte(1);
            writer.WriteInt16(disjoint.Request.TargetSlotIndex);
            writer.WriteByte((byte)disjoint.Request.ItemSpace);
            writer.WriteByte((byte)Math.Min(byte.MaxValue, disjoint.Materials.Count));
            for (var index = 0; index < disjoint.Materials.Count && index < byte.MaxValue; index++)
            {
                var material = disjoint.Materials[index];
                writer.WriteInt16(material.SlotIndex);
                writer.WriteInt32(material.ItemTemplateId);
                writer.WriteInt32(material.Count);
            }
            writer.WriteInt32(result.RequesterGold);
            writer.WriteInt32(result.Endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildDisjointError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }

        internal static byte[] BuildOwnerDisjointNotification(
            int ownerGold,
            int endurance)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(ownerGold);
            writer.WriteInt32(endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildRepairNotification(int gold, int endurance)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(gold);
            writer.WriteInt32(endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildRepairError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }

        internal static byte[] BuildUpgradeNotification(
            int gold,
            int grade,
            int endurance)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(gold);
            writer.WriteInt32(grade);
            writer.WriteInt32(endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildUpgradeError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }
    }
}
