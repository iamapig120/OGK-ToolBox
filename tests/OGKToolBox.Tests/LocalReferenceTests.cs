using OGKToolBox.Infrastructure.Charts;

namespace OGKToolBox.Tests;

public sealed class LocalReferenceTests
{
    [Fact]
    public void ParsesConfiguredMuConvertOngekiSamples()
    {
        var root = Environment.GetEnvironmentVariable("OGKTOOLBOX_MUCONVERT_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var sampleRoot = System.IO.Path.Combine(root, "tests", "ogk", "testset");
        var paths = Directory.EnumerateFiles(sampleRoot, "*.ogkr", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(paths);
        var builder = new OgkrChartPreviewBuilder(new OgkrSerializer());

        var previews = paths.Select(builder.Build).ToArray();

        Assert.All(previews, preview =>
        {
            Assert.NotEmpty(preview.Tempos);
            Assert.True(preview.DurationSeconds > 0);
            Assert.NotEmpty(preview.Lanes);
        });
        Assert.Contains(previews, preview => preview.ScrollRanges.Count > 0);
        Assert.Contains(previews.SelectMany(preview => preview.Notes), note =>
            note.Kind == OGKToolBox.Core.Models.ChartPreviewNoteKind.Bullet);
    }
}
