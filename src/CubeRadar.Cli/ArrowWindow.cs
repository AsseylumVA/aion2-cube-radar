using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CubeRadar.Core.Minimap;
using CubeRadar.Core.Radar;

namespace CubeRadar.Cli;

/// <summary>
/// A green arrow over the character that points to the nearest cube, turned by the camera direction from the minimap
/// (<see cref="MinimapHeading"/>), with distance and height below it. Mouse clicks go through it to the game; only while
/// the settings window is open can it be dragged (position saved on release). Shown and hidden by the
/// <see cref="OverlayWindow"/>, which applies the same rules as for the radar (game in front, no menu).
/// The window itself is created only when an arrow mode is shown. Its scale uses the monitor DPI, the same way the
/// radar does: <see cref="DeviceDpi"/> stayed at the system DPI when awareness was set late.
/// </summary>
internal sealed class ArrowWindow : Form
{
    private const int WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExTopmost = 0x8, WsExTransparent = 0x20;
    private const int WmNcHitTest = 0x84, WmMouseActivate = 0x21, WmExitSizeMove = 0x232, HtCaption = 2, HtTransparent = -1, MaNoActivate = 3;
    private const int GwlExStyle = -20;
    private const byte AcSrcAlpha = 0x1;
    private const int UlwAlpha = 0x2;

    private readonly RadarSettings _settings;
    private readonly MinimapHeading _heading;
    private readonly Func<RadarSnapshot?> _snapshot;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private bool _positioning;
    private bool _timerDisposed;
    private int _dpiDepth;
    private double? _shownAngle;

