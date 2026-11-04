using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Telrang.Web.Features.Accounts;
using static Telrang.Web.Tests.TestUtil;

namespace Telrang.Web.Tests;

/// <summary>
/// QR encoder (DESIGN §19). The pinned matrices were rendered with <see cref="QrCode.ToSvg"/>, converted with
/// rsvg-convert and decoded with zbarimg back to the input (2026-10-29; the otpauth one again on 2026-11-01 after the rename); their codewords also match qrencode
/// bit for bit apart from the mask choice.
/// </summary>
public class QrCodeTests
{
    private const string Otpauth =
        "otpauth://totp/Telrang:someone@example.com?secret=ABCDEFGHIJKLMNOPQRSTUVWXYZ234567&issuer=Telrang&digits=6&period=30";

    private const string Utf8Text = "Grüße aus Köln – 投票 ✓";

    // Version 1-M.
    private static readonly string[] HelloWorld =
    [
        "111111101100101111111",
        "100000100001001000001",
        "101110100101001011101",
        "101110101001001011101",
        "101110101110101011101",
        "100000101001001000001",
        "111111101010101111111",
        "000000001001100000000",
        "100010111111011111001",
        "000100001011100001111",
        "001111110011011010010",
        "111110001100010000000",
        "111110101010101100110",
        "000000001010111101011",
        "111111101110101011010",
        "100000100101110110011",
        "101110101101011000110",
        "101110100100100011011",
        "101110100111000111000",
        "100000100001010000000",
        "111111101111111110101",
    ];

    // Version 3-M, 32 UTF-8 bytes.
    private static readonly string[] Utf8Matrix =
    [
        "11111110110000011110001111111",
        "10000010100100001100101000001",
        "10111010110010011111001011101",
        "10111010010110001010101011101",
        "10111010111110111011101011101",
        "10000010001000011010101000001",
        "11111110101010101010101111111",
        "00000000011101011110100000000",
        "10011111101001000011110010111",
        "00101001001111001000101111010",
        "01101011101011011101111110111",
        "10101100001101001000010111110",
        "01010010101111011001100101111",
        "10110000000111101011100011100",
        "00110011001111001100011100100",
        "11101000110100010101110010000",
        "10100111110110100010111001110",
        "10110000110001101111110011110",
        "11101010110100010100101010001",
        "11110101110000110101101110111",
        "11110011001001110111111111101",
        "00000000110000101110100011101",
        "11111110110110100111101011100",
        "10000010100110100110100010001",
        "10111010101101100011111111011",
        "10111010111101000011010101101",
        "10111010001011101010110111011",
        "10000010011000011110010111101",
        "11111110101010010001110110000",
    ];

    // Version 7-M (116 bytes; with version information blocks).
    private static readonly string[] OtpauthMatrix =
    [
        "111111101011110000101100011010111100101111111",
        "100000100100001101001101100100011101001000001",
        "101110100111110110001010111110001101001011101",
        "101110101011101110001111011010000001101011101",
        "101110101010110011101111101101110111101011101",
        "100000101001000001011000110110001000001000001",
        "111111101010101010101010101010101010101111111",
        "000000001000010111111000100101110000000000000",
        "100010111001110011001111101100001111111111001",
        "101000001001101000100000101010101001101101100",
        "011110101010001010110001001110101101011010010",
        "110101000001110101100011000100001110000100011",
        "010100111001110101101110101100001110011001000",
        "011100010000000000011111001101100101101111000",
        "100010101011001001111100000100100101101010010",
        "011111011001001101100000110000111010100010000",
        "100110101001111001110000100001001100001100100",
        "100011010110011110111110111101111100101000111",
        "010000101100100110000000001010010011110111110",
        "001111010100101101010011010001011101110000000",
        "011111111101110101111111111001011100111110000",
        "101010001001000000011000111001101101100011110",
        "100010101010101101001010111010111000101011010",
        "101110001110000000011000111001010001100010000",
        "001011111101111000111111110100001111111111011",
        "111011010010101011100011101000111100110100010",
        "100001111101010101001010101000101000110011010",
        "000100010010001011101100000101011001101000000",
        "010110110110010011011100010100101110001110001",
        "001110000111000100010000001011100101111101010",
        "011101101111111101010010100111111101010011110",
        "010111011101010101011011010000011010001110010",
        "010100110110110010000101010000101010000010101",
        "111011011010011001000010101001100100011100111",
        "000010110101010111101110101011111011110100010",
        "011110000100101110101010011001011010001000011",
        "100110101101000100011111101100011000111110010",
        "000000001110100100011000111101100100100010111",
        "111111101110000000101010111011100001101011010",
        "100000100010101100011000110101010011100010010",
        "101110101000010110111111100101001001111110001",
        "101110100111011001000001111100100100011100100",
        "101110100111101000010011111010110100001010010",
        "100000100001110010101001000000011001010000000",
        "111111101101010011001101100101111101101110001",
    ];

