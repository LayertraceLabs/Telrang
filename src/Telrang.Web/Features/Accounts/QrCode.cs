using System.Net;
using System.Text;

namespace Telrang.Web.Features.Accounts;

/// <summary>
/// Dependency-free QR code encoder (ISO/IEC 18004) for the authenticator enrolment page (DESIGN §19):
/// byte mode (UTF-8), error correction level M, versions 1–10, smallest version that fits, mask chosen
/// by the four standard penalty rules. Matrices are indexed [row, column]; true is a dark module.
/// </summary>
public static class QrCode
{
    public const int MaxVersion = 10;

    /// <summary>Largest UTF-8 input, in bytes (version 10-M).</summary>
    public const int MaxBytes = 213;

    // Level M block structure per version (index = version):
    // EC codewords per block, blocks in group 1, data codewords per group-1 block, blocks in group 2
    // (group-2 blocks hold one more data codeword).
    private static readonly (int Ec, int G1Blocks, int G1Data, int G2Blocks)[] Blocks =
    [
        default,
        (10, 1, 16, 0), (16, 1, 28, 0), (26, 1, 44, 0), (18, 2, 32, 0), (24, 2, 43, 0),
        (16, 4, 27, 0), (18, 4, 31, 0), (22, 2, 38, 2), (22, 3, 36, 2), (26, 4, 43, 1),
    ];

    private static readonly int[][] AlignmentCenters =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    private const int EcLevelMBits = 0b00;

    public static bool[,] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var data = Encoding.UTF8.GetBytes(text);

        var version = 0;
        for (var v = 1; v <= MaxVersion; v++)
        {
            if (4 + CountBits(v) + 8 * data.Length <= DataCodewords(v) * 8)
            {
                version = v;
                break;
            }
        }
        if (version == 0)
        {
            throw new ArgumentException($"Text is {data.Length} bytes; a version {MaxVersion}-M QR code holds {MaxBytes}.", nameof(text));
        }

