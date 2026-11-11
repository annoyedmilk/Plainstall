using Plainstall.Core.Download;

namespace Plainstall.Core.Tests.Download;

public sealed class HostAllowlistTests
{
    [Theory]
    [InlineData("https://www.microsoft.com/en-us/software-download/windows11")]
    [InlineData(
        "https://www.microsoft.com/software-download-connector/api/getskuinformationbyproductedition?profile=1")]
    [InlineData("https://WWW.MICROSOFT.COM/")]
    [InlineData("https://www.microsoft.com:443/")]
    [InlineData("https://vlscppe.microsoft.com/tags?org_id=x&session_id=y")]
    [InlineData("https://ov-df.microsoft.com/mdt.js")]
    public void Catalog_allows_its_hosts(string url)
    {
        Assert.True(HostAllowlist.Catalog.IsAllowed(new Uri(url)));
        Assert.Null(HostAllowlist.Catalog.Check(new Uri(url)));
        Assert.True(HostAllowlist.Catalog.TryParse(url, out var parsed));
        Assert.NotNull(parsed);
    }

    [Theory]
    [InlineData("http://www.microsoft.com/")] // not https
    [InlineData("ftp://www.microsoft.com/")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("https://www.microsoft.com:8443/")] // unusual port
    [InlineData("https://www.microsoft.com:80/")]
    [InlineData("https://user@www.microsoft.com/")] // user info
    [InlineData("https://user:pw@www.microsoft.com/")]
    [InlineData("https://www.microsoft.com@evil.example/")] // the real host is evil.example
    [InlineData("https://www.microsoft.com.evil.example/")] // look-alike suffix
    [InlineData("https://evil.example/www.microsoft.com/")]
    [InlineData("https://evilwww.microsoft.com/")]
    [InlineData("https://microsoft.com/")] // parent domain is not on the list
    [InlineData("https://sub.www.microsoft.com/")]
    [InlineData("https://www.microsoft.com./")] // trailing dot
    [InlineData("https://www.micros\u043Eft.com/")] // Cyrillic o
    [InlineData("https://www.xn--microsft-sbh.com/")] // the same in punycode
    [InlineData("https://www.micro\u00ADsoft.com.evil.example/")] // soft hyphen
    [InlineData("https://www\u3002microsoft\u3002com.evil.example/")] // ideographic full stops
    [InlineData("https://20.70.246.20/")] // an address, not a name
    [InlineData("https://[::1]/")]
    [InlineData("https://localhost/")]
    [InlineData("https://software.download.prss.microsoft.com/x.iso")] // an ISO host is not a catalog host
    public void Catalog_refuses_everything_else(string url)
    {
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri), "the test input should at least parse");
        Assert.False(HostAllowlist.Catalog.IsAllowed(uri));
        Assert.NotNull(HostAllowlist.Catalog.Check(uri));
        Assert.False(HostAllowlist.Catalog.TryParse(url, out _));
    }

    [Theory]
    [InlineData(
        "https://software.download.prss.microsoft.com/dbazure/Windows11_Client_x64_en-us_26300_9457.iso?t=abc&sig=def")]
    [InlineData("https://software-download.microsoft.com/pr/Win11.ISO")]
    [InlineData("https://download.microsoft.com/download/a/b/c/Win11.iso")]
    public void Iso_allows_iso_files_on_its_hosts(string url)
    {
        Assert.True(HostAllowlist.Iso.IsAllowed(new Uri(url)));
    }

    [Theory]
    [InlineData("https://software.download.prss.microsoft.com/dbazure/setup.exe")] // not an ISO
    [InlineData("https://software.download.prss.microsoft.com/dbazure/file?name=x.iso")] // ".iso" only in the query
    [InlineData("https://software.download.prss.microsoft.com/dbazure/x.iso/setup.exe")]
    [InlineData("https://software.download.prss.microsoft.com/dbazure/setup.exe#x.iso")] // ".iso" only in the fragment
    [InlineData("https://software.download.prss.microsoft.com/dbazure/x.iso/")]
    [InlineData("https://software.download.prss.microsoft.com/")]
    [InlineData("http://software.download.prss.microsoft.com/dbazure/x.iso")]
    [InlineData("https://software.download.prss.microsoft.com:444/dbazure/x.iso")]
    [InlineData("https://www.microsoft.com/x.iso")] // a catalog host is not an ISO host
    [InlineData("https://software.download.prss.microsoft.com.evil.example/x.iso")]
    [InlineData("https://download.microsoft.com.evil.example/x.iso")]
    [InlineData("https://evil.example/x.iso")]
    public void Iso_refuses_everything_else(string url)
    {
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));
        Assert.False(HostAllowlist.Iso.IsAllowed(uri));
        Assert.False(HostAllowlist.Iso.TryParse(url, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("www.microsoft.com")] // no scheme
    [InlineData("/en-us/software-download/windows11")] // relative
    [InlineData("//www.microsoft.com/")]
    [InlineData("https://www.microsoft.com\\@evil.example/")] // backslash trick
    [InlineData("https://www.microsoft.com/\r\nHost: evil.example")] // header injection
    [InlineData("https://www.microsoft.com/\t")]
    [InlineData("javascript:alert(1)")]
    public void TryParse_refuses_text_that_is_not_a_clean_absolute_link(string? text)
    {
        Assert.False(HostAllowlist.Catalog.TryParse(text, out var uri));
        Assert.Null(uri);
    }

    [Fact]
    public void Check_refuses_null_and_relative_addresses()
    {
        Assert.NotNull(HostAllowlist.Catalog.Check(null));
        Assert.NotNull(HostAllowlist.Catalog.Check(new Uri("/relative", UriKind.Relative)));
        Assert.False(HostAllowlist.Catalog.IsAllowed(null));
    }

    [Fact]
    public void The_reason_and_the_host_description_never_contain_path_or_query()
    {
        var uri = new Uri("https://evil.example/secret-path/file.iso?sig=SECRETTOKEN");
        var reason = HostAllowlist.Iso.Check(uri);
        Assert.NotNull(reason);
        Assert.DoesNotContain("SECRETTOKEN", reason);
        Assert.DoesNotContain("secret-path", reason);

        Assert.Equal("evil.example", HostAllowlist.DescribeHost(uri));
        Assert.Equal("an unknown address", HostAllowlist.DescribeHost(null));
    }

    [Fact]
    public void The_lists_are_the_documented_ones()
    {
        Assert.Equal(
            ["ov-df.microsoft.com", "vlscppe.microsoft.com", "www.microsoft.com"],
            HostAllowlist.Catalog.Hosts.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["download.microsoft.com", "software-download.microsoft.com", "software.download.prss.microsoft.com"],
            HostAllowlist.Iso.Hosts.Order(StringComparer.Ordinal));
    }
}