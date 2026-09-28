using System.Net;
using PlanShare;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Every per-client limit on the share server counts under one key. An IPv4 address is its own key,
/// an IPv4-mapped IPv6 address is the IPv4 address it wraps, and any other IPv6 address is its /64.
/// </summary>
public class PlanShareClientKeyTests
{
    [Fact]
    public void IPv4Address_IsItsOwnKey()
    {
        Assert.Equal("203.0.113.7", ClientKey.From(IPAddress.Parse("203.0.113.7")));
    }

    [Fact]
    public void IPv4MappedIPv6Address_HasTheSameKeyAsTheIPv4Address()
    {
        var mapped = ClientKey.From(IPAddress.Parse("::ffff:203.0.113.7"));

        Assert.Equal("203.0.113.7", mapped);
        Assert.Equal(ClientKey.From(IPAddress.Parse("203.0.113.7")), mapped);
    }

    [Fact]
    public void IPv6Addresses_InOneSlash64_ShareAKey()
    {
        var first = ClientKey.From(IPAddress.Parse("2001:db8:1:2::1"));
        var second = ClientKey.From(IPAddress.Parse("2001:db8:1:2:ffff:eeee:dddd:cccc"));

        Assert.Equal(first, second);
        Assert.Equal("2001:db8:1:2::/64", first);
    }

    [Fact]
    public void IPv6Addresses_InDifferentSlash64s_HaveDifferentKeys()
    {
        // Differ only in the last bit of the 64-bit prefix
        var first = ClientKey.From(IPAddress.Parse("2001:db8:1:2::1"));
        var second = ClientKey.From(IPAddress.Parse("2001:db8:1:3::1"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void IPv6ZoneId_IsNotPartOfTheKey()
    {
        Assert.Equal("fe80::/64", ClientKey.From(IPAddress.Parse("fe80::1%3")));
    }

    [Fact]
    public void IPv6Key_EndsInSlash64_SoItCannotEqualAnIPv4Key()
    {
        Assert.EndsWith("/64", ClientKey.From(IPAddress.Parse("2001:db8::1")));
        Assert.DoesNotContain('/', ClientKey.From(IPAddress.Parse("203.0.113.7")));
    }

    [Fact]
    public void NoAddress_GivesTheUnknownKey()
    {
        Assert.Equal("unknown", ClientKey.From(null));
    }
}
