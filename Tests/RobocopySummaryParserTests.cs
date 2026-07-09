using GuardWui3.Services;
using Xunit;

namespace GuardWui3.Tests;

public class RobocopySummaryParserTests
{
    private static readonly string[] Block =
    {
        "------------------------------------------------------------------------------",
        "",
        "               Total    Copied   Skipped  Mismatch    FAILED    Extras",
        "    Dirs :        13         2        11         0         0         0",
        "   Files :       132        10       122         0         1         3",
        "   Bytes :   1.234 g    10.5 m     1.2 g         0     500 k     100 k",
    };

    [Fact]
    public void ParsesOneSummaryBlock()
    {
        var p = new RobocopySummaryParser();
        foreach (var line in Block) p.Feed(line);

        Assert.Equal(1, p.Blocks);
        Assert.Equal(10, p.FilesCopied);
        Assert.Equal(122, p.FilesSkipped);
        Assert.Equal(1, p.FilesFailed);
        Assert.Equal(3, p.FilesExtras);
        Assert.Equal(10.5 * 1024 * 1024, p.BytesCopied, precision: 0);
    }

    [Fact]
    public void AccumulatesAcrossBlocksAndIgnoresChatter()
    {
        var p = new RobocopySummaryParser();
        p.Feed("   New File  \t\t  123\tC:\\somewhere\\file.txt");
        foreach (var line in Block) p.Feed(line);
        p.Feed("random text between folders");
        foreach (var line in Block) p.Feed(line);

        Assert.Equal(2, p.Blocks);
        Assert.Equal(20, p.FilesCopied);
        Assert.Equal(244, p.FilesSkipped);
    }

    [Fact]
    public void ShortDashRunsDoNotArmTheParser()
    {
        var p = new RobocopySummaryParser();
        p.Feed("----------");                    // decorative, under the 20-dash floor
        p.Feed("               Total    Copied   Skipped  Mismatch    FAILED    Extras");
        p.Feed("    Dirs :         1         1         0         0         0         0");
        Assert.Equal(0, p.Blocks);
    }

    [Fact]
    public void CommaDecimalSeparatorParses()
    {
        var p = new RobocopySummaryParser();
        foreach (var line in new[]
        {
            "--------------------------------------------------",
            "               Total    Copied   Skipped  Mismatch    FAILED    Extras",
            "    Dirs :         1         1         0         0         0         0",
            "   Files :         2         2         0         0         0         0",
            "   Bytes :     2,5 m     2,5 m         0         0         0         0",
        }) p.Feed(line);
        Assert.Equal(1, p.Blocks);
        Assert.Equal(2.5 * 1024 * 1024, p.BytesCopied, precision: 0);
    }
}
