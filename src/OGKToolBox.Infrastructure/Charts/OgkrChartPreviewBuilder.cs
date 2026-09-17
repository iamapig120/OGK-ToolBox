using System.Globalization;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Infrastructure.Charts;

public sealed class OgkrChartPreviewBuilder(IChartSerializer serializer) : IChartPreviewBuilder
{
    private static readonly Dictionary<string, ChartPreviewLaneKind> LaneKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WL"] = ChartPreviewLaneKind.LeftWall,
        ["LL"] = ChartPreviewLaneKind.Left,
        ["LC"] = ChartPreviewLaneKind.Center,
        ["LR"] = ChartPreviewLaneKind.Right,
        ["WR"] = ChartPreviewLaneKind.RightWall,
        ["CL"] = ChartPreviewLaneKind.Colorful,
        ["BM"] = ChartPreviewLaneKind.Beam,
        ["OB"] = ChartPreviewLaneKind.Beam
    };

    private static readonly HashSet<string> KnownNonVisualCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "VERSION", "CREATOR", "BPM_DEF", "MET_DEF", "TRESOLUTION", "XRESOLUTION", "CLK_DEF",
        "PROGJUDGE_BPM", "TUTORIAL", "BULLET_DAMAGE", "HARDBULLET_DAMAGE", "DANGERBULLET_DAMAGE",
        "BEAM_DAMAGE", "T_TOTAL", "T_TAP", "T_HOLD", "T_SIDE", "T_SHOLD", "T_FLICK", "T_BELL",
        "BPM", "MET", "SFL", "EST", "LBK", "ISF", "CLK"
    };

    private sealed record BulletPalette(ChartPreviewBulletShooter Shooter, double PlaceOffset,
        ChartPreviewBulletTarget Target, double Speed, bool IsLarge, ChartPreviewBulletShape Shape);

    private sealed record LaneStyle(int ColorId = -1, int BrightnessId = -1, int BeamWidthId = 0,
        int BeamShootPositionOffset = 0, bool IsObliqueBeam = false);

    public ChartPreview Build(string path)
    {
        var document = serializer.Read(path);
        var resolution = Math.Max(1, document.HeaderInt("TRESOLUTION"));
        var lanePoints = new Dictionary<(int Id, ChartPreviewLaneKind Kind), List<(long Tick, double X)>>();
        var laneStyles = new Dictionary<(int Id, ChartPreviewLaneKind Kind), LaneStyle>();
        var bulletPalettes = new Dictionary<string, BulletPalette>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<ChartPreviewNote>();
        var tempoValues = new List<(long Tick, double Bpm)>();
        var scrollRanges = new List<ChartScrollRange>();
        var unknown = 0;
        long maxTick = resolution * 4L;

        foreach (var line in document.Sections.SelectMany(section => section.Lines)
                     .Where(line => line.Command?.Equals("BPL", StringComparison.OrdinalIgnoreCase) == true))
            if (TryReadBulletPalette(line.Arguments, out var paletteId, out var palette))
                bulletPalettes[paletteId] = palette;

        foreach (var line in document.Sections.SelectMany(section => section.Lines).Where(line => line.Command is not null))
        {
            var command = line.Command!;
            if (command.Equals("BPL", StringComparison.OrdinalIgnoreCase)
                && TryReadBulletPalette(line.Arguments, out var paletteId, out var palette))
            {
                bulletPalettes[paletteId] = palette;
                continue;
            }
            if (command.Equals("BPM", StringComparison.OrdinalIgnoreCase) && line.Arguments.Count >= 3
                && TryTick(line.Arguments[0], line.Arguments[1], resolution, out var tempoTick)
                && TryDouble(line.Arguments[2], out var bpm) && bpm > 0)
            {
                tempoValues.Add((tempoTick, bpm));
                continue;
            }
            if (command.Equals("SFL", StringComparison.OrdinalIgnoreCase) && line.Arguments.Count >= 4
                && TryTick(line.Arguments[0], line.Arguments[1], resolution, out var scrollTick)
                && TryInt(line.Arguments[2], out var duration) && TryDouble(line.Arguments[3], out var multiplier))
            {
                scrollRanges.Add(new(scrollTick, scrollTick + Math.Max(0, duration), multiplier));
                continue;
            }
            if (TryReadLane(command, line.Arguments, resolution, out var laneKey, out var lanePoint))
            {
                if (!lanePoints.TryGetValue(laneKey, out var points)) lanePoints[laneKey] = points = [];
                points.Add(lanePoint);
                if (command[2] == 'S') laneStyles[laneKey] = ReadLaneStyle(command, line.Arguments);
                maxTick = Math.Max(maxTick, lanePoint.Tick);
                continue;
            }

            if (TryReadNote(command, line.Arguments, resolution, bulletPalettes, out var note))
            {
                notes.Add(note);
                maxTick = Math.Max(maxTick, Math.Max(note.Tick, note.EndTick));
                continue;
            }

            if (!KnownNonVisualCommands.Contains(command)) unknown++;
        }

        var defaultBpm = (double)document.HeaderDecimal("BPM_DEF");
        if (defaultBpm <= 0) defaultBpm = 120;
        if (!tempoValues.Any(item => item.Tick == 0)) tempoValues.Add((0, defaultBpm));
        var tempos = BuildTempoMap(tempoValues, resolution);

        var lanes = lanePoints.Select(pair =>
            {
                var style = laneStyles.GetValueOrDefault(pair.Key) ?? new LaneStyle();
                return new ChartPreviewLane(pair.Key.Id, pair.Key.Kind, NormalizeLanePoints(pair.Value),
                    style.ColorId, style.BrightnessId, style.BeamWidthId,
                    style.BeamShootPositionOffset, style.IsObliqueBeam);
            })
            .OrderBy(lane => lane.Kind).ThenBy(lane => lane.Id).ToArray();
        var laneById = lanes.GroupBy(lane => lane.Id).ToDictionary(group => group.Key, group => group.First());
        var resolvedNotes = notes.Select(note => laneById.TryGetValue(note.LaneId, out var lane)
                ? note with { LaneKind = lane.Kind }
                : note)
            .OrderBy(note => note.Tick).ToArray();

        return new ChartPreview
        {
            SourcePath = path,
            Resolution = resolution,
            MaxTick = maxTick,
            Lanes = lanes,
            Notes = resolvedNotes,
            Tempos = tempos,
            ScrollRanges = scrollRanges.OrderBy(range => range.StartTick).ToArray(),
            UnknownCommandCount = unknown
        };
    }

    private static bool TryReadLane(string command, IReadOnlyList<string> args, int resolution,
        out (int Id, ChartPreviewLaneKind Kind) key, out (long Tick, double X) point)
    {
        key = default;
        point = default!;
        if (command.Length != 3 || !LaneKinds.TryGetValue(command[..2], out var kind)
            || command[2] is not ('S' or 'N' or 'E') || args.Count < 4
            || !TryInt(args[0], out var id) || !TryTick(args[1], args[2], resolution, out var tick)
            || !TryDouble(args[3], out var x)) return false;
        key = (id, kind);
        point = new(tick, x);
        return true;
    }

    private static bool TryReadNote(string command, IReadOnlyList<string> args, int resolution,
        IReadOnlyDictionary<string, BulletPalette> bulletPalettes, out ChartPreviewNote note)
    {
        note = default!;
        if ((command.Equals("TAP", StringComparison.OrdinalIgnoreCase) || command.Equals("CTP", StringComparison.OrdinalIgnoreCase)
                || command.Equals("XTP", StringComparison.OrdinalIgnoreCase)) && args.Count >= 5
            && TryInt(args[0], out var tapLaneId)
            && TryTick(args[1], args[2], resolution, out var tapTick) && TryXGrid(args[3], args[4], out var tapX))
            note = new(ChartPreviewNoteKind.Tap, tapTick, tapX, IsCritical: !command.Equals("TAP", StringComparison.OrdinalIgnoreCase), LaneId: tapLaneId);
        else if ((command.Equals("HLD", StringComparison.OrdinalIgnoreCase) || command.Equals("CHD", StringComparison.OrdinalIgnoreCase)
                || command.Equals("XHD", StringComparison.OrdinalIgnoreCase)) && args.Count >= 9
            && TryInt(args[0], out var holdLaneId)
            && TryTick(args[1], args[2], resolution, out var holdTick) && TryXGrid(args[3], args[4], out var holdX)
            && TryTick(args[5], args[6], resolution, out var endTick) && TryXGrid(args[7], args[8], out var endX))
            note = new(ChartPreviewNoteKind.Hold, holdTick, holdX, endTick, endX,
                !command.Equals("HLD", StringComparison.OrdinalIgnoreCase), holdLaneId);
        else if ((command.Equals("FLK", StringComparison.OrdinalIgnoreCase) || command.Equals("CFK", StringComparison.OrdinalIgnoreCase)) && args.Count >= 3
            && TryTick(args[0], args[1], resolution, out var flickTick) && TryDouble(args[2], out var flickX))
            note = new(ChartPreviewNoteKind.Flick, flickTick, flickX,
                IsCritical: command.Equals("CFK", StringComparison.OrdinalIgnoreCase),
                IsRight: args.Count >= 4 && args[3].Equals("R", StringComparison.OrdinalIgnoreCase));
        else if (command.Equals("BEL", StringComparison.OrdinalIgnoreCase) && args.Count >= 3
            && TryTick(args[0], args[1], resolution, out var bellTick) && TryDouble(args[2], out var bellX))
            note = new(ChartPreviewNoteKind.Bell, bellTick, bellX);
        else if (command.Equals("BLT", StringComparison.OrdinalIgnoreCase) && args.Count >= 5
            && TryTick(args[1], args[2], resolution, out var bulletTick) && TryDouble(args[3], out var bulletX))
        {
            var palette = bulletPalettes.GetValueOrDefault(args[0])
                ?? new BulletPalette(ChartPreviewBulletShooter.TargetHead, 0,
                    ChartPreviewBulletTarget.Player, 1, false, ChartPreviewBulletShape.Circle);
            note = new(ChartPreviewNoteKind.Bullet, bulletTick, bulletX,
                BulletShape: palette.Shape,
                BulletStrength: args[4].ToUpperInvariant() switch
                {
                    "STR" => ChartPreviewBulletStrength.Hard,
                    "DNG" => ChartPreviewBulletStrength.Danger,
                    _ => ChartPreviewBulletStrength.Normal
                },
                BulletShooter: palette.Shooter, BulletTarget: palette.Target,
                BulletSpeed: palette.Speed, IsLargeBullet: palette.IsLarge,
                BulletPlaceOffset: palette.PlaceOffset);
        }
        else return false;
        return true;
    }

    private static LaneStyle ReadLaneStyle(string command, IReadOnlyList<string> args)
    {
        if (command.StartsWith("CL", StringComparison.OrdinalIgnoreCase) && args.Count >= 6)
            return new(TryInt(args[4], out var color) ? color : -1,
                TryInt(args[5], out var brightness) ? brightness : -1);
        if ((command.StartsWith("BM", StringComparison.OrdinalIgnoreCase)
             || command.StartsWith("OB", StringComparison.OrdinalIgnoreCase)) && args.Count >= 5)
            return new(BeamWidthId: TryInt(args[4], out var width) ? width : 0,
                BeamShootPositionOffset: args.Count >= 6 && TryInt(args[5], out var offset) ? offset : 0,
                IsObliqueBeam: command.StartsWith("OB", StringComparison.OrdinalIgnoreCase));
        return new();
    }

    private static bool TryReadBulletPalette(IReadOnlyList<string> args, out string id, out BulletPalette palette)
    {
        id = args.Count > 0 ? args[0] : string.Empty;
        palette = default!;
        if (args.Count < 8 || string.IsNullOrWhiteSpace(id)
            || !TryDouble(args[2], out var offset) || !TryDouble(args[4], out var speed)) return false;
        var shooter = args[1].ToUpperInvariant() switch
        {
            "ENE" => ChartPreviewBulletShooter.Enemy,
            "CEN" => ChartPreviewBulletShooter.Center,
            _ => ChartPreviewBulletShooter.TargetHead
        };
        var target = args[3].Equals("FIX", StringComparison.OrdinalIgnoreCase)
            ? ChartPreviewBulletTarget.FixedField : ChartPreviewBulletTarget.Player;
        var shape = args[6].ToUpperInvariant() switch
        {
            "NDL" => ChartPreviewBulletShape.Needle,
            "SQR" => ChartPreviewBulletShape.Square,
            _ => ChartPreviewBulletShape.Circle
        };
        palette = new(shooter, offset, target, speed,
            args[5].Equals("L", StringComparison.OrdinalIgnoreCase), shape);
        return true;
    }

    private static IReadOnlyList<ChartPreviewPoint> NormalizeLanePoints(IEnumerable<(long Tick, double X)> rawPoints) =>
        rawPoints.OrderBy(point => point.Tick)
            .GroupBy(point => point.Tick)
            .Select(group => new ChartPreviewPoint(group.Key, group.First().X, group.Last().X))
            .ToArray();

    private static bool TryXGrid(string placeValue, string gridValue, out double result)
    {
        result = 0;
        if (!TryDouble(placeValue, out var place) || !TryDouble(gridValue, out var grid)) return false;
        result = place + grid / 4096d;
        return true;
    }

    private static IReadOnlyList<ChartTempoPoint> BuildTempoMap(IEnumerable<(long Tick, double Bpm)> values, int resolution)
    {
        var sorted = values.GroupBy(item => item.Tick).Select(group => group.Last()).OrderBy(item => item.Tick).ToArray();
        var result = new List<ChartTempoPoint>(sorted.Length);
        double seconds = 0;
        for (var index = 0; index < sorted.Length; index++)
        {
            if (index > 0)
            {
                var previous = sorted[index - 1];
                seconds += (sorted[index].Tick - previous.Tick) / (double)resolution * 240d / previous.Bpm;
            }
            result.Add(new(sorted[index].Tick, seconds, sorted[index].Bpm));
        }
        return result;
    }

    private static bool TryTick(string measureValue, string tickValue, int resolution, out long tick)
    {
        tick = 0;
        if (!TryInt(measureValue, out var measure) || !TryInt(tickValue, out var offset)) return false;
        tick = measure * (long)resolution + offset;
        return true;
    }

    private static bool TryInt(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryDouble(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
