using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CubeRadar.Core.Capture;
using CubeRadar.Core.Minimap;

namespace CubeRadar.Cli;

/// <summary>
/// The camera direction, read off the view cone on AION 2's north-up minimap (<see cref="ViewConeDetector"/>). Grabs the
/// small screen area around the minimap about 12 times a second, like a screenshot of that spot; nothing is read from the
/// game process. Runs on its own thread and only while something needs it (<see cref="Active"/>).
/// </summary>
internal sealed class MinimapHeading : IDisposable
{
    /// <summary>AION 2's default layout, measured at 2560×1440 (2026-11-07): centre 195 px from the right edge, 243 px from the top, radius 110 px.</summary>
    public const double DefaultRight = 195 / 1440.0, DefaultTop = 243 / 1440.0, DefaultRadius = 110 / 1440.0;
    /// <summary>A reading older than this no longer counts (minimap hidden, menu open, loading screen).</summary>
    public const int MaxAgeMs = 1500;
    private const int IntervalMs = 80;

    public sealed record Reading(double YawDeg, long AtMs, double Score);

    /// <summary>Where the minimap is on screen right now, in screen pixels.</summary>
    public readonly record struct Circle(double Cx, double Cy, double Radius);

    private enum Kind { Off, Reading, NoGame, Error, Seen, Miss, Search, Busy, NoGameCal, NotFound, Found, Standard }

    private readonly RadarSettings _settings;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly LumaImage _luma = new(1, 1);
    private readonly ViewConeTracker _tracker = new();
    private readonly object _captureGate = new();
    private readonly object _statusGate = new();
    private byte[] _pixels = [];
    private volatile bool _disposed;
    private volatile bool _active;
    private volatile bool _calibrating;
    private volatile Reading? _latest;
    private Kind _kind = Kind.Off;
    private string _detail = "";
    private double _yaw, _score, _cx, _cy, _radius;
    private bool _weak;
    private int _frames, _of;
    private Bitmap? _frame;
    private IntPtr _gameWindow;
    private long _gameWindowAt = -10_000;

    public MinimapHeading(RadarSettings settings)
    {
        _settings = settings;
        _thread = new Thread(Loop) { IsBackground = true, Name = "minimap", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Capture only while the arrow is on screen or the settings are open.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (!_calibrating) Set(value ? Kind.Reading : Kind.Off);
            if (value) _wake.Set();
        }
    }

    /// <summary>The last accepted reading (see <see cref="ViewConeTracker"/>), or null if there is none younger than <see cref="MaxAgeMs"/>.</summary>
    public Reading? Current => _latest is { } r && Environment.TickCount64 - r.AtMs <= MaxAgeMs ? r : null;

    /// <summary>One line for the settings window, in the active language.</summary>
    public string Status
    {
        get
        {
            lock (_statusGate)
            {
                return _kind switch
                {
                    Kind.Reading => UiText.T("heading.reading"),
                    Kind.NoGame => UiText.T("heading.noGame"),
                    Kind.Error => UiText.T("heading.error", _detail),
                    Kind.Seen => UiText.T("heading.seen", _yaw, Compass(_yaw), _score, _weak ? UiText.T("heading.weak") : ""),
                    Kind.Miss => UiText.T("heading.miss"),
                    Kind.Search => UiText.T("heading.search"),
                    Kind.Busy => UiText.T("heading.busy"),
                    Kind.NoGameCal => UiText.T("heading.noGameCal"),
                    Kind.NotFound => UiText.T("heading.notFound"),
                    Kind.Found => UiText.T("heading.found", _cx, _cy, _radius, _frames, _of),
                    Kind.Standard => UiText.T("heading.standard"),
                    _ => UiText.T("heading.off"),
                };
            }
        }
    }

    private void Loop()
    {
        while (!_disposed)
        {
            if (!_active || _calibrating)
            {
                _wake.WaitOne(500);
                continue;
            }
            var started = Environment.TickCount64;
            try
            {
                Step();
            }
            catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
            {
                Set(Kind.Error, ex.Message); // e.g. the secure desktop (UAC prompt) is up
            }
            var wait = IntervalMs - (int)(Environment.TickCount64 - started);
            if (wait > 0) _wake.WaitOne(wait);
        }
    }

    private void Step()
    {
        if (GameClientRect() is not { } client)
        {
            Set(Kind.NoGame);
            return;
        }
        var c = CircleFor(client);
        var side = (int)Math.Ceiling(2 * c.Radius) + 8;
        var area = new Rectangle((int)Math.Round(c.Cx) - side / 2, (int)Math.Round(c.Cy) - side / 2, side, side);
        ViewCone? cone;
        lock (_captureGate)
        {
            Grab(area);
            cone = ViewConeDetector.Detect(_luma, c.Cx - area.Left, c.Cy - area.Top, c.Radius);
        }
        var now = Environment.TickCount64;
        if (_tracker.Update(cone, now) is { } r)
        {
            _latest = new Reading(r.YawDeg, now, r.Score);
            Set(Kind.Seen, yaw: r.YawDeg, score: r.Score, weak: r.Weak);
        }
        else if (Current is null)
        {
            Set(Kind.Miss);
        }
    }

