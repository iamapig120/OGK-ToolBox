using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Charts;
using OGKToolBox.Infrastructure.Scanning;

namespace OGKToolBox.Tests;

public sealed class MusicMetadataReaderTests
{
    [Fact]
    public void ReadsUnicodeMetadataChartAndHundredthLevelConstant()
    {
        using var temp = new TempDirectory();
        var musicDirectory = temp.CreateDirectory("music", "music0123");
        var sourceDirectory = temp.CreateDirectory("musicsource", "musicsource0123");
        File.WriteAllText(System.IO.Path.Combine(musicDirectory, "0123_03.ogkr"), "[HEADER]\nCREATOR\t譜面作者\nBPM_DEF\t180.5\nT_TOTAL\t777\n[LANE]\nTAP\t0\n");
        File.WriteAllBytes(System.IO.Path.Combine(sourceDirectory, "music0123.acb"), [1]);
        var xmlPath = System.IO.Path.Combine(musicDirectory, "Music.xml");
        File.WriteAllText(xmlPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <MusicData><dataName>music0123</dataName><Name><id>123</id><str>テスト楽曲</str></Name>
            <ArtistName><str>中文作者</str></ArtistName><Genre><str>オンゲキ</str></Genre>
            <MusicSourceName><id>123</id></MusicSourceName><VersionID><str>ver_1_50</str></VersionID>
            <ReleaseVersion>2026-01-02T00:00:00</ReleaseVersion><FumenData><FumenData>
            <FumenConstIntegerPart>12</FumenConstIntegerPart><FumenConstFractionalPart>50</FumenConstFractionalPart>
            <FumenFile><path>0123_03.ogkr</path></FumenFile></FumenData></FumenData></MusicData>
            """);
        var package = new DataPackage("A000", temp.Path, new Version(1, 50), 0, true);

        var music = new MusicMetadataReader(new OgkrSerializer()).Read(xmlPath, package, new Dictionary<string, GameResource>());

        Assert.Equal("テスト楽曲", music.Title);
        Assert.Equal("中文作者", music.Artist);
        Assert.Equal(12.50m, music.Charts[0].LevelConstant);
        Assert.Equal("譜面作者", music.Charts[0].Creator);
        Assert.Equal(777, music.Charts[0].TotalNotes);
        Assert.NotNull(music.Audio);
    }
}
