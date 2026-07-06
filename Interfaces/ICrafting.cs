using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

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
