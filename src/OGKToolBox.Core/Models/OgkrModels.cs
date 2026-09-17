namespace OGKToolBox.Core.Models;

public sealed record OgkrLine(int LineNumber, string RawText, string? Command, IReadOnlyList<string> Arguments);

public sealed class OgkrSection
{
    public required string Name { get; init; }
    public required int HeaderLineNumber { get; init; }
    public required List<OgkrLine> Lines { get; init; }
}

public sealed class OgkrDocument
{
    public required string SourcePath { get; init; }
    public required string NewLine { get; init; }
    public required bool EndsWithNewLine { get; init; }
    public required IReadOnlyList<string> OriginalLines { get; init; }
    public required IReadOnlyList<OgkrSection> Sections { get; init; }
    public required IReadOnlyList<LibraryDiagnostic> Diagnostics { get; init; }

    public OgkrSection? FindSection(string name) =>
        Sections.FirstOrDefault(section => string.Equals(section.Name, name, StringComparison.OrdinalIgnoreCase));

    public string? HeaderValue(string command) =>
        FindSection("HEADER")?.Lines.FirstOrDefault(line =>
            string.Equals(line.Command, command, StringComparison.OrdinalIgnoreCase))?.Arguments.FirstOrDefault();

    public int HeaderInt(string command) => int.TryParse(HeaderValue(command), out var value) ? value : 0;

    public decimal HeaderDecimal(string command) =>
        decimal.TryParse(HeaderValue(command), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
}

public enum ChartPreviewLaneKind { LeftWall, Left, Center, Right, RightWall, Colorful, Beam }
public enum ChartPreviewNoteKind { Tap, Hold, Flick, Bell, Bullet }
public enum ChartPreviewBulletShape { Circle, Needle, Square }
public enum ChartPreviewBulletStrength { Normal, Hard, Danger }
public enum ChartPreviewBulletShooter { TargetHead, Enemy, Center }
public enum ChartPreviewBulletTarget { FixedField, Player }
public enum ChartPreviewSoundKind
{
    Tap,
    CriticalTap,
    Wall,
    CriticalWall,
    HoldLoop,
    HoldEnd,
    Flick,
    CriticalFlick,
    Bell,
    BeamNotice,
    BeamShot
}

public sealed record ChartPreviewPoint(long Tick, double XFore, double XRear)
{
    public double CenterX => (XFore + XRear) / 2d;
}

public sealed record ChartPreviewLane(int Id, ChartPreviewLaneKind Kind, IReadOnlyList<ChartPreviewPoint> Points,
    int ColorId = -1, int BrightnessId = -1, int BeamWidthId = 0,
    int BeamShootPositionOffset = 0, bool IsObliqueBeam = false)
{
    public long StartTick => Points.Count == 0 ? 0 : Points[0].Tick;
    public long EndTick => Points.Count == 0 ? 0 : Points[^1].Tick;
    public bool Contains(long tick) => Points.Count > 0 && tick >= StartTick && tick <= EndTick;

    public double XAtTick(long tick)
        => XAtTick((double)tick);

    public double XAtTick(double tick)
    {
        if (Points.Count == 0) return 0;
        if (tick <= Points[0].Tick) return Points[0].XFore;
        for (var index = 0; index < Points.Count - 1; index++)
        {
            var current = Points[index];
            var next = Points[index + 1];
            if (tick > next.Tick) continue;
            if (next.Tick == current.Tick) return next.XFore;
            var amount = (tick - current.Tick) / (double)(next.Tick - current.Tick);
            return current.XRear + (next.XFore - current.XRear) * amount;
        }
        return Points[^1].XRear;
    }
}
public sealed record ChartPreviewNote(ChartPreviewNoteKind Kind, long Tick, double X, long EndTick = 0,
    double EndX = 0, bool IsCritical = false, int LaneId = -1,
    ChartPreviewLaneKind? LaneKind = null, bool IsRight = false,
    ChartPreviewBulletShape BulletShape = ChartPreviewBulletShape.Circle,
    ChartPreviewBulletStrength BulletStrength = ChartPreviewBulletStrength.Normal,
    ChartPreviewBulletShooter BulletShooter = ChartPreviewBulletShooter.TargetHead,
    ChartPreviewBulletTarget BulletTarget = ChartPreviewBulletTarget.Player,
    double BulletSpeed = 1, bool IsLargeBullet = false, double BulletPlaceOffset = 0);
public sealed record ChartTempoPoint(long Tick, double Seconds, double Bpm);
public sealed record ChartScrollRange(long StartTick, long EndTick, double Multiplier);

public sealed class ChartPreview
{
    public required string SourcePath { get; init; }
    public required int Resolution { get; init; }
    public required long MaxTick { get; init; }
    public required IReadOnlyList<ChartPreviewLane> Lanes { get; init; }
    public required IReadOnlyList<ChartPreviewNote> Notes { get; init; }
    public required IReadOnlyList<ChartTempoPoint> Tempos { get; init; }
    public required IReadOnlyList<ChartScrollRange> ScrollRanges { get; init; }
    public required int UnknownCommandCount { get; init; }
    public double MeasureCount => Resolution <= 0 ? 0 : (double)MaxTick / Resolution;
    public double DurationSeconds => SecondsAtTick(MaxTick);

