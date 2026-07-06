using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Builds the shared 0x01A action packet used by both combat and magic.
/// Layout: hdr(4) UniqueNo(target)@4 ActIndex@8 ActionID(category)@10 union@12.
/// 28 bytes = 7 words (PacketSize[0x01A]=0x0E).
internal static class ActionPacket
{
    public const ushort Talk = 0x00, CastMagic = 0x03, Attack = 0x02, AttackOff = 0x04, Weaponskill = 0x07, JobAbility = 0x09, HomepointMenu = 0x0B;

    public static byte[] Build(ushort actionId, uint target, ushort actIndex = 0, uint buf0 = 0)
    {
        var p = new byte[28];
        SubPacket.WriteHeader(p, 0x01A);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), target);   // UniqueNo
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), actIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(10), actionId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), buf0);    // union[0] (SpellId / WS id / ability id)
        return p;
    }
}

/// Builds 0x0DD GP_CLI_COMMAND_EQUIP_INSPECT (/check). hdr(4) UniqueNo@4 ActIndex@8(u32) Kind@12.
/// 16 bytes = 4 words. Kind=0x00 = Check (con). Server replies with a 0x029 carrying difficulty.
internal static class CheckPacket
{
    public static byte[] Build(uint mobId, uint mobIndex)
    {
        var p = new byte[16];
        SubPacket.WriteHeader(p, 0x0DD);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), mobId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), mobIndex);
        p[12] = 0x00; // Kind = Check
        return p;
    }
}

/// Builds 0x0E8 GP_CLI_COMMAND_CAMP (/heal). hdr(4) Mode@4(u32): 0=Toggle, 1=On, 2=Off. 8 bytes = 2 words
/// (PacketSize[0x0E8]=0x04). Starts/stops the resting (HP+MP regen) stance; the server refuses it while engaged.
internal static class CampPacket
{
    public static byte[] Build(uint mode)
    {
        var p = new byte[8];
        SubPacket.WriteHeader(p, 0x0E8);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), mode);
        return p;
    }
}
