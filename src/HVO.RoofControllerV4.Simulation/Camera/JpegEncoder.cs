namespace HVO.RoofControllerV4.Simulation.Camera;

/// <summary>
/// A baseline JPEG encoder (ITU T.81) for 8-bit greyscale images, with the example luminance quantization and Huffman
/// tables of Annex K. It is enough for the emulated camera's frames, which browsers decode as any camera's JPEG.
/// </summary>
public static class JpegEncoder
{
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];

    // Table K.1, in natural (row-major) order.
    private static readonly byte[] LuminanceQuantization =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99
    ];

    // Tables K.3 and K.5: code counts by length (1-16 bits), then the symbols in code order.
    private static readonly byte[] DcBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] AcBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    private static readonly byte[] AcValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa
    ];

    private static readonly byte[] ZigZagPosition = Inverse(ZigZag);
    private static readonly (ushort Code, byte Length)[] DcCodes = BuildCodes(DcBits, DcValues);
    private static readonly (ushort Code, byte Length)[] AcCodes = BuildCodes(AcBits, AcValues);
    private static readonly double[,] Cosines = BuildCosines();

    /// <summary>Encodes <paramref name="pixels"/> (row-major, one byte per pixel) as a baseline greyscale JPEG.</summary>
    /// <param name="quality">1-100, as the IJG scale: 50 is the Annex K table.</param>
    public static byte[] EncodeGreyscale(ReadOnlySpan<byte> pixels, int width, int height, int quality = 75)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNotEqual(pixels.Length, width * height);
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);

        var table = ScaledTable(quality);
        using var output = new MemoryStream(width * height / 4 + 1024);
        WriteHeaders(output, width, height, table);

        var writer = new BitWriter(output);
        Span<double> block = stackalloc double[64];
        Span<int> quantized = stackalloc int[64];
        var previousDc = 0;
        for (var by = 0; by < height; by += 8)
        {
            for (var bx = 0; bx < width; bx += 8)
            {
                // Edge blocks repeat the last row and column, as encoders usually pad.
                for (var y = 0; y < 8; y++)
                {
                    var row = Math.Min(by + y, height - 1) * width;
                    for (var x = 0; x < 8; x++)
                    {
                        block[y * 8 + x] = pixels[row + Math.Min(bx + x, width - 1)] - 128;
                    }
                }

                Transform(block, table, quantized);
                previousDc = EncodeBlock(writer, quantized, previousDc);
            }
        }

        writer.Flush();
        output.Write([0xFF, 0xD9]);
        return output.ToArray();
    }

    private static byte[] ScaledTable(int quality)
    {
        var scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var table = new byte[64];
        for (var i = 0; i < 64; i++)
        {
            table[i] = (byte)Math.Clamp((LuminanceQuantization[i] * scale + 50) / 100, 1, 255);
        }

        return table;
    }

    private static void WriteHeaders(Stream output, int width, int height, byte[] table)
    {
        output.Write([0xFF, 0xD8]);

        // APP0 JFIF 1.1, no density units, no thumbnail.
        output.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);

        // DQT: table 0, 8-bit values in zig-zag order.
        output.Write([0xFF, 0xDB, 0x00, 0x43, 0x00]);
        for (var i = 0; i < 64; i++)
        {
            output.WriteByte(table[ZigZag[i]]);
        }

        // SOF0: 8-bit precision, one component (id 1, 1x1 sampling, table 0).
        output.Write([0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00]);

        // DHT: DC table 0 and AC table 0.
        var length = 2 + 1 + 16 + DcValues.Length + 1 + 16 + AcValues.Length;
        output.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)length, 0x00]);
        output.Write(DcBits);
        output.Write(DcValues);
        output.WriteByte(0x10);
        output.Write(AcBits);
        output.Write(AcValues);

        // SOS: one component, tables 0/0, spectral selection 0-63, no successive approximation.
        output.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
    }

    /// <summary>The forward DCT of one level-shifted block, quantized and put in zig-zag order.</summary>
    private static void Transform(ReadOnlySpan<double> block, byte[] table, Span<int> quantized)
    {
        Span<double> rows = stackalloc double[64];
        for (var y = 0; y < 8; y++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0.0;
                for (var x = 0; x < 8; x++)
                {
                    sum += block[y * 8 + x] * Cosines[u, x];
                }

                rows[y * 8 + u] = sum;
            }
        }

        for (var v = 0; v < 8; v++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0.0;
                for (var y = 0; y < 8; y++)
                {
                    sum += rows[y * 8 + u] * Cosines[v, y];
                }

                var natural = v * 8 + u;
                quantized[ZigZagPosition[natural]] = (int)Math.Round(sum / table[natural]);
            }
        }
    }

    private static int EncodeBlock(BitWriter writer, ReadOnlySpan<int> coefficients, int previousDc)
    {
        var dc = coefficients[0];
        var difference = dc - previousDc;
        var size = Magnitude(difference);
        writer.Write(DcCodes[size]);
        writer.Write(Amplitude(difference, size), size);

        var zeros = 0;
        for (var k = 1; k < 64; k++)
        {
            var value = coefficients[k];
            if (value == 0)
            {
                zeros++;
                continue;
            }

            while (zeros > 15)
            {
                writer.Write(AcCodes[0xF0]);
                zeros -= 16;
            }

            size = Magnitude(value);
            writer.Write(AcCodes[(zeros << 4) | size]);
            writer.Write(Amplitude(value, size), size);
            zeros = 0;
        }

        if (zeros > 0)
        {
            writer.Write(AcCodes[0x00]);
        }

        return dc;
    }

    /// <summary>The number of bits of |value| (its category).</summary>
    private static int Magnitude(int value)
    {
        var magnitude = Math.Abs(value);
        var bits = 0;
        while (magnitude > 0)
        {
            bits++;
            magnitude >>= 1;
        }

        return bits;
    }

    /// <summary>The value's low bits as the standard codes it: negative values as their ones' complement.</summary>
    private static int Amplitude(int value, int size) => value < 0 ? value + (1 << size) - 1 : value;

    /// <summary>The Huffman code of each symbol (Annex C): codes of each length in order, then one bit longer.</summary>
    private static (ushort Code, byte Length)[] BuildCodes(byte[] bits, byte[] values)
    {
        var codes = new (ushort, byte)[256];
        var code = 0;
        var index = 0;
        for (var length = 1; length <= 16; length++)
        {
            for (var i = 0; i < bits[length - 1]; i++)
            {
                codes[values[index++]] = ((ushort)code, (byte)length);
                code++;
            }

            code <<= 1;
        }

        return codes;
    }

    private static byte[] Inverse(byte[] order)
    {
        var inverse = new byte[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            inverse[order[i]] = (byte)i;
        }

        return inverse;
    }

    private static double[,] BuildCosines()
    {
        var cosines = new double[8, 8];
        for (var u = 0; u < 8; u++)
        {
            var c = u == 0 ? Math.Sqrt(0.5) : 1.0;
            for (var x = 0; x < 8; x++)
            {
                cosines[u, x] = 0.5 * c * Math.Cos((2 * x + 1) * u * Math.PI / 16);
            }
        }

        return cosines;
    }

    /// <summary>Writes the entropy-coded segment most significant bit first, stuffing a zero after each 0xFF byte.</summary>
    private sealed class BitWriter(Stream output)
    {
        private int _buffer;
        private int _count;

        public void Write((ushort Code, byte Length) code) => Write(code.Code, code.Length);

        public void Write(int bits, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((bits >> i) & 1);
                if (++_count == 8)
                {
                    Emit();
                }
            }
        }

        /// <summary>Pads the last byte with one bits.</summary>
        public void Flush()
        {
            while (_count != 0)
            {
                Write(1, 1);
            }
        }

        private void Emit()
        {
            var value = (byte)_buffer;
            output.WriteByte(value);
            if (value == 0xFF)
            {
                output.WriteByte(0x00);
            }

            _buffer = 0;
            _count = 0;
        }
    }
}