    /// <summary>The minimap circle for a game window client area, from the settings or AION 2's default layout.</summary>
    public Circle CircleFor(Rectangle client)
    {
        double h = client.Height;
        return new Circle(client.Right - (_settings.MinimapRight ?? DefaultRight) * h,
            client.Top + (_settings.MinimapTop ?? DefaultTop) * h,
            (_settings.MinimapRadius ?? DefaultRadius) * h);
    }

    /// <summary>
    /// Looks for the minimap in the top-right part of the game window (three captures, a moment apart) and saves where it
    /// is. Returns a message for the user. A new placement drops the previous track so a stale angle is not kept.
    /// </summary>
    public async Task<string> CalibrateAsync()
    {
        if (_calibrating) return Remember(Kind.Busy);
        _calibrating = true;
        try
        {
            return await Task.Run(async () =>
            {
                if (GameClientRect() is not { } client) return Remember(Kind.NoGameCal);
                double h = client.Height;
                var w = (int)(0.34 * h);
                var area = new Rectangle(client.Right - w, client.Top, w, (int)(0.36 * h));
                var frames = new List<LumaImage>();
                for (var i = 0; i < 3; i++)
                {
                    if (i > 0) await Task.Delay(300);
                    lock (_captureGate)
                    {
                        Grab(area);
                        frames.Add(_luma.Clone());
                    }
                }
                var found = ViewConeDetector.FindPlacement(frames, DefaultRadius * h);
                if (found is null) return Remember(Kind.NotFound);
                _settings.MinimapRight = (client.Right - (area.Left + found.Cx)) / h;
                _settings.MinimapTop = (area.Top + found.Cy - client.Top) / h;
                _settings.MinimapRadius = found.Radius / h;
                _settings.Save();
                _tracker.Reset();
                _latest = null;
                return Remember(Kind.Found, cx: area.Left + found.Cx, cy: area.Top + found.Cy, radius: found.Radius,
                    frames: found.Frames, of: frames.Count);
            });
        }
        finally
        {
            _calibrating = false;
            _wake.Set();
        }
    }

    /// <summary>Back to AION 2's default layout.</summary>
    public void ResetCalibration()
    {
        _settings.MinimapRight = _settings.MinimapTop = _settings.MinimapRadius = null;
        _settings.Save();
        _tracker.Reset();
        _latest = null;
        Set(Kind.Standard);
    }

    /// <summary>Screen pixels of the area into <see cref="_luma"/>. Callers hold <see cref="_captureGate"/>.</summary>
    private void Grab(Rectangle area)
    {
        if (area.Width < 1 || area.Height < 1) throw new ArgumentException("minimap area is empty");
        if (_frame is null || _frame.Width != area.Width || _frame.Height != area.Height)
        {
            _frame?.Dispose();
            _frame = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        }
        using (var g = Graphics.FromImage(_frame)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        var data = _frame.LockBits(new Rectangle(Point.Empty, _frame.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var length = data.Stride * data.Height;
            if (_pixels.Length != length) _pixels = new byte[length];
            Marshal.Copy(data.Scan0, _pixels, 0, length);
        }
        finally
        {
            _frame.UnlockBits(data);
        }
        _luma.LoadBgra(_pixels, area.Width, area.Height, data.Stride);
    }

    /// <summary>The client area of AION 2's main window in screen pixels; null if the game is not running or minimised.</summary>
    public Rectangle? GameClientRect()
    {
        var now = Environment.TickCount64;
        if (now - _gameWindowAt > 3_000 || !IsWindow(_gameWindow))
        {
            _gameWindow = FindGameWindow();
            _gameWindowAt = now;
        }
        if (_gameWindow == IntPtr.Zero || IsIconic(_gameWindow) || !GetClientRect(_gameWindow, out var r)) return null;
        var origin = new Point(0, 0);
        if (!ClientToScreen(_gameWindow, ref origin) || r.Right <= 0 || r.Bottom <= 0) return null;
        return new Rectangle(origin.X, origin.Y, r.Right, r.Bottom);
    }

    /// <summary>The largest visible top-level window of an AION 2 process.</summary>
    public static IntPtr FindGameWindow()
    {
        var pids = GameProcessLocator.FindGameProcessIds();
        if (pids.Length == 0) return IntPtr.Zero;
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid) || !GetClientRect(hwnd, out var r)) return true;
            long area = (long)r.Right * r.Bottom;
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        _thread.Join(1_000);
        _frame?.Dispose();
    }

    private string Remember(Kind kind, string detail = "", double cx = 0, double cy = 0, double radius = 0, int frames = 0, int of = 0)
    {
        Set(kind, detail, cx: cx, cy: cy, radius: radius, frames: frames, of: of);
        return Status;
    }

    private void Set(Kind kind, string detail = "", double yaw = 0, double score = 0, bool weak = false,
        double cx = 0, double cy = 0, double radius = 0, int frames = 0, int of = 0)
    {
        lock (_statusGate)
        {
            _kind = kind;
            _detail = detail;
            _yaw = yaw;
            _score = score;
            _weak = weak;
            _cx = cx;
            _cy = cy;
            _radius = radius;
            _frames = frames;
            _of = of;
        }
    }

    private static string Compass(double yaw)
    {
        string[] keys = ["heading.dir.N", "heading.dir.NE", "heading.dir.E", "heading.dir.SE",
            "heading.dir.S", "heading.dir.SW", "heading.dir.W", "heading.dir.NW"];
        return UiText.T(keys[(int)Math.Round(ViewConeDetector.Normalize(yaw) / 45) % 8]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
}
