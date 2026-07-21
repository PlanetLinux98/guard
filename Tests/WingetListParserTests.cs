using System.Linq;
using GuardWui3.Services;
using Xunit;

namespace GuardWui3.Tests;

public class WingetListParserTests
{
    // Column layout matters: the parser slices by the header's "Id" offset.
    private const string Sample =
        "spinner\r" +
        "Name                  Id                        Version    Available  Source\r\n" +
        "--------------------------------------------------------------------------------\r\n" +
        "Visual Studio Code    Microsoft.VisualStudio…   1.90.0     1.91.0     winget\r\n" +
        "Cool App              9NBLGGH4NNS1              Unknown               msstore\r\n" +
        "Legacy Thing          ARP\\Machine\\X64\\Legacy    2.0\r\n" +
        "Some Very Long Name…  SomeVendor.SomeApp        3.1                   winget\r\n" +
        "NoVersion App         Vendor.NoVer                                    winget\r\n" +
        "Old App               Vendor.OldApp                        2.0.0      winget\r\n";

    [Fact]
    public void ParsesSourcesVersionsAndTruncation()
    {
        var rows = WingetListParser.Parse(Sample);
        Assert.Equal(6, rows.Count);

        var vsc = rows[0];
        Assert.Equal("Visual Studio Code", vsc.Name);
        Assert.False(vsc.NameTruncated);
        Assert.False(vsc.CanAuto);            // id itself is truncated
        Assert.Equal("1.90.0", vsc.Version);
        Assert.Equal("winget", vsc.Source);

        var store = rows[1];
        Assert.Equal("msstore", store.Source);
        Assert.Equal("Unknown", store.Version);
        Assert.True(store.CanAuto);

        var arp = rows[2];
        Assert.Equal("", arp.Source);
        Assert.Equal("2.0", arp.Version);
        Assert.False(arp.CanAuto);            // ARP\ synthetic id

        var truncated = rows[3];
        Assert.True(truncated.NameTruncated);
        Assert.Equal("Some Very Long Name", truncated.Name);
        Assert.True(truncated.CanAuto);

        // Empty Version cell: the source token must NOT be misread as the
        // version (the bug this parser was extracted to fix).
        var noVer = rows[4];
        Assert.Equal("", noVer.Version);
        Assert.Equal("winget", noVer.Source);
        Assert.True(noVer.CanAuto);

        // Version blank but Available populated: tokenizes to the exact same
        // [id, X, source] shape as a populated Version with Available blank
        // (vsc above), so X must not be misread as the installed Version -
        // that would make an app with no known installed version look
        // already up to date.
        var oldApp = rows[5];
        Assert.Equal("", oldApp.Version);
        Assert.Equal("winget", oldApp.Source);
        Assert.True(oldApp.CanAuto);
    }

    [Fact]
    public void LocalizedHeaderFallsBackToDashLineAndSecondWord()
    {
        // No "Name"/"Id"/"Version" words at all: header found via the all-dash
        // separator, the Id column via the second whitespace-delimited word.
        string sample =
            "Naam                  Kenn                      Versie\r\n" +
            "-----------------------------------------------------\r\n" +
            "Some App              Vendor.App                1.0\r\n";
        var rows = WingetListParser.Parse(sample);
        var row = Assert.Single(rows);
        Assert.Equal("Some App", row.Name);
        Assert.Equal("Vendor.App", row.Id);
        Assert.Equal("1.0", row.Version);
    }

    [Fact]
    public void GarbageInputYieldsNoRows()
    {
        Assert.Empty(WingetListParser.Parse(""));
        Assert.Empty(WingetListParser.Parse("no header here\r\njust text\r\n"));
    }
}
