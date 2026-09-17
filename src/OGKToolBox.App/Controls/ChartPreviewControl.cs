using System.Numerics;
using System.Windows;
using System.Windows.Media;
using OGKToolBox.Core.Models;

namespace OGKToolBox.App.Controls;

public sealed class ChartPreviewControl : FrameworkElement
{
    private const double VirtualWidth = 1080;
    private const double VirtualHeight = 1920;
    private const double JudgeZ = 7.5;
    private const double FarDrawZ = 40;
    private const double SceneOffsetY = 190;
    private const double SceneHorizontalScale = 1.32;
    private static readonly Vector3 CameraPosition = new(0, 4.75f, -1);
    private static readonly Vector3 CameraTarget = new(0, -3.5f, 25);
    private static readonly Vector3 CameraForward = Vector3.Normalize(CameraTarget - CameraPosition);
    private static readonly Vector3 CameraRight = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, CameraForward));
    private static readonly Vector3 CameraUp = Vector3.Cross(CameraForward, CameraRight);

    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(
        nameof(Preview), typeof(ChartPreview), typeof(ChartPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CurrentSecondsProperty = DependencyProperty.Register(
        nameof(CurrentSeconds), typeof(double), typeof(ChartPreviewControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ScrollSpeedProperty = DependencyProperty.Register(
        nameof(ScrollSpeed), typeof(double), typeof(ChartPreviewControl),
        new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChartPreview? Preview { get => (ChartPreview?)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public double CurrentSeconds { get => (double)GetValue(CurrentSecondsProperty); set => SetValue(CurrentSecondsProperty, value); }
    public double ScrollSpeed { get => (double)GetValue(ScrollSpeedProperty); set => SetValue(ScrollSpeedProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The game frame is always portrait. Asking the parent for that aspect ratio keeps the
        // player from becoming a large landscape panel with a narrow picture in its centre.
        var availableWidth = double.IsInfinity(availableSize.Width) ? VirtualWidth : availableSize.Width;
        var availableHeight = double.IsInfinity(availableSize.Height) ? VirtualHeight : availableSize.Height;
        var scale = Math.Min(availableWidth / VirtualWidth, availableHeight / VirtualHeight);
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        return new Size(VirtualWidth * scale, VirtualHeight * scale);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(4, 6, 11)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var preview = Preview;
        if (preview is null || ActualWidth < 40 || ActualHeight < 40) return;

        var scale = Math.Min(ActualWidth / VirtualWidth, ActualHeight / VirtualHeight);
        var offsetX = (ActualWidth - VirtualWidth * scale) / 2;
        var offsetY = (ActualHeight - VirtualHeight * scale) / 2;
        dc.PushTransform(new TranslateTransform(offsetX, offsetY));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, VirtualWidth, VirtualHeight)));
        var sceneBounds = new Rect(0, 0, VirtualWidth, VirtualHeight);
        var sceneBackground = new LinearGradientBrush(Color.FromRgb(6, 9, 18), Color.FromRgb(12, 17, 29), 90);
        dc.DrawRectangle(sceneBackground, null, sceneBounds);

        var currentTick = preview.TickAtSeconds(CurrentSeconds);
        var speed = ChartPreview.GameSpeedValue(ScrollSpeed);
        var visibleStartTick = Math.Max(0L, currentTick - preview.Resolution);
        var visibleEndTick = FindVisibleEndTick();

        // Render the complete look-ahead scene first, then paint the background back over the
        // regions outside the game's draw interval. Changing lane geometry at the near/far planes
        // made translucent ribbons enter and leave the tessellator as whole polygons, which could
        // flash even though the judge line itself was stable. A screen-space cover gives every
        // object a gradual, pixel-stable reveal instead.
        TryProjectAtZ(JudgeZ, 0, out var judgeCenter);
        TryProjectAtZ(FarDrawZ, 0, out var farCenter);
        var playfieldTop = Math.Clamp(farCenter.Y, 0, VirtualHeight);
        var playfieldBottom = Math.Clamp(judgeCenter.Y - 4, 0, VirtualHeight);
        DrawField();
        DrawMeasureLines();
        foreach (var lane in preview.Lanes.Where(item => item.Kind != ChartPreviewLaneKind.Beam)) DrawLane(lane);
        foreach (var hold in preview.Notes.Where(item => item.Kind == ChartPreviewNoteKind.Hold
                     && item.EndTick >= visibleStartTick && item.Tick <= visibleEndTick)) DrawHold(hold);
        foreach (var beam in preview.Lanes.Where(item => item.Kind == ChartPreviewLaneKind.Beam)) DrawLane(beam);
        for (var index = LowerBoundNote(visibleStartTick); index < preview.Notes.Count; index++)
        {
            var note = preview.Notes[index];
            if (note.Tick > visibleEndTick) break;
            DrawNote(note);
        }
        CoverOutsidePlayfield();
        DrawJudgeLine();

        dc.Pop();

        void CoverOutsidePlayfield()
        {
            Cover(new Rect(0, 0, VirtualWidth, playfieldTop));
            Cover(new Rect(0, playfieldBottom, VirtualWidth, VirtualHeight - playfieldBottom));

            void Cover(Rect bounds)
            {
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
                dc.PushClip(new RectangleGeometry(bounds));
                // Draw against the full scene bounds so the replacement pixels exactly match the
                // original gradient instead of restarting the gradient inside each mask rectangle.
                dc.DrawRectangle(sceneBackground, null, sceneBounds);
                dc.Pop();
            }
        }
        dc.Pop();
        dc.Pop();

        void DrawField()
        {
            var left = new List<Point>();
            var right = new List<Point>();
            var step = Math.Max(1L, preview.Resolution / 24L);
            for (var tick = Math.Max(0, currentTick - preview.Resolution); tick <= currentTick + preview.Resolution * 24L; tick += step)
            {
                var bounds = FieldBounds(tick);
                if (!TryProject(tick, bounds.Left, out var a) || !TryProject(tick, bounds.Right, out var b)) continue;
                left.Add(a); right.Add(b);
            }
            var geometry = Ribbon(left, right);
            if (geometry is not null)
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(18, 25, 40)),
                    new Pen(new SolidColorBrush(Color.FromRgb(54, 68, 96)), 2), geometry);
        }

        void DrawMeasureLines()
        {
            var step = Math.Max(1L, preview.Resolution / 2L);
            var first = Math.Max(0, currentTick / step * step);
            for (var tick = first; tick <= currentTick + preview.Resolution * 24L; tick += step)
            {
                var bounds = FieldBounds(tick);
                if (!TryProject(tick, bounds.Left, out var a) || !TryProject(tick, bounds.Right, out var b)) continue;
                var major = tick % (preview.Resolution * 4L) == 0;
                dc.DrawLine(new Pen(new SolidColorBrush(major
                    ? Color.FromArgb(130, 150, 171, 218) : Color.FromArgb(50, 150, 171, 218)), major ? 2.4 : 1.2), a, b);
            }
        }

        void DrawJudgeLine()
        {
            var bounds = FieldBounds(currentTick);
            if (!TryProjectAtZ(JudgeZ, bounds.Left, out var a) || !TryProjectAtZ(JudgeZ, bounds.Right, out var b)) return;
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(72, 255, 92, 54)), 28), a, b);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(255, 112, 65)), 6), a, b);
        }

        void DrawLane(ChartPreviewLane lane)
        {
            if (lane.Points.Count < 2) return;
            var color = LaneColor(lane.Kind);
            var halfWidth = lane.Kind switch
            {
                ChartPreviewLaneKind.LeftWall or ChartPreviewLaneKind.RightWall => 1.2,
                ChartPreviewLaneKind.Beam => 3.0,
                _ => .4
            };
            var left = new List<Point>();
            var right = new List<Point>();
            for (var index = 0; index < lane.Points.Count - 1; index++)
            {
                var a = lane.Points[index]; var b = lane.Points[index + 1];
                if (!TryVisibleTickRange(a.Tick, b.Tick, out var visibleStart, out var visibleEnd)) continue;
                var startZ = ZAtTick(visibleStart);
                var endZ = ZAtTick(visibleEnd);
                var subdivisions = Math.Clamp((int)Math.Ceiling(Math.Abs(endZ - startZ) / .35d), 2, 160);
                // Adjacent control-point spans belong to one strip. Drawing every span as a
                // separate translucent polygon made their shared seam brighten and dim. The
                // exact JudgeZ/FarDrawZ intersections are inserted before sampling, so a long
                // two-point lane can never lose its last sample above the mask and end in mid-air.
                for (var part = left.Count == 0 ? 0 : 1; part <= subdivisions; part++)
                {
                    var amount = part / (double)subdivisions;
                    var tick = visibleStart + (visibleEnd - visibleStart) * amount;
                    var spanAmount = b.Tick == a.Tick ? 1d : (tick - a.Tick) / (b.Tick - a.Tick);
                    var x = a.XRear + (b.XFore - a.XRear) * spanAmount;
                    if (!TryProject(tick, x - halfWidth, out var l) || !TryProject(tick, x + halfWidth, out var r)) continue;
                    left.Add(l); right.Add(r);
                }
            }
            var ribbon = Ribbon(left, right);
            if (ribbon is not null)
            {
                var alpha = lane.Kind == ChartPreviewLaneKind.Beam ? (byte)180 : (byte)205;
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
                    new Pen(new SolidColorBrush(Color.FromArgb(225, color.R, color.G, color.B)), 1.6), ribbon);
            }
        }

        void DrawHold(ChartPreviewNote note)
        {
            var lane = preview.Lanes.FirstOrDefault(item => item.Id == note.LaneId && item.Kind != ChartPreviewLaneKind.Beam);
            var left = new List<Point>(); var right = new List<Point>(); var center = new List<Point>();
            if (!TryVisibleTickRange(note.Tick, note.EndTick, out var visibleStart, out var visibleEnd)) return;
            var count = Math.Clamp((int)Math.Ceiling(Math.Abs(ZAtTick(visibleEnd) - ZAtTick(visibleStart)) / .3d), 12, 180);
            for (var index = 0; index <= count; index++)
            {
                var amount = index / (double)count;
                var tick = visibleStart + (visibleEnd - visibleStart) * amount;
                var holdAmount = note.EndTick == note.Tick ? 1d : (tick - note.Tick) / (note.EndTick - note.Tick);
                var x = lane?.XAtTick(tick) ?? note.X + (note.EndX - note.X) * holdAmount;
                if (!TryProject(tick, x - 3, out var l) || !TryProject(tick, x + 3, out var r)
                    || !TryProject(tick, x, out var c)) continue;
                left.Add(l); right.Add(r); center.Add(c);
            }
            var ribbon = Ribbon(left, right);
            if (ribbon is null) return;
            var color = LaneColor(note.LaneKind ?? ChartPreviewLaneKind.Center);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(145, color.R, color.G, color.B)),
                new Pen(new SolidColorBrush(Color.FromArgb(240, color.R, color.G, color.B)), 2.5), ribbon);
            if (center.Count > 1) dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), 3), Polyline(center));
        }

        void DrawNote(ChartPreviewNote note)
        {
            if (note.Kind == ChartPreviewNoteKind.Hold && IsPointVisible(note.EndTick)
                && TryProject(note.EndTick, note.EndX, out var end))
                DrawBar(end, note.EndTick, note.EndX, note, .72);
            if (!IsPointVisible(note.Tick)) return;
            if (!TryProject(note.Tick, note.X, out var point)) return;
            switch (note.Kind)
            {
                case ChartPreviewNoteKind.Tap:
                case ChartPreviewNoteKind.Hold: DrawBar(point, note.Tick, note.X, note, 1); break;
                case ChartPreviewNoteKind.Flick: DrawFlick(point, note); break;
                case ChartPreviewNoteKind.Bell:
                    var bellRadius = ProjectedHalfWidth(note.Tick, note.X, 1.15);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(80, 255, 210, 53)), null, point, bellRadius * 1.7, bellRadius * 1.7);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 210, 53)), new Pen(Brushes.White, 2), point, bellRadius, bellRadius);
                    break;
                case ChartPreviewNoteKind.Bullet:
                    var bulletRadius = ProjectedHalfWidth(note.Tick, note.X, .8);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(85, 87, 216, 255)), null, point, bulletRadius * 1.8, bulletRadius * 1.8);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(87, 216, 255)), new Pen(Brushes.White, 1.5), point, bulletRadius, bulletRadius);
                    break;
            }
        }

        void DrawBar(Point point, long tick, double x, ChartPreviewNote note, double opacity)
        {
            var color = LaneColor(note.LaneKind ?? ChartPreviewLaneKind.Center);
            var wall = note.LaneKind is ChartPreviewLaneKind.LeftWall or ChartPreviewLaneKind.RightWall;
            var halfWidth = wall ? 1.2 : 3d;
            if (!TryProject(tick, x - halfWidth, out var left) || !TryProject(tick, x + halfWidth, out var right)) return;
            var width = Math.Max(8, Math.Abs(right.X - left.X));
            var height = Math.Clamp(width * (wall ? .34 : .13), 5, 30);
            var rect = new Rect(point.X - width / 2, point.Y - height / 2, width, height);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(230 * opacity), color.R, color.G, color.B)),
                new Pen(note.IsCritical ? Brushes.Gold : Brushes.White, note.IsCritical ? 3.2 : 2), rect, height * .32, height * .32);
        }

        void DrawFlick(Point point, ChartPreviewNote note)
        {
            var size = Math.Clamp(ProjectedHalfWidth(note.Tick, note.X, 3), 12, 70);
            var direction = note.IsRight ? 1d : -1d;
            var color = note.IsCritical ? Color.FromRgb(255, 218, 73) : Color.FromRgb(70, 226, 243);
            var points = new[]
            {
                new Point(point.X - direction * size * .65, point.Y - size * .28),
                new Point(point.X + direction * size * .55, point.Y - size * .28),
                new Point(point.X + direction * size, point.Y),
                new Point(point.X + direction * size * .55, point.Y + size * .28),
                new Point(point.X - direction * size * .65, point.Y + size * .28)
            };
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(235, color.R, color.G, color.B)), new Pen(Brushes.White, 2), Polygon(points));
        }

        (double Left, double Right) FieldBounds(long tick)
        {
            var left = preview.Lanes.FirstOrDefault(item => item.Kind == ChartPreviewLaneKind.LeftWall && item.Contains(tick));
            var right = preview.Lanes.FirstOrDefault(item => item.Kind == ChartPreviewLaneKind.RightWall && item.Contains(tick));
            return (left?.XAtTick(tick) ?? -24, right?.XAtTick(tick) ?? 24);
        }

        double ZAtTick(double tick) =>
            JudgeZ + preview.DrawFrameDeltaAtSeconds(tick, CurrentSeconds) * .2d * speed;

        bool IsPointVisible(double tick)
        {
            var z = ZAtTick(tick);
            // A small overdraw margin keeps large note sprites from disappearing while their
            // centre is just outside the screen-space mask. Unlike lanes, point objects do not
            // need boundary tessellation, so rejecting the rest of the chart is safe and avoids
            // projecting thousands of fully covered notes every frame.
            return z >= JudgeZ - .65d && z <= FarDrawZ + .65d;
        }

        long FindVisibleEndTick()
        {
            if (currentTick >= preview.MaxTick || ZAtTick(preview.MaxTick) <= FarDrawZ)
                return preview.MaxTick;
            return (long)Math.Ceiling(TickAtZ(currentTick, preview.MaxTick, FarDrawZ + .65d));
        }

        int LowerBoundNote(long tick)
        {
            var low = 0;
            var high = preview.Notes.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (preview.Notes[middle].Tick < tick) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        bool TryVisibleTickRange(double startTick, double endTick, out double visibleStart, out double visibleEnd)
        {
            visibleStart = startTick;
            visibleEnd = endTick;
            if (endTick <= startTick) return false;
            var startZ = ZAtTick(startTick);
            var endZ = ZAtTick(endTick);
            if (endZ < JudgeZ || startZ > FarDrawZ) return false;
            if (startZ < JudgeZ) visibleStart = TickAtZ(startTick, endTick, JudgeZ);
            if (endZ > FarDrawZ) visibleEnd = TickAtZ(startTick, endTick, FarDrawZ);
            return visibleEnd > visibleStart;
        }

        double TickAtZ(double lowTick, double highTick, double targetZ)
        {
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var middle = (lowTick + highTick) / 2d;
                if (ZAtTick(middle) < targetZ) lowTick = middle;
                else highTick = middle;
            }
            return (lowTick + highTick) / 2d;
        }

        bool TryProject(double tick, double place, out Point point)
        {
            return TryProjectAtZ(ZAtTick(tick), place, out point);
        }

        bool TryProjectAtZ(double z, double place, out Point point)
        {
            var world = new Vector3((float)(place * .1 * SceneHorizontalScale), 0, (float)z);
            var delta = world - CameraPosition;
            var cameraX = Vector3.Dot(delta, CameraRight);
            var cameraY = Vector3.Dot(delta, CameraUp);
            var cameraZ = Vector3.Dot(delta, CameraForward);
            if (cameraZ <= .3f) { point = default; return false; }
            var tan = Math.Tan(Math.PI / 6d);
            var ndcX = cameraX / (cameraZ * tan * (VirtualWidth / VirtualHeight));
            var ndcY = cameraY / (cameraZ * tan);
            point = new((ndcX + 1) * VirtualWidth / 2, (1 - ndcY) * VirtualHeight / 2 + SceneOffsetY);
            return true;
        }

        double ProjectedHalfWidth(long tick, double x, double halfPlaces) =>
            TryProject(tick, x - halfPlaces, out var left) && TryProject(tick, x + halfPlaces, out var right)
                ? Math.Max(3, Math.Abs(right.X - left.X) / 2) : 3;
    }

    private static Geometry? Ribbon(IReadOnlyList<Point> left, IReadOnlyList<Point> right)
    {
        if (left.Count < 2 || right.Count < 2) return null;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(left[0], true, true);
        foreach (var point in left.Skip(1)) context.LineTo(point, true, false);
        foreach (var point in right.Reverse()) context.LineTo(point, true, false);
        return geometry;
    }

    private static Geometry Polyline(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(points[0], false, false);
        foreach (var point in points.Skip(1)) context.LineTo(point, true, false);
        return geometry;
    }

    private static Geometry Polygon(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(points[0], true, true);
        foreach (var point in points.Skip(1)) context.LineTo(point, true, false);
        return geometry;
    }

    private static Color LaneColor(ChartPreviewLaneKind kind) => kind switch
    {
        ChartPreviewLaneKind.LeftWall => Color.FromRgb(126, 101, 255),
        ChartPreviewLaneKind.Left => Color.FromRgb(255, 68, 86),
        ChartPreviewLaneKind.Center => Color.FromRgb(48, 225, 121),
        ChartPreviewLaneKind.Right => Color.FromRgb(48, 139, 255),
        ChartPreviewLaneKind.RightWall => Color.FromRgb(255, 70, 188),
        ChartPreviewLaneKind.Colorful => Color.FromRgb(255, 205, 69),
        _ => Color.FromRgb(218, 77, 255)
    };
}