        var codewords = AddErrorCorrection(version, EncodeData(version, data));
        var size = 17 + 4 * version;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];

        DrawFunctionPatterns(version, modules, isFunction);
        PlaceData(codewords, modules, isFunction);

        var bestMask = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask, modules, isFunction);
            DrawFormatBits(mask, modules, isFunction);
            var penalty = Penalty(modules);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
            }
            ApplyMask(mask, modules, isFunction); // XOR again to undo
        }
        ApplyMask(bestMask, modules, isFunction);
        DrawFormatBits(bestMask, modules, isFunction);
        return modules;
    }

    /// <summary>
    /// SVG with dark modules as <c>fill="currentColor"</c> rects (merged per horizontal run) on a white
    /// background rect. No style attribute (CSP); size it with a CSS class.
    /// </summary>
    public static string ToSvg(bool[,] modules, int quietZone = 4, string? cssClass = null, string? ariaLabel = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentOutOfRangeException.ThrowIfNegative(quietZone);
        var size = modules.GetLength(0);
        var full = size + 2 * quietZone;

        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(full).Append(' ').Append(full).Append('"');
        if (cssClass is not null)
        {
            sb.Append(" class=\"").Append(WebUtility.HtmlEncode(cssClass)).Append('"');
        }
        if (ariaLabel is not null)
        {
            sb.Append(" role=\"img\" aria-label=\"").Append(WebUtility.HtmlEncode(ariaLabel)).Append('"');
        }
        sb.Append(" shape-rendering=\"crispEdges\">");
        sb.Append("<rect width=\"").Append(full).Append("\" height=\"").Append(full).Append("\" fill=\"#fff\"/>");
        sb.Append("<g fill=\"currentColor\">");
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (!modules[y, x])
                {
                    continue;
                }
                var start = x;
                while (x + 1 < size && modules[y, x + 1])
                {
                    x++;
                }
                sb.Append("<rect x=\"").Append(start + quietZone).Append("\" y=\"").Append(y + quietZone)
                  .Append("\" width=\"").Append(x - start + 1).Append("\" height=\"1\"/>");
            }
        }
        sb.Append("</g></svg>");
        return sb.ToString();
    }

    // ---- data -----------------------------------------------------------------------------------

    private static int CountBits(int version) => version < 10 ? 8 : 16;

    private static int DataCodewords(int version)
    {
        var b = Blocks[version];
        return b.G1Blocks * b.G1Data + b.G2Blocks * (b.G1Data + 1);
    }

    private static byte[] EncodeData(int version, byte[] data)
    {
        var capacityBits = DataCodewords(version) * 8;
        var bits = new BitWriter(capacityBits);
        bits.Write(0b0100, 4); // byte mode
        bits.Write(data.Length, CountBits(version));
        foreach (var b in data)
        {
            bits.Write(b, 8);
        }
        bits.Write(0, Math.Min(4, capacityBits - bits.Length)); // terminator
        bits.Write(0, (8 - bits.Length % 8) % 8);
        for (var pad = 0xEC; bits.Length < capacityBits; pad ^= 0xEC ^ 0x11)
        {
            bits.Write(pad, 8);
        }
        return bits.ToBytes();
    }

    private static byte[] AddErrorCorrection(int version, byte[] data)
    {
        var (ec, g1Blocks, g1Data, g2Blocks) = Blocks[version];
        var blockCount = g1Blocks + g2Blocks;
        var generator = ReedSolomon.Generator(ec);

        var dataBlocks = new byte[blockCount][];
        var ecBlocks = new byte[blockCount][];
        var offset = 0;
        for (var i = 0; i < blockCount; i++)
        {
            var length = g1Data + (i < g1Blocks ? 0 : 1);
            dataBlocks[i] = data.AsSpan(offset, length).ToArray();
            ecBlocks[i] = ReedSolomon.Remainder(dataBlocks[i], generator);
            offset += length;
        }

        // Interleave: codeword i of every block in turn (short blocks run out first), then the EC codewords.
        var result = new List<byte>(data.Length + ec * blockCount);
        for (var i = 0; i <= g1Data; i++)
        {
            foreach (var block in dataBlocks)
            {
                if (i < block.Length)
                {
                    result.Add(block[i]);
                }
            }
        }
        for (var i = 0; i < ec; i++)
        {
            foreach (var block in ecBlocks)
            {
                result.Add(block[i]);
            }
        }
        return [.. result];
    }

    private static void PlaceData(byte[] codewords, bool[,] modules, bool[,] isFunction)
    {
        var size = modules.GetLength(0);
        var bitIndex = 0;
        var totalBits = codewords.Length * 8;
        // Two-column strips from the right edge, zigzagging up and down, skipping the vertical timing column.
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }
            var upward = ((right + 1) & 2) == 0;
            for (var vert = 0; vert < size; vert++)
            {
                var y = upward ? size - 1 - vert : vert;
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    if (isFunction[y, x])
                    {
                        continue;
                    }
                    // Remainder bits (versions 2–6) stay light.
                    if (bitIndex < totalBits)
                    {
                        modules[y, x] = ((codewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                        bitIndex++;
                    }
                }
            }
        }
    }

    // ---- function patterns ----------------------------------------------------------------------

    private static void DrawFunctionPatterns(int version, bool[,] modules, bool[,] isFunction)
    {
        var size = modules.GetLength(0);

        for (var i = 0; i < size; i++)
        {
            Set(modules, isFunction, 6, i, i % 2 == 0); // vertical timing
            Set(modules, isFunction, i, 6, i % 2 == 0); // horizontal timing
        }

        DrawFinder(modules, isFunction, 3, 3);
        DrawFinder(modules, isFunction, size - 4, 3);
        DrawFinder(modules, isFunction, 3, size - 4);

        var centers = AlignmentCenters[version];
        var last = centers.Length - 1;
        for (var i = 0; i < centers.Length; i++)
        {
            for (var j = 0; j < centers.Length; j++)
            {
                var overlapsFinder = (i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0);
                if (!overlapsFinder)
                {
                    for (var dy = -2; dy <= 2; dy++)
                    {
                        for (var dx = -2; dx <= 2; dx++)
                        {
                            Set(modules, isFunction, centers[i] + dx, centers[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                        }
                    }
                }
            }
        }

        DrawFormatBits(0, modules, isFunction); // reserves the area; redrawn per mask
        DrawVersionBits(version, modules, isFunction);
    }

    /// <summary>Finder centred at (cx, cy) with its light separator, clipped to the symbol.</summary>
    private static void DrawFinder(bool[,] modules, bool[,] isFunction, int cx, int cy)
    {
        var size = modules.GetLength(0);
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || x >= size || y < 0 || y >= size)
                {
                    continue;
                }
                var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                Set(modules, isFunction, x, y, ring != 2 && ring != 4);
            }
        }
    }

    private static void DrawFormatBits(int mask, bool[,] modules, bool[,] isFunction)
    {
        var data = (EcLevelMBits << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }
        var bits = ((data << 10) | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        var size = modules.GetLength(0);
        // Around the top-left finder.
        for (var i = 0; i <= 5; i++)
        {
            Set(modules, isFunction, 8, i, Bit(i));
        }
        Set(modules, isFunction, 8, 7, Bit(6));
        Set(modules, isFunction, 8, 8, Bit(7));
        Set(modules, isFunction, 7, 8, Bit(8));
        for (var i = 9; i < 15; i++)
        {
            Set(modules, isFunction, 14 - i, 8, Bit(i));
        }
        // Split between the other two finders.
        for (var i = 0; i < 8; i++)
        {
            Set(modules, isFunction, size - 1 - i, 8, Bit(i));
        }
        for (var i = 8; i < 15; i++)
        {
            Set(modules, isFunction, 8, size - 15 + i, Bit(i));
        }
        Set(modules, isFunction, 8, size - 8, true); // dark module
    }

    private static void DrawVersionBits(int version, bool[,] modules, bool[,] isFunction)
    {
        if (version < 7)
        {
            return;
        }
        var rem = version;
        for (var i = 0; i < 12; i++)
        {
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        }
        var bits = (version << 12) | rem;

        var size = modules.GetLength(0);
        for (var i = 0; i < 18; i++)
        {
            var bit = ((bits >> i) & 1) != 0;
            int a = size - 11 + i % 3, b = i / 3;
            Set(modules, isFunction, a, b, bit); // top right
            Set(modules, isFunction, b, a, bit); // bottom left
        }
    }

    /// <summary>Sets a function module at column x, row y.</summary>
    private static void Set(bool[,] modules, bool[,] isFunction, int x, int y, bool dark)
    {
        modules[y, x] = dark;
        isFunction[y, x] = true;
    }

    // ---- masking --------------------------------------------------------------------------------

    private static void ApplyMask(int mask, bool[,] modules, bool[,] isFunction)
    {
        var size = modules.GetLength(0);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    _ => throw new ArgumentOutOfRangeException(nameof(mask)),
                };
                if (invert && !isFunction[y, x])
                {
                    modules[y, x] = !modules[y, x];
                }
            }
        }
    }

    private static readonly bool[] FinderLike = [true, false, true, true, true, false, true];

    private static int Penalty(bool[,] m)
    {
        var size = m.GetLength(0);
        var penalty = 0;

        // N1 (runs of 5+ same colour) and N3 (1:1:3:1:1 finder-like pattern with 4 light modules on one side),
        // rows and columns.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < size; a++)
            {
                bool At(int i) => pass == 0 ? m[a, i] : m[i, a];

                var run = 1;
                for (var i = 1; i < size; i++)
                {
                    if (At(i) == At(i - 1))
                    {
                        run++;
                    }
                    else
                    {
                        if (run >= 5)
                        {
                            penalty += run - 2;
                        }
                        run = 1;
                    }
                }
                if (run >= 5)
                {
                    penalty += run - 2;
                }

                for (var i = 0; i + 7 <= size; i++)
                {
                    var match = true;
                    for (var k = 0; k < 7 && match; k++)
                    {
                        match = At(i + k) == FinderLike[k];
                    }
                    if (!match)
                    {
                        continue;
                    }
                    if (LightSpan(i - 4, i) || LightSpan(i + 7, i + 11))
                    {
                        penalty += 40;
                    }
                }

                bool LightSpan(int from, int to)
                {
                    if (from < 0 || to > size)
                    {
                        return false;
                    }
                    for (var i = from; i < to; i++)
                    {
                        if (At(i))
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }
        }

        // N2: 2x2 blocks of one colour.
        for (var y = 0; y + 1 < size; y++)
        {
            for (var x = 0; x + 1 < size; x++)
            {
                var c = m[y, x];
                if (c == m[y, x + 1] && c == m[y + 1, x] && c == m[y + 1, x + 1])
                {
                    penalty += 3;
                }
            }
        }

        // N4: deviation of the dark share from 50 %, 10 points per 5 % step.
        var dark = 0;
        foreach (var module in m)
        {
            if (module)
            {
                dark++;
            }
        }
        var total = size * size;
        var k5 = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        penalty += k5 * 10;

        return penalty;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private sealed class BitWriter(int capacityBits)
    {
        private readonly byte[] bytes = new byte[(capacityBits + 7) / 8];

        public int Length { get; private set; }

        public void Write(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0)
                {
                    bytes[Length >> 3] |= (byte)(0x80 >> (Length & 7));
                }
                Length++;
            }
        }

        public byte[] ToBytes() => bytes;
    }

    /// <summary>Reed-Solomon over GF(256) with the QR field polynomial x^8 + x^4 + x^3 + x^2 + 1.</summary>
    private static class ReedSolomon
    {
        private static readonly byte[] Exp = new byte[255];
        private static readonly byte[] Log = new byte[256];

        static ReedSolomon()
        {
            var x = 1;
            for (var i = 0; i < 255; i++)
            {
                Exp[i] = (byte)x;
                Log[x] = (byte)i;
                x <<= 1;
                if (x >= 0x100)
                {
                    x ^= 0x11D;
                }
            }
        }

        private static byte Multiply(byte a, byte b) =>
            a == 0 || b == 0 ? (byte)0 : Exp[(Log[a] + Log[b]) % 255];

        /// <summary>Coefficients of (x - α^0)(x - α^1)…(x - α^(degree-1)), highest power first, leading 1 dropped.</summary>
        public static byte[] Generator(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1; // start with the constant polynomial 1
            var root = (byte)1;
            for (var i = 0; i < degree; i++)
            {
                // Multiply by (x - root): shift up, add root * current.
                for (var j = 0; j < degree; j++)
                {
                    result[j] = Multiply(result[j], root);
                    if (j + 1 < degree)
                    {
                        result[j] ^= result[j + 1];
                    }
                }
                root = Multiply(root, 2);
            }
            return result;
        }

        public static byte[] Remainder(byte[] data, byte[] generator)
        {
            var result = new byte[generator.Length];
            foreach (var b in data)
            {
                var factor = (byte)(b ^ result[0]);
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[^1] = 0;
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] ^= Multiply(generator[i], factor);
                }
            }
            return result;
        }
    }
}
