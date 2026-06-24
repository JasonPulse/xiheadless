using System.Buffers.Binary;

namespace XiHeadless.Net;

/// Port of LSB zlib_compress (src/common/zlib.cpp), driven by res/compress.dat
/// (512 uint32: [b+0x80]=bit pattern, [b+0x180]=bit length, b = signed byte value).
/// Output: out[0]=1 header, then a bitstream. Returns compressed BIT count (+8),
/// which goes into the packet's size field. Inverse of FfxiDecompress.
public sealed class FfxiCompress
{
    readonly uint[] _enc;

    public FfxiCompress(string compressDatPath)
    {
        var bytes = File.ReadAllBytes(compressDatPath);
        _enc = new uint[bytes.Length / 4];
        for (int i = 0; i < _enc.Length; i++)
            _enc[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
    }

    /// Compress `input` into `out` (out[0]=1 header + bitstream). Returns bit count + 8.
    public int Compress(ReadOnlySpan<byte> input, byte[] outBuf)
    {
        Array.Clear(outBuf);
        int read = 0; // bit offset
        foreach (byte raw in input)
        {
            int b = (sbyte)raw;
            uint elem = _enc[b + 0x180];        // number of bits for this byte
            uint v = _enc[b + 0x80];            // bit pattern
            Span<byte> b32 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b32, v);
            for (int k = 0; k < elem; k++)
            {
                int bit = (b32[k / 8] >> (k & 7)) & 1;
                int pos = read + k;
                int idx = 1 + pos / 8;          // out[0] reserved for header
                int sh = pos & 7;
                outBuf[idx] = (byte)((outBuf[idx] & ~(1 << sh)) | (bit << sh));
            }
            read += (int)elem;
        }
        outBuf[0] = 1;
        return read + 8;
    }
}
