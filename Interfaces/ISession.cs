namespace XiHeadless.Interfaces;

/// Minimal surface the capabilities need from the connection.
public interface ISession
{
    WorldState State { get; }
    void Enqueue(byte[] subPacket);

    /// Enqueue several sub-packets atomically so they land in the SAME outbound frame, consecutively
    /// (no 0x015 keepalive or other packet processed between them). Required by the server's PacketGuard
    /// for ordered pairs like 0x084 SHOP_SELL_REQ -> 0x085 SHOP_SELL_SET (the 0x085 is dropped as
    /// "out-of-order" unless the immediately preceding processed packet was the 0x084).
    void EnqueueAtomic(params byte[][] subPackets);
}
