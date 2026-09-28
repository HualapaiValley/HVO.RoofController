using System;
using System.Collections.Generic;
using System.IO;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>A decoded greyscale JPEG and the markers it had, in order (without the first byte, 0xFF).</summary>
internal sealed record DecodedJpeg(int Width, int Height, byte[] Pixels, IReadOnlyList<byte> Markers);

/// <summary>
/// A strict decoder for the one kind of JPEG the emulated camera writes (ITU T.81 baseline, one 8-bit component,
/// 1x1 sampling, no restart intervals), written independently of the encoder so a round trip checks it: any
/// other segment, a marker inside the entropy data, padding that is not 1 bits, or bytes after EOI fail the decode.
/// </summary>
internal static class BaselineJpegDecoder
{
    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];

    public static DecodedJpeg Decode(byte[] data)
    {
        var markers = new List<byte>();
        var position = 0;
        Expect(ReadMarker(data, ref position) == 0xD8, "The data does not start with SOI.");
        markers.Add(0xD8);

        var quantization = new int[64];
        var huffman = new Dictionary<int, Dictionary<(int Length, int Code), byte>>();
        int width = 0, height = 0;
        while (true)
        {
            var marker = ReadMarker(data, ref position);
            markers.Add(marker);
            var length = (data[position] << 8) | data[position + 1];
            var segment = data.AsSpan(position + 2, length - 2);
            position += length;
            switch (marker)
            {
                case 0xE0:
                    Expect(segment[..5].SequenceEqual("JFIF\0"u8), "APP0 is not JFIF.");
                    break;
                case 0xDB:
                    Expect(segment.Length == 65 && segment[0] == 0, "DQT is not one 8-bit table 0.");
                    for (var k = 0; k < 64; k++)
                    {
                        quantization[k] = segment[1 + k];
                    }

                    break;
                case 0xC0:
                    Expect(segment[0] == 8 && segment[5] == 1 && segment[7] == 0x11 && segment[8] == 0, "SOF0 is not one 8-bit component at 1x1 on table 0.");
                    height = (segment[1] << 8) | segment[2];
                    width = (segment[3] << 8) | segment[4];
                    break;
                case 0xC4:
                    while (segment.Length > 0)
                    {
                        huffman[segment[0]] = BuildTable(segment, out var used);
                        segment = segment[used..];
                    }

                    break;
                case 0xDA:
                    Expect(segment.Length == 6 && segment[0] == 1 && segment[1] == 1 && segment[2] == 0x00 && segment[3] == 0 && segment[4] == 63 && segment[5] == 0,
                        "SOS is not one baseline scan of component 1 on tables 0.");
                    var pixels = DecodeScan(data, ref position, width, height, quantization, huffman[0x00], huffman[0x10]);
                    Expect(ReadMarker(data, ref position) == 0xD9, "The scan is not followed by EOI.");
                    markers.Add(0xD9);
                    Expect(position == data.Length, "There are bytes after EOI.");
                    return new DecodedJpeg(width, height, pixels, markers);
                default:
                    throw new InvalidDataException($"Unexpected marker 0xFF{marker:X2}.");
            }
        }
    }

    private static byte ReadMarker(byte[] data, ref int position)
    {
        Expect(data[position] == 0xFF, $"Expected a marker at {position}.");
        position += 2;
        return data[position - 1];
    }

    /// <summary>The first table of a DHT segment, which can hold several; <paramref name="used"/> is its length.</summary>
    private static Dictionary<(int Length, int Code), byte> BuildTable(ReadOnlySpan<byte> segment, out int used)
    {
        var table = new Dictionary<(int, int), byte>();
        var code = 0;
        var symbol = 17;
        for (var length = 1; length <= 16; length++)
        {
            for (var i = 0; i < segment[length]; i++)
            {
                table[(length, code++)] = segment[symbol++];
            }

            code <<= 1;
        }

        Expect(symbol <= segment.Length, "DHT ends inside its symbols.");
        used = symbol;
        return table;
    }

    private static byte[] DecodeScan(byte[] data, ref int position, int width, int height, int[] quantization,
        Dictionary<(int, int), byte> dc, Dictionary<(int, int), byte> ac)
    {
        var bits = new BitReader(data, position);
        var pixels = new byte[width * height];
        var coefficients = new double[64];
        var previousDc = 0;
        for (var by = 0; by < height; by += 8)
        {
            for (var bx = 0; bx < width; bx += 8)
            {
                Array.Clear(coefficients);
                var dcSize = ReadSymbol(bits, dc);
                previousDc += Extend(bits.Read(dcSize), dcSize);
                coefficients[0] = previousDc * quantization[0];
                for (var k = 1; k < 64; k++)
                {
                    var runSize = ReadSymbol(bits, ac);
                    int run = runSize >> 4, size = runSize & 15;
                    if (size == 0)
                    {
                        if (run != 15)
                        {
                            break;
                        }

                        k += 15;
                        continue;
                    }

                    k += run;
                    Expect(k < 64, "An AC run passes the end of the block.");
                    coefficients[ZigZag[k]] = Extend(bits.Read(size), size) * quantization[k];
                }

                Inverse(coefficients, pixels, bx, by, width, height);
            }
        }

        Expect(bits.RemainingBitsAreOnes(), "The scan's last byte is not padded with 1 bits.");
        position = bits.Position;
        return pixels;
    }

    private static int ReadSymbol(BitReader bits, Dictionary<(int, int), byte> table)
    {
        var code = 0;
        for (var length = 1; length <= 16; length++)
        {
            code = (code << 1) | bits.Read(1);
            if (table.TryGetValue((length, code), out var symbol))
            {
                return symbol;
            }
        }

        throw new InvalidDataException("A Huffman code is not in the table.");
    }

    private static int Extend(int value, int size)
        => size == 0 ? 0 : value < 1 << (size - 1) ? value - (1 << size) + 1 : value;

    private static void Inverse(double[] coefficients, byte[] pixels, int bx, int by, int width, int height)
    {
        for (var y = 0; y < 8 && by + y < height; y++)
        {
            for (var x = 0; x < 8 && bx + x < width; x++)
            {
                var sum = 0.0;
                for (var v = 0; v < 8; v++)
                {
                    for (var u = 0; u < 8; u++)
                    {
                        var c = coefficients[v * 8 + u];
                        if (c != 0)
                        {
                            sum += (u == 0 ? Math.Sqrt(0.5) : 1) * (v == 0 ? Math.Sqrt(0.5) : 1) * c
                                * Math.Cos((2 * x + 1) * u * Math.PI / 16) * Math.Cos((2 * y + 1) * v * Math.PI / 16);
                        }
                    }
                }

                pixels[(by + y) * width + bx + x] = (byte)Math.Clamp(Math.Round(sum / 4 + 128), 0, 255);
            }
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    /// <summary>Reads the entropy-coded bits, taking 0xFF 0x00 as 0xFF and refusing any other marker.</summary>
    private sealed class BitReader(byte[] data, int position)
    {
        private int _byte;
        private int _left;

        public int Position => position;

        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                if (_left == 0)
                {
                    Expect(position < data.Length, "The scan ran past the end of the data.");
                    _byte = data[position++];
                    if (_byte == 0xFF)
                    {
                        Expect(data[position] == 0x00, $"Marker 0xFF{data[position]:X2} inside the scan.");
                        position++;
                    }

                    _left = 8;
                }

                value = (value << 1) | ((_byte >> --_left) & 1);
            }

            return value;
        }

        public bool RemainingBitsAreOnes() => (_byte & ((1 << _left) - 1)) == (1 << _left) - 1;
    }
}