    public ArrowWindow(RadarSettings settings, MinimapHeading heading, Func<RadarSnapshot?> snapshot)
    {
        _settings = settings;
        _heading = heading;
        _snapshot = snapshot;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Cube Radar Arrow";
        _timer.Tick += (_, _) => Redraw();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate | WsExTopmost | WsExTransparent;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmNcHitTest:
                m.Result = _positioning ? HtCaption : HtTransparent; // Windows moves it while positioning; else clicks fall through
                return;
            case WmMouseActivate:
                m.Result = MaNoActivate;
                return;
            case WmExitSizeMove:
                _settings.ArrowX = Left + Width / 2;
                _settings.ArrowY = Top + Height / 2;
                _settings.Save();
                break;
        }
        base.WndProc(ref m);
    }

    /// <summary>Real monitor DPI. See <see cref="OverlayWindow"/> for why <see cref="DeviceDpi"/> is not enough.</summary>
    private int ArrowDpi
    {
        get
        {
            if (IsHandleCreated)
            {
                var monitor = MonitorFromWindow(Handle, 2); // MONITOR_DEFAULTTONEAREST
                if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
                    return (int)dpiX;
                var windowDpi = (int)GetDpiForWindow(Handle);
                if (windowDpi > 0) return windowDpi;
            }
            return DeviceDpi > 0 ? DeviceDpi : 96;
        }
    }

    private float ScaleFor(int dpi) => dpi / 96f * Math.Clamp(_settings.ArrowScalePct, 50, 250) / 100f;

    private float RenderScale => ScaleFor(ArrowDpi);

    protected override bool OnGetDpiScaledSize(int deviceDpiOld, int deviceDpiNew, ref Size desiredSize)
    {
        desiredSize = ArrowRenderer.Measure(ScaleFor(deviceDpiNew));
        return true;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        if (_dpiDepth > 0) return;
        _dpiDepth++;
        try
        {
            base.OnDpiChanged(e);
            ApplySettings();
        }
        finally
        {
            _dpiDepth--;
        }
    }

    /// <summary>While the settings are open the arrow can be dragged, and shows a sample if there is no cube.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Positioning
    {
        get => _positioning;
        set
        {
            if (_positioning == value) return;
            _positioning = value;
            if (!IsHandleCreated) return;
            ApplyClickThrough();
            Redraw();
        }
    }

    public void SetShown(bool show)
    {
        if (show == Visible && (!show || IsHandleCreated)) return;
        if (show)
        {
            _ = Handle; // created here, not while the mode is still the mini-radar
            ApplyClickThrough();
            ApplySettings();
            if (!Visible) Show();
            if (!_timer.Enabled) _timer.Start();
        }
        else if (IsHandleCreated)
        {
            _timer.Stop();
            _shownAngle = null;
            Hide();
        }
    }

    private void ApplyClickThrough()
    {
        var style = GetWindowLong(Handle, GwlExStyle);
        SetWindowLong(Handle, GwlExStyle, _positioning ? style & ~WsExTransparent : style | WsExTransparent);
    }

    /// <summary>Size and position after a settings change (or when it shows).</summary>
    public void ApplySettings()
    {
        if (!IsHandleCreated) return;
        var size = ArrowRenderer.Measure(RenderScale);
        if (Size != size) Size = size;
        var centre = _settings.ArrowX is { } x && _settings.ArrowY is { } y && OnSomeScreen(new Point(x, y))
            ? new Point(x, y)
            : DefaultCentre();
        Location = new Point(centre.X - Width / 2, centre.Y - Height / 2);
        Redraw();
    }

    public void ResetPosition()
    {
        _settings.ArrowX = _settings.ArrowY = null;
        _settings.Save();
        ApplySettings();
    }

    /// <summary>Above the character: the middle of the game window, 40 % from the top (the camera keeps the character centred).</summary>
    private Point DefaultCentre()
    {
        var area = _heading.GameClientRect() ?? Screen.PrimaryScreen!.Bounds;
        return new Point(area.Left + area.Width / 2, area.Top + (int)(area.Height * 0.40));
    }

    private void Redraw()
    {
        if (!IsHandleCreated) return;
        var size = ArrowRenderer.Measure(RenderScale);
        if (size.Width <= 0 || size.Height <= 0) return;
        if (Size != size) Size = size;
        var frame = ArrowFrame.From(_snapshot(), _heading.Current?.YawDeg);
        // Smooth the turn a little (the minimap is read about 12 times a second, the arrow drawn about 30 times).
        if (frame?.AngleDeg is { } target)
        {
            _shownAngle = _shownAngle is { } shown ? shown + 0.45 * SignedDifference(target, shown) : target;
            frame = frame with { AngleDeg = ViewConeDetector.Normalize(_shownAngle.Value) };
        }
        else _shownAngle = null;

        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            ArrowRenderer.Render(g, RenderScale, frame, _positioning, Environment.TickCount64);
        ApplyOpacity(bitmap, Math.Clamp(_settings.ArrowOpacityPct, 40, 100));
        var hbitmap = OverlayWindow.PremultipliedBitmap(bitmap);
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var old = SelectObject(dc, hbitmap);
        try
        {
            var source = Point.Empty;
            var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref size, dc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteDC(dc);
            DeleteObject(hbitmap);
        }
    }

    /// <summary>Window opacity baked into the pixels, then premultiplied the same way as the radar bitmap.</summary>
    private static void ApplyOpacity(Bitmap frame, int pct)
    {
        var data = frame.LockBits(new Rectangle(Point.Empty, frame.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[frame.Width * 4];
            for (var y = 0; y < frame.Height; y++)
            {
                var line = data.Scan0 + y * data.Stride;
                Marshal.Copy(line, row, 0, row.Length);
                for (var i = 3; i < row.Length; i += 4)
                {
                    var a = (byte)(row[i] * pct / 100);
                    row[i] = a;
                    var f = a / 255f;
                    row[i - 3] = (byte)(row[i - 3] * f + 0.5f);
                    row[i - 2] = (byte)(row[i - 2] * f + 0.5f);
                    row[i - 1] = (byte)(row[i - 1] * f + 0.5f);
                }
                Marshal.Copy(row, 0, line, row.Length);
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }
    }

    /// <summary>a − b in −180…180°.</summary>
    private static double SignedDifference(double a, double b) => ((a - b) % 360 + 540) % 360 - 180;

    private static bool OnSomeScreen(Point p) => Screen.AllScreens.Any(s => s.Bounds.Contains(p));

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopTimer();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopTimer();
        base.Dispose(disposing);
    }

    private void StopTimer()
    {
        if (_timerDisposed) return;
        _timerDisposed = true;
        _timer.Stop();
        _timer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref Size psize, IntPtr hdcSrc,
        ref Point pptSrc, int crKey, ref BlendFunction pblend, int flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}

/// <summary>What the arrow shows: direction relative to the camera, distance, height, and how much to trust it.</summary>
/// <param name="AngleDeg">0 = straight ahead (into the screen), 90 = to the right; null = camera direction unknown.</param>
/// <param name="DistanceM">Planar distance; null = own position unknown.</param>
internal sealed record ArrowFrame(double? AngleDeg, double? DistanceM, double? HeightM, string Status, string? Note, bool Unsure)
{
    /// <summary>The nearest cube that is visible or being opened, else the nearest one out of view; null if there is none.</summary>
    public static ArrowFrame? From(RadarSnapshot? s, double? cameraYaw)
    {
        if (s is null) return null;
        var me = OverlayRenderer.Me(s);
        var cubes = s.Cubes.Where(c => c.Status is nameof(CubeStatus.Visible) or nameof(CubeStatus.Opening)).ToList();
        if (cubes.Count == 0) cubes = s.Cubes.Where(c => c.Status == nameof(CubeStatus.OutOfView)).ToList();
        if (cubes.Count == 0) return null;
        if (me is null) return new ArrowFrame(null, null, null, cubes[0].Status, UiText.T("arrow.posUnknown"), true);

        var cube = cubes.MinBy(c => Units.Planar(me.X, me.Y, c.X, c.Y))!;
        var o = s.Orientation;
        double dx = cube.X - me.X, dy = cube.Y - me.Y;
        var bearing = Math.Atan2(dx * o.EastX + dy * o.EastY, dx * o.NorthX + dy * o.NorthY) * 180 / Math.PI;
        var distance = Units.Planar(me.X, me.Y, cube.X, cube.Y) / Units.PerMetre;

        string? note = null;
        var unsure = false;
        if (me.Ring > 0)
        {
            note = UiText.T("arrow.posRing", me.Ring / Units.PerMetre);
            unsure = true;
        }
        else if (me.AgeMs > s.FixFreshSeconds * 1000)
        {
            note = UiText.T("arrow.posAge", me.AgeMs / 1000);
            unsure = true;
        }
        if (cameraYaw is null) note = UiText.T("arrow.minimapMiss");
        else if (note is null && cube.Status == nameof(CubeStatus.OutOfView)) note = UiText.T("arrow.outOfView");
        else if (note is null && cube.Status == nameof(CubeStatus.Opening)) note = UiText.T("arrow.opening");

        return new ArrowFrame(cameraYaw is { } yaw ? ViewConeDetector.Normalize(bearing - yaw) : null, distance,
            cube.DzUnits is { } dz ? dz / Units.PerMetre : null, cube.Status, note, unsure);
    }
}

/// <summary>Draws the arrow: a green slab lying flat in perspective, so "ahead" points into the screen.</summary>
internal static class ArrowRenderer
{
    private const float Width = 240, Height = 200, Cx = 120, Cy = 78, Length = 62, Squash = 0.55f, Thickness = 9;
    private const double HereMetres = 4;

    // Outline of the arrow lying flat, pointing to +v (ahead); about its middle.
    private static readonly (double U, double V)[] Shape =
        [(0, 0.96), (0.56, 0.16), (0.21, 0.16), (0.21, -0.89), (-0.21, -0.89), (-0.21, 0.16), (-0.56, 0.16)];

    public static Size Measure(float scale) => new((int)(Width * scale), (int)(Height * scale));

    public static void Render(Graphics g, float scale, ArrowFrame? f, bool positioning, long nowMs)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(scale, scale);

        if (positioning)
        {
            using var frame = new Pen(Color.FromArgb(200, Palette.Me), 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(frame, 1, 1, Width - 2, Height - 2);
            f ??= new ArrowFrame(35, 87, 12, nameof(CubeStatus.Visible), UiText.T("arrow.sample"), false);
        }
        if (f is null) return;

        var color = f.Status switch
        {
            nameof(CubeStatus.Opening) => Palette.Opening,
            nameof(CubeStatus.OutOfView) => Palette.OutOfView,
            _ => Palette.Visible,
        };
        var alpha = f.Unsure ? 150 : 255;

        if (f.DistanceM is < HereMetres) Here(g, color, nowMs);
        else if (f.AngleDeg is { } angle) Arrow(g, angle, color, alpha);
        else Unknown(g);

        if (f.DistanceM is { } d)
        {
            var main = d < HereMetres ? UiText.T("arrow.here") : $"{d:0} m";
            var height = RelativeHeight.Arrow(f.HeightM);
            using var big = Medium(15f, scale);
            using var mid = Medium(10.5f, scale);
            var mainWidth = MeasureLogical(g, main, big).Width;
            var heightWidth = height is null ? 0 : MeasureLogical(g, height, mid).Width;
            var x = Cx - (mainWidth + heightWidth) / 2;
            Outlined(g, main, big, color, x, 136);
            if (height is not null) Outlined(g, height, mid, color, x + mainWidth - 4, 143);
        }
        if (f.Note is { } note)
        {
            using var small = Medium(9f, scale);
            Outlined(g, note, small, f.Unsure || f.AngleDeg is null ? Palette.OutOfView : Palette.Text, Cx - MeasureLogical(g, note, small).Width / 2, 168);
        }
    }

    /// <summary>
    /// Segoe UI Semibold at the final pixel size. Same face as the radar. Bold on that family synthesizes a heavier stroke.
    /// A world scale must not stretch these glyphs: the outline is drawn in device pixels.
    /// </summary>
    private static Font Medium(float points, float scale) =>
        new("Segoe UI Semibold", points * scale * 96f / 72f, FontStyle.Regular, GraphicsUnit.Pixel);

    private static float DeviceScale(Graphics g)
    {
        var e = g.Transform.Elements;
        var s = MathF.Sqrt(e[0] * e[0] + e[1] * e[1]);
        return s < 0.01f ? 1f : s;
    }

    private static SizeF MeasureLogical(Graphics g, string text, Font font)
    {
        var scale = DeviceScale(g);
        var state = g.Save();
        g.ResetTransform();
        var size = g.MeasureString(text, font);
        g.Restore(state);
        return new SizeF(size.Width / scale, size.Height / scale);
    }

    private static PointF Project(double u, double v, double angleDeg, float lift = 0)
    {
        var a = angleDeg * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        var x = u * cos + v * sin; // turned clockwise: 90° = to the right
        var y = -u * sin + v * cos;
        return new PointF(Cx + (float)(x * Length), Cy - (float)(y * Length * Squash) + lift);
    }

    private static void Arrow(Graphics g, double angle, Color color, int alpha)
    {
        var top = Shape.Select(p => Project(p.U, p.V, angle)).ToArray();
        var bottom = top.Select(p => new PointF(p.X, p.Y + Thickness)).ToArray();

        using (var shadowPath = new GraphicsPath())
        {
            var rx = Length * 1.05f;
            var ry = Length * Squash * 1.05f;
            shadowPath.AddEllipse(Cx - rx, Cy + Thickness + 6 - ry, 2 * rx, 2 * ry);
            using var shadow = new PathGradientBrush(shadowPath)
            {
                CenterColor = Color.FromArgb(alpha * 70 / 255, 0, 0, 0),
                SurroundColors = [Color.FromArgb(0, 0, 0, 0)],
            };
            g.FillPath(shadow, shadowPath);
        }

        var side = Color.FromArgb(alpha, Shade(color, 0.45));
        using (var sideBrush = new SolidBrush(side))
        {
            g.FillPolygon(sideBrush, bottom);
            for (var i = 0; i < top.Length; i++)
            {
                var j = (i + 1) % top.Length;
                g.FillPolygon(sideBrush, [top[i], top[j], bottom[j], bottom[i]]);
            }
        }

        var tip = top[0];
        var tail = Project(0, -0.89, angle);
        if (Math.Abs(tip.X - tail.X) + Math.Abs(tip.Y - tail.Y) < 1) tail = new PointF(tip.X, tip.Y + 1);
        using (var face = new LinearGradientBrush(tail, tip, Color.FromArgb(alpha, color), Color.FromArgb(alpha, Lighten(color, 0.45))))
            g.FillPolygon(face, top);
        using var outline = new Pen(Color.FromArgb(alpha, Shade(color, 0.3)), 1.4f) { LineJoin = LineJoin.Round };
        g.DrawPolygon(outline, top);
    }

    /// <summary>At the cube: a pulsing ring.</summary>
    private static void Here(Graphics g, Color color, long nowMs)
    {
        var phase = nowMs % 1200 / 1200f;
        for (var k = 0; k < 2; k++)
        {
            var p = (phase + k * 0.5f) % 1f;
            var r = 14 + p * 40;
            using var ring = new Pen(Color.FromArgb((int)(230 * (1 - p)), color), 3);
            g.DrawEllipse(ring, Cx - r, Cy - r * Squash, 2 * r, 2 * r * Squash);
        }
    }

    /// <summary>No camera direction: a grey ring with a question mark instead of a direction that could be wrong.</summary>
    private static void Unknown(Graphics g)
    {
        using var ring = new Pen(Color.FromArgb(200, Palette.Muted), 3);
        g.DrawEllipse(ring, Cx - 26, Cy - 26, 52, 52);
        using var font = Medium(18f, DeviceScale(g));
        Outlined(g, "?", font, Palette.Muted, Cx - MeasureLogical(g, "?", font).Width / 2, Cy - 20);
    }

    /// <summary>
    /// Text with a dark outline, readable on any background. The path is built at the final pixel size and filled
    /// without a world scale, so a scale change does not stretch a 96 DPI outline. Grayscale anti-alias: ClearType
    /// fringes tear on the premultiplied layered window.
    /// </summary>
    private static void Outlined(Graphics g, string text, Font font, Color color, float x, float y)
    {
        var scale = DeviceScale(g);
        var origin = new[] { new PointF(x, y) };
        g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, origin);
        var state = g.Save();
        g.ResetTransform();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, origin[0], StringFormat.GenericTypographic);
        using var outline = new Pen(Color.FromArgb(210, 8, 12, 16), 3.5f * scale) { LineJoin = LineJoin.Round };
        g.DrawPath(outline, path);
        using var fill = new SolidBrush(color);
        g.FillPath(fill, path);
        g.Restore(state);
    }

    private static Color Shade(Color c, double f) => Color.FromArgb((int)(c.R * f), (int)(c.G * f), (int)(c.B * f));

    private static Color Lighten(Color c, double f) =>
        Color.FromArgb(c.R + (int)((255 - c.R) * f), c.G + (int)((255 - c.G) * f), c.B + (int)((255 - c.B) * f));
}
