using System.Buffers.Binary;

namespace XiHeadless.Net;

/// LSB's custom Huffman-style bit decompressor (src/common/zlib.cpp — NOT real zlib).
/// Driven by a jump table built from res/decompress.dat. Compressed stream: byte[0]==1
/// header, then a bitstream; `bits` (the size field in the packet) is the bit count.
/// Verified against the compiled LSB oracle (scratchpad/pipe).
public sealed class FfxiDecompress
{
    readonly uint[] _jump;

    public FfxiDecompress(string decompressDatPath)
    {
        var bytes = File.ReadAllBytes(decompressDatPath);
        int n = bytes.Length / 4;
        var data = new uint[n];
        for (int i = 0; i < n; i++) data[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        uint @base = data[0] - 4;
        _jump = new uint[n];
        for (int i = 0; i < n; i++)
            _jump[i] = data[i] > 0xff ? (data[i] - @base) / 4 : data[i];
    }

    /// Decompress `bits` bits from `comp` (which begins with the 0x01 header byte).
    public byte[] Decompress(ReadOnlySpan<byte> comp, uint bits, int maxOut = 8192)
    {
        // comp[0] must be 1; bitstream starts at comp[1]
        var src = comp[1..];
        var outBuf = new byte[maxOut];
        int w = 0;
        uint pos = _jump[0];
        for (uint i = 0; i < bits && w < maxOut; i++)
        {
            int s = (src[(int)(i / 8)] >> (int)(i & 7)) & 1;
            pos = _jump[pos + s];
            if (_jump[pos] != 0 || _jump[pos + 1] != 0) continue; // internal node
            outBuf[w++] = (byte)_jump[pos + 3];                   // leaf data
            pos = _jump[0];
        }
        return outBuf[..w];
    }
}
