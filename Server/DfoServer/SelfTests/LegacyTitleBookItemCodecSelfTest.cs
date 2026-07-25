using System;
using DfoServer.Game.TitleBook;

namespace DfoServer.SelfTests
{
    public static class LegacyTitleBookItemCodecSelfTest
    {
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== LEGACY_TITLEBOOK_ITEM_CODEC selftest ===");

            var persisted = new byte[LegacyTitleBookItemCodec.PersistedRecordSize];
            WriteInt16(persisted, 0, 3);
            WriteInt32(persisted, 2, 400330051);
            WriteInt32(persisted, 6, 777);
            persisted[10] = 0x6D;
            WriteUInt16(persisted, 11, 1234);
            persisted[13] = 2;
            WriteInt32(persisted, 14, 0x01020304);
            persisted[18] = 7;
            persisted[19] = 2;
            WriteUInt16(persisted, 20, 0x3456);
            WriteInt32(persisted, 22, -1);
            WriteChronicle(persisted, 26);
            Buffer.BlockCopy(Sequence(37, 0x40), 0, persisted, 47, 37);
            persisted[LegacyTitleBookItemCodec.CommonNetworkSize] = 9;

            var core = LegacyTitleBookItemCodec.DecodePersistedRecord(persisted);
            Check("decode persisted base fields", core.ItemId == 400330051
                && core.Value == 777
                && core.Attr == 0x6D
                && core.Durability == 1234
                && core.SealFlag == 2);
            Check("decode persisted dynamic fields", core.EnchantCardId == 0x01020304
                && core.EnchantUpgradeCount == 7
                && core.AmplifyType == 2
                && core.AmplifyValue == 0x3456
                && core.Marker16 == -1
                && core.EquipmentLockId == 9);
            Check("decode chronicle and tail", core.ChronicleOptions.Count == 2
                && core.ChronicleOptions[1].OptionId == 0x55667788
                && core.EmblemSocketCount == 0x40
                && core.SortLockFlag == 0x64);

            var listEntry = new byte[LegacyTitleBookItemCodec.TitleBookListEntrySize];
            WriteUInt16(listEntry, 0, 12);
            WriteInt32(listEntry, 2, 400330052);
            WriteInt32(listEntry, 6, 888);
            listEntry[10] = 0x11;
            WriteUInt16(listEntry, 11, 2222);
            listEntry[13] = 1;
            WriteInt32(listEntry, 14, 0x02030405);
            listEntry[18] = 8;
            listEntry[19] = 3;
            WriteUInt16(listEntry, 20, 0x4567);

            Check("decode legacy list entry", LegacyTitleBookItemCodec.TryDecodeListEntry(listEntry, 0, out var bookIndex, out var entryCore)
                && bookIndex == 12
                && entryCore.ItemId == 400330052
                && entryCore.Value == 888
                && entryCore.Marker16 == 0);

            var snapshot = TitleBookItemProjection.ToListEntry(4, core);
            Check("project item core to protocol snapshot", snapshot.SlotIndex == 4
                && snapshot.ItemId == core.ItemId
                && snapshot.EnchantIndex == core.EnchantCardId
                && snapshot.AmplifyValue == core.AmplifyValue);

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static byte[] Sequence(int length, byte start)
        {
            var data = new byte[length];
            for (var index = 0; index < length; index++)
                data[index] = unchecked((byte)(start + index));
            return data;
        }

        private static void WriteChronicle(byte[] data, int offset)
        {
            data[offset] = 2;
            WriteInt32(data, offset + 1, 0x11223344);
            data[offset + 5] = 1;
            data[offset + 6] = 2;
            data[offset + 7] = 3;
            data[offset + 8] = 4;
            WriteInt32(data, offset + 9, 0x55667788);
            data[offset + 13] = 5;
            data[offset + 14] = 6;
            data[offset + 15] = 7;
            data[offset + 16] = 8;
        }

        private static void WriteInt16(byte[] buf, int offset, short value)
        {
            BitConverter.GetBytes(value).CopyTo(buf, offset);
        }

        private static void WriteInt32(byte[] buf, int offset, int value)
        {
            BitConverter.GetBytes(value).CopyTo(buf, offset);
        }

        private static void WriteUInt16(byte[] buf, int offset, ushort value)
        {
            BitConverter.GetBytes(value).CopyTo(buf, offset);
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
