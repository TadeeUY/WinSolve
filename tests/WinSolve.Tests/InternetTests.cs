using System.Net;
using WinSolve.Services;
using WinSolve.UI;
using Xunit;

namespace WinSolve.Tests;

public class InternetTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress Mask = IPAddress.Parse("255.255.255.0");

    private const string ArpOutput = """

        Interface: 192.168.1.20 --- 0x7
          Internet Address      Physical Address      Type
          192.168.1.1           aa-bb-cc-dd-ee-01     dynamic
          192.168.1.34          12-34-56-78-9a-bc     dynamic
          192.168.1.255         ff-ff-ff-ff-ff-ff     static
          224.0.0.22            01-00-5e-00-00-16     static
          239.255.255.250       01-00-5e-7f-ff-fa     static
          255.255.255.255       ff-ff-ff-ff-ff-ff     static

        Interface: 10.0.0.5 --- 0x9
          10.0.0.1              aa-bb-cc-dd-ee-02     dynamic
        """;

    [Fact]
    public void Arp_output_gives_only_devices_of_this_network()
    {
        var devices = RouterService.ParseArp(ArpOutput, Local, Mask);
        Assert.Equal(2, devices.Count);
        Assert.Contains(("192.168.1.1", "AA:BB:CC:DD:EE:01"), devices);
        Assert.Contains(("192.168.1.34", "12:34:56:78:9A:BC"), devices);
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("http://192.168.0.1:8080/", true)]
    [InlineData("https://10.0.0.1", true)]
    [InlineData("172.16.5.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("http://example.com", false)]
    [InlineData("ftp://192.168.1.1", false)]
    [InlineData("http://admin:pass@192.168.1.1", false)]
    [InlineData("", false)]
    public void Only_private_network_addresses_are_accepted_for_the_router(string text, bool expected)
        => Assert.Equal(expected, RouterService.TryParseAddress(text, out _));

    [Fact]
    public void A_made_up_address_is_described_as_private()
    {
        Assert.Equal("Private address (phone, tablet or virtual machine)", RouterService.Describe("DA:A1:19:00:00:01"));
        Assert.Equal("Raspberry Pi", RouterService.Describe("B8:27:EB:11:22:33"));
        Assert.Equal("", RouterService.Describe("AC:84:C6:11:22:33"));
    }

    [Fact]
    public void Verdict_names_where_the_problem_is()
    {
        Assert.Contains("can't reach the router", new InternetCheckResult(null, null, false).Verdict);
        Assert.Contains("no Internet", new InternetCheckResult(2, null, false).Verdict);
        Assert.Contains("DNS", new InternetCheckResult(2, 20, false).Verdict);
        Assert.Contains("slow", new InternetCheckResult(2, 400, true).Verdict);
        Assert.Contains("Everything works", new InternetCheckResult(2, 20, true).Verdict);
    }

    [Fact]
    public void The_sign_in_script_keeps_the_credentials_as_plain_text_literals()
    {
        var script = RouterDialog.FillScript("ad\"min", "p');alert(1);//\\");
        // The serializer writes quotes and angle brackets as \uXXXX, so nothing typed can end the string literal.
        Assert.Contains("\"ad\\u0022min\"", script);
        Assert.DoesNotContain("ad\"min", script);
        Assert.DoesNotContain("p');alert(1)", script);
        Assert.EndsWith(")", script);
    }

    [Fact]
    public void The_router_password_is_stored_encrypted_and_comes_back()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protectedText = RouterVault.Protect("s3cret-ñ");
        Assert.DoesNotContain("s3cret", protectedText);
        Assert.Equal("s3cret-ñ", RouterVault.Unprotect(protectedText));
    }

    [Fact]
    public void Every_internet_string_has_a_Spanish_translation()
    {
        string[] keys =
        [
            "Internet", "Your router", "Block or remove in the router", "Scan the network",
            "This PC can't reach the router. Check the cable or the Wi-Fi connection, then restart the router.",
            "Everything works: the router, the Internet and the DNS.",
            "Private address (phone, tablet or virtual machine)",
            "Through {0}. This PC: {1}", "Collapse the menu", "Expand the menu",
        ];
        foreach (var k in keys) Assert.True(WinSolve.Localization.Spanish.Table.ContainsKey(k), k);
    }
}

