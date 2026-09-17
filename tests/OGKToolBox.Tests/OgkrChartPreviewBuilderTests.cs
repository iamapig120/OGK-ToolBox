using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure.Charts;

namespace OGKToolBox.Tests;

public sealed class OgkrChartPreviewBuilderTests
{
    [Fact]
    public void ProjectsLanesAndNotesOntoAbsoluteTicks()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "preview.ogkr");
        File.WriteAllText(path, """
            [HEADER]
            TRESOLUTION	1920
            [LANE]
            LLS	2	1	960	-6
            LLN	2	2	0	-4
            LLE	2	3	0	-6
            [NOTES]
            TAP	2	2	0	-4	2048
            HLD	2	3	0	-6	0	4	960	-2	0
            [FLICK]
            FLK	5	0	1	R
            [BELL]
            BEL	6	0	0	--
            """);

        var preview = new OgkrChartPreviewBuilder(new OgkrSerializer()).Build(path);

        var lane = Assert.Single(preview.Lanes);
        Assert.Equal(ChartPreviewLaneKind.Left, lane.Kind);
        Assert.Equal([2880L, 3840L, 5760L], lane.Points.Select(point => point.Tick));
        Assert.Collection(preview.Notes,
            note =>
            {
                Assert.Equal(ChartPreviewNoteKind.Tap, note.Kind);
                Assert.Equal(2, note.LaneId);
                Assert.Equal(ChartPreviewLaneKind.Left, note.LaneKind);
                Assert.Equal(-3.5, note.X, 6);
            },
            note =>
            {
                Assert.Equal(ChartPreviewNoteKind.Hold, note.Kind);
                Assert.Equal(8640, note.EndTick);
                Assert.Equal(2, note.LaneId);
                Assert.Equal(ChartPreviewLaneKind.Left, note.LaneKind);
            },
            note => Assert.Equal(ChartPreviewNoteKind.Flick, note.Kind),
            note => Assert.Equal(ChartPreviewNoteKind.Bell, note.Kind));
    }

    [Fact]
    public void PreservesLaneCornersAndInterpolatesHoldPathLikeTheGameReader()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "corner.ogkr");
        File.WriteAllText(path, """
            [HEADER]
            TRESOLUTION	1920
            BPM_DEF	120
            [LANE]
            LCS	8	0	0	0
            LCN	8	1	0	3
            LCN	8	1	0	6
            LCE	8	2	0	12
            [NOTES]
            HLD	8	0	960	1	0	1	960	9	0
            """);

        var preview = new OgkrChartPreviewBuilder(new OgkrSerializer()).Build(path);
        var lane = Assert.Single(preview.Lanes);
        Assert.Equal(3, lane.Points.Count);
        Assert.Equal((3d, 6d), (lane.Points[1].XFore, lane.Points[1].XRear));
        Assert.Equal(1.5, lane.XAtTick(960), 6);
        Assert.Equal(9, lane.XAtTick(2880), 6);
        Assert.Equal(8, Assert.Single(preview.Notes).LaneId);
    }

    [Fact]
    public void CountsUnsupportedCommandsWithoutRejectingPreview()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "future.ogkr");
        File.WriteAllText(path, "[HEADER]\nTRESOLUTION\t1920\n[EXTRA]\nFUTURE\t1\t2\n");

        var preview = new OgkrChartPreviewBuilder(new OgkrSerializer()).Build(path);

        Assert.Equal(1, preview.UnknownCommandCount);
        Assert.Empty(preview.Notes);
    }

    [Fact]
    public void ConvertsTicksAndSecondsAcrossBpmChangesAndSoflan()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "timing.ogkr");
        File.WriteAllText(path, """
            [HEADER]
            TRESOLUTION	1920
            BPM_DEF	120	120	120	240
            [COMPOSITION]
            BPM	0	0	120
            BPM	2	0	240
            SFL	1	0	1920	0.5
            [LANE]
            LLS	1	0	0	-6
            LLE	1	4	0	-6
            """);

        var preview = new OgkrChartPreviewBuilder(new OgkrSerializer()).Build(path);

        Assert.Equal(4, preview.SecondsAtTick(3840), 6);
        Assert.Equal(5, preview.SecondsAtTick(5760), 6);
        Assert.Equal(5760, preview.TickAtSeconds(5));
        Assert.Equal(180, preview.ScrollFramePositionAtTick(3840), 6);
        Assert.Equal(165, preview.ScrollFramePositionAtSeconds(3.5), 6);
        Assert.Equal(15, preview.DrawFrameDeltaAtSeconds(3840, 3.5), 6);
        Assert.Equal(172.5, preview.ScrollFramePositionAtTick(3600d), 6);
        Assert.Equal(7.5, preview.DrawFrameDeltaAtSeconds(3600d, 3.5), 6);
        Assert.Equal(2.35, ChartPreview.GameSpeedValue(6), 6);
        Assert.Equal(30, ChartPreview.GameSpeedValue(20), 6);
    }

    [Fact]
    public void ProjectsBulletsAndBeamPaths()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "danger.ogkr");
        File.WriteAllText(path, """
            [HEADER]
            TRESOLUTION	1920
            BPM_DEF	180
            [BEAM]
            BMS	7	1	0	-12	5
            BMN	7	2	0	0	2
            BME	7	3	0	12	2
            [BULLET]
            BPL	A0	ENE	1.5	FIX	1.75	L	NDL	0
            BLT	A0	2	960	4	DNG
            """);

        var preview = new OgkrChartPreviewBuilder(new OgkrSerializer()).Build(path);

        var beam = Assert.Single(preview.Lanes);
        Assert.Equal(ChartPreviewLaneKind.Beam, beam.Kind);
        Assert.Equal(5, beam.BeamWidthId);
        var bullet = Assert.Single(preview.Notes);
        Assert.Equal(ChartPreviewNoteKind.Bullet, bullet.Kind);
        Assert.Equal(ChartPreviewBulletShape.Needle, bullet.BulletShape);
        Assert.Equal(ChartPreviewBulletStrength.Danger, bullet.BulletStrength);
        Assert.Equal(ChartPreviewBulletShooter.Enemy, bullet.BulletShooter);
        Assert.Equal(ChartPreviewBulletTarget.FixedField, bullet.BulletTarget);
        Assert.True(bullet.IsLargeBullet);
        Assert.Equal(1.75, bullet.BulletSpeed);
    }
}
