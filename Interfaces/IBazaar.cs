using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

/// Personal bazaar: price inventory items for sale, then open the bazaar so other players can buy
/// from the bot where it stands. (Buyer-side packets 0x104/0x105/0x106 are a separate concern.)
public interface IBazaar
{
    // Setup order is BeginEdit -> SetPrice(s) -> Open. (Server semantics, 0x10b/0x109: "close"
    // = enter the Set-Prices menu / hide the bazaar to edit; "open" = exit the menu / show it.)
    void BeginEdit();                          // 0x10B — enter price-editing mode (isSettingBazaarPrices=true)
    void SetPrice(byte invSlot, uint price);   // 0x10A — price an inventory item for sale
    void Open();                               // 0x109 — finish editing and open the bazaar to buyers

    // BUYER side (bot-to-bot handoff): browse a nearby player's bazaar, buy by bazaar slot, then exit.
    // Browse triggers a burst of s2c 0x105 items into World.BazaarList (clear it before browsing).
    void Browse(uint sellerId, ushort targid); // c2s 0x105 — target + list a player's bazaar
    void Buy(byte bazaarSlot, uint count);     // c2s 0x106 — buy from the browsed bazaar
    void StopBrowsing();                       // c2s 0x104 — release the bazaar target
}
