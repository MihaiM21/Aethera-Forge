using System.Net;
using Aethera.Infrastructure.Dns;

namespace Aethera.Api.Tests.DnsProviders;

public sealed class CloudflareIpRangesTests
{
    private static IPAddress Ip(string address) => IPAddress.Parse(address);

    [Theory]
    [InlineData("104.21.5.9")]
    [InlineData("172.67.200.1")]
    [InlineData("173.245.48.1")]
    [InlineData("173.245.63.255")]
    [InlineData("162.159.255.255")]
    [InlineData("131.0.75.4")]
    [InlineData("2606:4700:3030::6815:509")]
    [InlineData("2a06:98c0:3600::103")]
    [InlineData("::ffff:104.21.5.9")]
    public void Contains_AddressesInsideTheRanges(string address) => Assert.True(CloudflareIpRanges.Contains(Ip(address)));

    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("173.245.64.1")]
    [InlineData("104.15.255.255")]
    [InlineData("104.32.0.1")]
    [InlineData("172.80.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:db8::1")]
    [InlineData("2606:4701::1")]
    [InlineData("::ffff:203.0.113.10")]
    public void Contains_AddressesOutsideTheRanges(string address) => Assert.False(CloudflareIpRanges.Contains(Ip(address)));

    [Fact]
    public void AllInside_RequiresEveryAnswerToBeInside()
    {
        Assert.True(CloudflareIpRanges.AllInside([Ip("104.21.5.9"), Ip("172.67.200.1"), Ip("2606:4700::1")]));
        Assert.False(CloudflareIpRanges.AllInside([Ip("104.21.5.9"), Ip("203.0.113.10")])); // mixed: not proxied
        Assert.False(CloudflareIpRanges.AllInside([Ip("203.0.113.10")]));
        Assert.False(CloudflareIpRanges.AllInside([])); // nothing resolved is not "proxied"
    }

    [Fact]
    public void TheListIsTheDatedPublishedOne()
    {
        Assert.Equal(22, CloudflareIpRanges.Cidrs.Count);
        Assert.Equal("2026-10-24", CloudflareIpRanges.CopiedOn);
    }
}

public sealed class CloudflareZonesTests
{
    [Fact]
    public void FindZone_LongestSuffixWins()
    {
        string[] zones = ["example.com", "app.example.com", "other.org"];
        Assert.Equal("app.example.com", CloudflareZones.FindZone("api.app.example.com", zones));
        Assert.Equal("example.com", CloudflareZones.FindZone("www.example.com", zones));
        Assert.Equal("example.com", CloudflareZones.FindZone("example.com", zones));
        Assert.Equal("example.com", CloudflareZones.FindZone("*.example.com", zones));
        Assert.Equal("app.example.com", CloudflareZones.FindZone("*.app.example.com", zones));
    }

    [Fact]
    public void FindZone_RequiresALabelBoundary_AndIsCaseInsensitive()
    {
        Assert.Null(CloudflareZones.FindZone("notexample.com", ["example.com"]));
        Assert.Null(CloudflareZones.FindZone("example.com.evil.net", ["example.com"]));
        Assert.Null(CloudflareZones.FindZone("app.example.com", []));
        Assert.Equal("example.com", CloudflareZones.FindZone("APP.Example.com.", ["Example.COM"]));
    }

    [Fact]
    public void CandidateNames_ExactThenWildcardsUpToTheZone()
    {
        Assert.Equal(["a.b.example.com", "*.b.example.com", "*.example.com"], CloudflareZones.CandidateNames("a.b.example.com", "example.com"));
        Assert.Equal(["example.com"], CloudflareZones.CandidateNames("example.com", "example.com"));
        Assert.Equal(["app.example.com", "*.example.com"], CloudflareZones.CandidateNames("app.example.com", "example.com"));
        Assert.Equal(["*.example.com"], CloudflareZones.CandidateNames("*.example.com", "example.com"));
    }
}
