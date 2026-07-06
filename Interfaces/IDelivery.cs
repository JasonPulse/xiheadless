using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

/// Delivery box (Mog House mail). Mail gil/items to another character by name — async, the recipient
/// can be offline. The bot must be in a delivery-capable zone (a city with AH/mog-menu, or a
/// residential area); cities qualify, so an RMT bot can deliver from where it spams.
public interface IDelivery
{
    Task<bool> EnterMogHouse(CancellationToken ct = default);   // 0x5E to the current city's MH entrance
    Task ExitMogHouse(CancellationToken ct = default);          // 0x5E mogHouseZoneLine -> back to the city
    Task<bool> SendGil(string player, int amount, CancellationToken ct = default);
    Task<bool> SendItem(string player, byte invSlot, int qty, CancellationToken ct = default);   // mail one inventory slot's item
}
