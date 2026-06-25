using System.Buffers.Binary;

namespace XiHeadless.Capabilities;

/// Synthesis (crafting). Combine a crystal with ingredients; the server matches the recipe by the
/// crystal + ingredient item ids it finds in the given inventory slots, and produces the result
/// (+ a skill-up chance). Mats come from shops (IShop / guild shops) — a separate capability.
public interface ICrafting
{
    // crystalItem/crystalSlot = the crystal (item id + its inventory slot); ingredients = up to 8
    // (item id + inventory slot) each. The crystal must be a valid crystal and the slots must hold
    // exactly those items, or the server rejects the synth. Sends the combine and waits for the
    // server's result packet (0x06F). Returns the SynthesisResult code (0=Success, 1=Failed,
    // 2=Interrupted, 6=SkillTooLow, 13=MustWaitLonger, 14=InterruptedCritical), or -1 on timeout.
    Task<int> Synth(ushort crystalItem, byte crystalSlot, IReadOnlyList<(ushort item, byte slot)> ingredients, CancellationToken ct = default);
}

/// Builds 0x096 GP_CLI_COMMAND_COMBINE_ASK. Layout: hdr(4) HashNo@4 padding@5 Crystal@6(u16)
/// CrystalIdx@8 Items@9 ItemNo[8]@10(u16 each) TableNo[8]@26(u8 each). 36 bytes = 9 words
/// (PacketSize[0x096]=0x12). HashNo is not validated (left 0); the recipe is matched from the mats.
internal static class CombinePacket
{
    public static byte[] Build(ushort crystal, byte crystalSlot, IReadOnlyList<(ushort item, byte slot)> ings)
    {
        var p = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(0x096 | (9 << 9)));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), crystal);
        p[8] = crystalSlot;
        p[9] = (byte)Math.Min(ings.Count, 8);
        for (int i = 0; i < ings.Count && i < 8; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(10 + i * 2), ings[i].item);
            p[26 + i] = ings[i].slot;
        }
        return p;
    }
}

public sealed class Crafting(ISession s) : ICrafting
{
    public async Task<int> Synth(ushort crystalItem, byte crystalSlot, IReadOnlyList<(ushort item, byte slot)> ingredients, CancellationToken ct = default)
    {
        s.State.SynthResult = -1;                                  // arm: wait for the next 0x06F
        s.Enqueue(CombinePacket.Build(crystalItem, crystalSlot, ingredients));
        for (int t = 0; t < 30000 && s.State.SynthResult < 0; t += 200)  // synth animation can run ~15s+
            await Task.Delay(200, ct);
        return s.State.SynthResult;
    }
}