public class RouterDetectionTests
{
    [Fact]
    public void A_UPnP_description_gives_brand_and_model()
    {
        const string xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <friendlyName>WANDevice</friendlyName>
                <manufacturer>ZTE Corporation</manufacturer>
                <modelName>ZXHN F670L</modelName>
              </device>
            </root>
            """;
        var id = RouterDetector.ParseUpnp(xml);
        Assert.NotNull(id);
        Assert.Equal("ZTE", id!.Brand);
        Assert.Equal("ZXHN F670L", id.Model);
        Assert.Equal("ZTE ZXHN F670L", id.Display);
    }

    [Fact]
    public void A_UPnP_description_with_a_doctype_is_refused()
        => Assert.Null(RouterDetector.ParseUpnp("<!DOCTYPE x [<!ENTITY a \"b\">]><root><device><modelName>&a;</modelName></device></root>"));

    [Fact]
    public void The_sign_in_page_title_gives_the_brand()
    {
        var id = RouterDetector.FromText("", "<html><head><title>TP-Link Archer C6</title></head></html>");
        Assert.Equal("TP-Link", id!.Brand);
        Assert.Equal("Archer C6", id.Model);
        Assert.Null(RouterDetector.FromText("", "<html><title>Login</title></html>"));
    }

    [Fact]
    public void Only_a_location_on_the_router_itself_is_followed()
    {
        var gateway = System.Net.IPAddress.Parse("192.168.1.1");
        Assert.NotNull(RouterDetector.LocationOf("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.1:52869/rootDesc.xml\r\n", gateway));
        Assert.Null(RouterDetector.LocationOf("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.50/rootDesc.xml\r\n", gateway));
        Assert.Null(RouterDetector.LocationOf("HTTP/1.1 200 OK\r\nLOCATION: http://evil.example.com/x.xml\r\n", gateway));
    }
}

public class WifiChannelTests
{
    private static WifiAp Ap(int channel, int rssi, WifiBand band = WifiBand.Ghz24)
        => new("net", "AA:BB:CC:00:00:" + channel.ToString("X2"), channel, 0, rssi, band);

    [Theory]
    [InlineData(2412, 1)]
    [InlineData(2437, 6)]
    [InlineData(2462, 11)]
    [InlineData(2484, 14)]
    [InlineData(5180, 36)]
    [InlineData(5745, 149)]
    [InlineData(5955, 1)]
    [InlineData(1234, 0)]
    public void Frequency_gives_channel(int mhz, int channel) => Assert.Equal(channel, ChannelPlanner.ChannelOf(mhz));

    [Fact]
    public void The_least_crowded_of_the_three_clean_channels_is_recommended()
    {
        // Crowded on 1 and 6, nothing on 11.
        var aps = new[] { Ap(1, -50), Ap(1, -60), Ap(6, -45), Ap(6, -70), Ap(4, -65) };
        var loads = ChannelPlanner.Score(aps, WifiBand.Ghz24, currentChannel: 6);
        Assert.Equal(11, loads.Single(l => l.IsBest).Channel);
        Assert.True(loads.Single(l => l.Channel == 6).IsCurrent);
        Assert.False(ChannelPlanner.CurrentIsFine(loads));
    }

    [Fact]
    public void Staying_put_when_the_current_channel_is_already_as_good_as_any()
    {
        var aps = new[] { Ap(6, -80), Ap(1, -50), Ap(11, -50) };
        var loads = ChannelPlanner.Score(aps, WifiBand.Ghz24, currentChannel: 6);
        Assert.Equal(6, loads.Single(l => l.IsBest).Channel);
        Assert.True(ChannelPlanner.CurrentIsFine(loads));
    }

    [Fact]
    public void A_strong_neighbor_counts_more_than_a_far_one_and_5GHz_channels_do_not_overlap()
    {
        Assert.True(ChannelPlanner.Weight(-45) > ChannelPlanner.Weight(-85));
        Assert.Equal(0, ChannelPlanner.Overlap(WifiBand.Ghz5, 36, 40));
        Assert.Equal(1, ChannelPlanner.Overlap(WifiBand.Ghz5, 36, 36));
        Assert.True(ChannelPlanner.Overlap(WifiBand.Ghz24, 6, 7) > ChannelPlanner.Overlap(WifiBand.Ghz24, 6, 9));
        Assert.Equal(0, ChannelPlanner.Overlap(WifiBand.Ghz24, 1, 11));
    }
}

public class DnsBenchmarkTests
{
    [Fact]
    public void The_question_is_a_standard_A_query()
    {
        var q = DnsBenchmark.BuildQuery("example.com", 0x5301);
        Assert.Equal(0x53, q[0]);
        Assert.Equal(0x01, q[1]);
        Assert.Equal(1, q[5]);                     // one question
        Assert.Equal(7, q[12]);                    // "example"
        Assert.Equal(0, q[^5]);                    // end of the name
        Assert.Equal(new byte[] { 0, 1, 0, 1 }, q[^4..]);   // type A, class IN
    }

    [Fact]
    public void Only_a_matching_reply_without_error_counts()
    {
        byte[] ok = [0x53, 0x01, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0];
        Assert.True(DnsBenchmark.IsAnswer(ok, 0x5301));
        Assert.False(DnsBenchmark.IsAnswer(ok, 0x5302));                                        // another question
        Assert.False(DnsBenchmark.IsAnswer([0x53, 0x01, 0x81, 0x83, 0, 1, 0, 0, 0, 0, 0, 0], 0x5301)); // NXDOMAIN
        Assert.False(DnsBenchmark.IsAnswer([0x53, 0x01], 0x5301));
    }

    [Fact]
    public void The_median_ignores_one_slow_lookup()
    {
        Assert.Equal(12.0, DnsBenchmark.Median([10, 12, 400])!.Value);
        Assert.Equal(11.0, DnsBenchmark.Median([10, 12])!.Value);
        Assert.Null(DnsBenchmark.Median([]));
    }
}

public class SidebarSettingTests
{
    [Fact]
    public void The_side_menu_starts_expanded()
        => Assert.False(new WinSolve.Core.AppSettings().SidebarCollapsed);
}
