using System.Buffers.Binary;

namespace XiHeadless.Interfaces;

public interface IChat
{
    void Say(string msg);
    void Shout(string msg);          // zone-wide
    void Yell(string msg);           // cross-zone (city-area broadcast)
    void Party(string msg);
    void Tell(string to, string msg);
}
