using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// Minimal pure-managed encoder for a solid-color 8-bit RGB PNG. Used to embed valid raster images into
/// test PPTX fixtures without pulling SkiaSharp / any native image dependency into the test project.
/// </summary>
internal static class TinyPng
{
    public static byte[] CreateSolid(int width, int height, byte r = 0x20, byte g = 0x60, byte b = 0xA0)
    {
        // Raw scanlines: each row is a filter byte (0 = None) followed by width*3 RGB samples.
        var raw = new byte[height * (1 + (width * 3))];
        var p = 0;
        for (var y = 0; y < height; y++)
        {
            raw[p++] = 0;
            for (var x = 0; x < width; x++)
            {
                raw[p++] = r;
                raw[p++] = g;
                raw[p++] = b;
            }
        }

        byte[] idat;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            idat = compressed.ToArray();
        }

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // color type: truecolor RGB
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", idat);
        WriteChunk(output, "IEND", Array.Empty<byte>());

        return output.ToArray();
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = new byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(typeBytes, data)));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }
}