    public double SecondsAtTick(long tick)
        => SecondsAtTick((double)tick);

    public double SecondsAtTick(double tick)
    {
        var tempo = Tempos.LastOrDefault(item => item.Tick <= tick) ?? Tempos[0];
        return tempo.Seconds + (tick - tempo.Tick) / (double)Resolution * 240d / tempo.Bpm;
    }

    public long TickAtSeconds(double seconds)
    {
        var tempo = Tempos.LastOrDefault(item => item.Seconds <= seconds) ?? Tempos[0];
        return tempo.Tick + (long)Math.Round((seconds - tempo.Seconds) * tempo.Bpm / 240d * Resolution);
    }

    public double ScrollFramePositionAtTick(long tick)
        => ScrollFramePositionAtTick((double)tick);

    public double ScrollFramePositionAtTick(double tick)
    {
        var position = SecondsAtTick(tick) * 60d;
        foreach (var range in ScrollRanges)
        {
            var overlapStart = Math.Max(0, range.StartTick);
            var overlapEnd = Math.Min(tick, range.EndTick);
            if (overlapEnd <= overlapStart) continue;
            position += (SecondsAtTick(overlapEnd) - SecondsAtTick(overlapStart))
                * 60d * (range.Multiplier - 1d);
        }
        return position;
    }

    public double ScrollFramePositionAtSeconds(double seconds)
    {
        var clampedSeconds = Math.Max(0, seconds);
        var tempo = Tempos.LastOrDefault(item => item.Seconds <= clampedSeconds) ?? Tempos[0];
        var currentTick = tempo.Tick
            + (clampedSeconds - tempo.Seconds) * tempo.Bpm / 240d * Resolution;
        var position = clampedSeconds * 60d;
        foreach (var range in ScrollRanges)
        {
            var overlapStart = Math.Max(0d, range.StartTick);
            var overlapEnd = Math.Min(currentTick, range.EndTick);
            if (overlapEnd <= overlapStart) continue;
            position += (SecondsAtFractionalTick(overlapEnd) - SecondsAtFractionalTick(overlapStart))
                * 60d * (range.Multiplier - 1d);
        }
        return position;
    }

    public double DrawFrameDelta(long targetTick, long currentTick) =>
        ScrollFramePositionAtTick(targetTick) - ScrollFramePositionAtTick(currentTick);

    public double DrawFrameDeltaAtSeconds(long targetTick, double currentSeconds) =>
        ScrollFramePositionAtTick(targetTick) - ScrollFramePositionAtSeconds(currentSeconds);

    public double DrawFrameDeltaAtSeconds(double targetTick, double currentSeconds) =>
        ScrollFramePositionAtTick(targetTick) - ScrollFramePositionAtSeconds(currentSeconds);

    private double SecondsAtFractionalTick(double tick)
    {
        var tempo = Tempos.LastOrDefault(item => item.Tick <= tick) ?? Tempos[0];
        return tempo.Seconds + (tick - tempo.Tick) / Resolution * 240d / tempo.Bpm;
    }

    public static double GameSpeedValue(double displayedSpeed) =>
        displayedSpeed >= 20d ? 30d : 1d + (Math.Clamp(displayedSpeed, 1d, 19.75d) - 1d) * .27d;
}
