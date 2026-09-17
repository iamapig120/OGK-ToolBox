using OGKToolBox.Infrastructure.Charts;

namespace OGKToolBox.Tests;

public sealed class OgkrSerializerTests
{
    [Fact]
    public void PreservesUnknownCommandsAndExactLineEndings()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "chart.ogkr");
        const string source = "[HEADER]\r\nCREATOR\t譜面作者\r\nBPM_DEF\t180.5\r\n[EXTRA]\r\nFUTURE_COMMAND\talpha\tbeta\r\n";
        File.WriteAllText(path, source);

        var serializer = new OgkrSerializer();
        var document = serializer.Read(path);

        Assert.Equal("譜面作者", document.HeaderValue("CREATOR"));
        Assert.Contains(document.FindSection("EXTRA")!.Lines, line => line.Command == "FUTURE_COMMAND");
        Assert.Equal(source, serializer.WriteToString(document));
    }

    [Fact]
    public void ReportsMissingHeaderWithoutRejectingDocument()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "chart.ogkr");
        File.WriteAllText(path, "[LANE]\nTAP\t0\t0\n");
        var document = new OgkrSerializer().Read(path);
        Assert.Contains(document.Diagnostics, item => item.Code == "OGKR_HEADER_MISSING");
    }
}