    private static string[] Rows(bool[,] modules)
    {
        var size = modules.GetLength(0);
        var rows = new string[size];
        for (var y = 0; y < size; y++)
        {
            var sb = new StringBuilder(size);
            for (var x = 0; x < size; x++)
            {
                sb.Append(modules[y, x] ? '1' : '0');
            }
            rows[y] = sb.ToString();
        }
        return rows;
    }

    [Fact]
    public void Pinned_matrices_match()
    {
        Assert.Equal(HelloWorld, Rows(QrCode.Encode("HELLO WORLD")));
        Assert.Equal(Utf8Matrix, Rows(QrCode.Encode(Utf8Text)));
        Assert.Equal(OtpauthMatrix, Rows(QrCode.Encode(Otpauth)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(14, 1)]
    [InlineData(15, 2)]
    [InlineData(26, 2)]
    [InlineData(27, 3)]
    [InlineData(42, 3)]
    [InlineData(62, 4)]
    [InlineData(84, 5)]
    [InlineData(106, 6)]
    [InlineData(122, 7)]
    [InlineData(123, 8)]
    [InlineData(152, 8)]
    [InlineData(180, 9)]
    [InlineData(181, 10)]
    [InlineData(213, 10)]
    public void Picks_the_smallest_version_and_size_is_17_plus_4v(int bytes, int version)
    {
        var modules = QrCode.Encode(new string('a', bytes));
        Assert.Equal(17 + 4 * version, modules.GetLength(0));
        Assert.Equal(17 + 4 * version, modules.GetLength(1));
    }

    [Fact]
    public void Throws_beyond_version_10_M()
    {
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('a', QrCode.MaxBytes + 1)));
        // Counted in UTF-8 bytes, not chars: 107 x "ü" is 214 bytes.
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('ü', 107)));
        QrCode.Encode(new string('a', QrCode.MaxBytes));
    }

    [Fact]
    public void Finder_patterns_sit_in_three_corners()
    {
        var m = QrCode.Encode(Otpauth);
        var n = m.GetLength(0);
        foreach (var (ox, oy) in new[] { (0, 0), (n - 7, 0), (0, n - 7) })
        {
            for (var dy = 0; dy < 7; dy++)
            {
                for (var dx = 0; dx < 7; dx++)
                {
                    var ring = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                    Assert.Equal(ring != 2, m[oy + dy, ox + dx]);
                }
            }
        }
        // Separator: the row below the top-left finder is light.
        for (var x = 0; x < 8; x++)
        {
            Assert.False(m[7, x]);
        }
        Assert.True(m[n - 8, 8]); // dark module
    }

    [Fact]
    public void Svg_is_csp_safe_and_scanner_friendly()
    {
        var svg = QrCode.ToSvg(QrCode.Encode("HELLO WORLD"), cssClass: "qr-code", ariaLabel: "QR \"code\"");
        Assert.StartsWith("<svg ", svg);
        Assert.DoesNotContain("style", svg);
        Assert.Contains("viewBox=\"0 0 29 29\"", svg); // 21 + 2 x 4 quiet zone
        Assert.Contains("shape-rendering=\"crispEdges\"", svg);
        Assert.Contains("<rect width=\"29\" height=\"29\" fill=\"#fff\"/>", svg);
        Assert.Contains("fill=\"currentColor\"", svg);
        Assert.Contains("class=\"qr-code\"", svg);
        Assert.Contains("role=\"img\" aria-label=\"QR &quot;code&quot;\"", svg);
        // Every dark module is covered by exactly one rect (runs are merged per row).
        var dark = Rows(QrCode.Encode("HELLO WORLD")).Sum(r => r.Count(c => c == '1'));
        var covered = Regex.Matches(svg, "<rect x=\"\\d+\" y=\"\\d+\" width=\"(\\d+)\" height=\"1\"/>")
            .Sum(match => int.Parse(match.Groups[1].Value));
        Assert.Equal(dark, covered);
    }

    [Fact]
    public async Task Enable_authenticator_page_shows_the_qr_code_and_the_key()
    {
        await using var factory = new AppFactory();
        await CreateUserAsync(factory, "qr@example.com");
        var browser = await LoggedInBrowserAsync(factory, "qr@example.com");
        var response = await browser.GetAsync("/Account/Manage/EnableAuthenticator");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=", html);
        Assert.Matches("<svg [^>]*class=\"qr-code\" role=\"img\" aria-label=\"QR code for the authenticator app\"", html);
        Assert.Contains("authenticator-key", html);
        Assert.Contains("style-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
}
