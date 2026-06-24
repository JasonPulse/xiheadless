namespace XiHeadless.Capabilities;

/// Minimal surface the capabilities need from the connection.
public interface ISession
{
    WorldState State { get; }
    void Enqueue(byte[] subPacket);
}
