using Telrang.Core.Ids;

namespace Telrang.Core.Tests;

public class PollIdTests
{
    [Fact]
    public void RoundTrip()
    {
        for (var i = 0; i < 100; i++)
        {
            var g = Guid.NewGuid();
            var s = PollId.Encode(g);
            Assert.Equal(22, s.Length);
            Assert.True(PollId.TryDecode(s, out var back));
            Assert.Equal(g, back);
        }
    }

    [Fact]
    public void EncodeUsesGuidBytesAndNoPadding()
    {
        var g = Guid.Empty;
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAA", PollId.Encode(g));
        var all = new Guid(Enumerable.Repeat((byte)0xFF, 16).ToArray());
        Assert.Equal("_____________________w", PollId.Encode(all));
    }

    [Fact]
    public void RejectsNull() => Assert.False(PollId.TryDecode(null, out _));

    [Theory]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAA")]    // 21
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAA")]  // 23
    [InlineData("AAAAAAAAAAAAAAAAAAAAA+")]   // '+'
    [InlineData("AAAAAAAAAAAAAAAAAAAAA/")]   // '/'
    [InlineData("AAAAAAAAAAAAAAAAAAAAA=")]   // padding char
    [InlineData("AAAAAAAAAAAAAAAAAAAA==")]   // padding
    [InlineData("AAAAAAAAAAAAAAAAAAAAA ")]   // whitespace
    [InlineData("AAAAAAAAAAAAAAAAAAAAAB")]   // non-canonical trailing bits
    [InlineData("AAAAAAAAAAAAAAAAAAAAAé")]
    public void RejectsMalformed(string s) => Assert.False(PollId.TryDecode(s, out _));

    [Fact]
    public void NewIdIsVersion4()
    {
        for (var i = 0; i < 50; i++)
        {
            var g = PollId.NewId();
            var s = g.ToString("D"); // xxxxxxxx-xxxx-Vxxx-Nxxx-...
            Assert.Equal('4', s[14]);
            Assert.Contains(s[19], "89ab");
        }
    }

    [Fact]
    public void NewIdsAreDistinct() => Assert.NotEqual(PollId.NewId(), PollId.NewId());
}
