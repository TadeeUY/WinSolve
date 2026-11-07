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
        Assert.Contains("\"ad\\\"min\"", script);
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
            "Through {0}. This PC: {1}",
        ];
        foreach (var k in keys) Assert.True(WinSolve.Localization.Spanish.Table.ContainsKey(k), k);
    }
}
