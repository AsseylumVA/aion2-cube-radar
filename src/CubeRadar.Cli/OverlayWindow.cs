using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CubeRadar.Core.Radar;
using CubeRadar.Core.Update;

namespace CubeRadar.Cli;

/// <summary>
/// Small always-on-top mini radar over the game: north up, we in the middle, live cubes with distance and height, our
/// recent trail. It never takes focus and can be dragged with the mouse whenever it is visible (position saved on
/// release); the mouse wheel zooms. Like other game overlays it hides while AION 2 is not the foreground window and,
/// optionally, while the mouse cursor is visible (menus / UI mode: in the world the game hides the cursor), also when
/// the cursor is over it, so it never covers a menu. A cursor that comes out while Alt is held (AION 2 shows it then)
/// does not count as a menu as long as Alt stays down; this needs admin rights, see <see cref="Elevation"/>. While the
/// settings window is open it stays visible, so it can be moved and zoomed then. Only Windows window/cursor/key state is
/// read, nothing from the game itself.
/// Ctrl+Shift+F10 switches it off/on, Ctrl+Shift+F11 opens/closes the settings, Ctrl+Shift+F12 cycles the range.
/// Nothing can draw over exclusive fullscreen.
/// </summary>
internal sealed class OverlayWindow : Form
{
    private const int WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExTopmost = 0x8;
    private const int WmNcHitTest = 0x84, WmMouseActivate = 0x21, WmExitSizeMove = 0x232, HtCaption = 2, MaNoActivate = 3;
    private const int CursorShowing = 0x1, VkMenu = 0x12;
    private const byte AcSrcAlpha = 0x1;
    private const int UlwAlpha = 0x2;
    private static readonly IntPtr HwndTopmost = new(-1);
    public static readonly int[] Ranges = [60, 120, 250];
    /// <summary>Display modes stored in <see cref="RadarSettings.OverlayMode"/>, with their i18n keys.</summary>
    public static readonly (string Value, string Key)[] Modes =
        [("radar", "overlay.modeRadar"), ("arrow", "overlay.modeArrow"), ("both", "overlay.modeBoth")];

    private readonly Func<RadarSnapshot> _snapshot;
    private readonly RadarSettings _settings;
    private readonly OverlayActions _actions;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    private SettingsForm? _settingsForm;
    private RadarSnapshot? _last;
    private int _ticks;
    private bool _moving;
    private int _dpiDepth;
    private int[] _gamePids = [];
    private long _gamePidsAt = -10_000;
    private long _cursorShownSince = -1;
    private bool _cursorByAlt;
    private NotifyIcon? _tray;
    private ToolStripMenuItem? _updateItem, _settingsItem, _toggleItem, _modeMenu, _zoomItem, _webItem, _capturesItem, _quitItem;
    private readonly List<(ToolStripMenuItem Item, string Mode)> _modeItems = [];
    private readonly MinimapHeading _heading;
    private readonly ArrowWindow _arrow;

    public OverlayWindow(Func<RadarSnapshot> snapshot, RadarSettings settings, OverlayActions actions)
    {
        _snapshot = snapshot;
        _settings = settings;
        _actions = actions;
        _heading = new MinimapHeading(settings);
        _arrow = new ArrowWindow(settings, _heading, () => _last);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Background;
        DoubleBuffered = true;
        // Same as the settings window: linear DPI, not Font. Font mode caches a pixel size and scales it
        // again on WM_GETDPISCALEDSIZE, which stretches this layered bitmap when the monitor DPI changes.
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Cube Radar Overlay";
        ApplySettings();
        var p = new Point(settings.OverlayX ?? 0, settings.OverlayY ?? 0);
        Location = settings.OverlayX is not null && OnSomeScreen(p) ? p : DefaultLocation();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        UiText.Changed += () => Ui(ApplyChrome);
    }

    /// <summary>Runs the overlay on its own UI thread; returns once the window exists.</summary>
    public static OverlayWindow Start(Func<RadarSnapshot> snapshot, RadarSettings settings, OverlayActions actions)
    {
        OverlayWindow? window = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            window = new OverlayWindow(snapshot, settings, actions);
            _ = window.Handle; // the handle must exist for calls from other threads, also while hidden
            ready.Set();
            Application.Run(new ApplicationContext());
        }) { IsBackground = true, Name = "overlay" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return window!;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate | WsExTopmost;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmNcHitTest:
                var screen = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                if (OverlayRenderer.GearRect(RenderScale).Contains(PointToClient(screen)))
                {
                    m.Result = 1; // HTCLIENT: the gear button gets the click
                    return;
                }
                m.Result = HtCaption; // everything else is a drag handle; Windows moves it
                _moving = (MouseButtons & MouseButtons.Left) != 0;
                return;
            case WmMouseActivate:
                m.Result = MaNoActivate; // clicking it never takes the focus from the game
                return;
            case WmExitSizeMove:
                _moving = false;
                _settings.OverlayX = Left;
                _settings.OverlayY = Top;
                _settings.Save();
                break;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && OverlayRenderer.GearRect(RenderScale).Contains(e.Location)) OpenSettings();
        base.OnMouseClick(e);
    }

    /// <summary>
    /// Real monitor DPI. <see cref="GetDpiForWindow"/> and <see cref="DeviceDpi"/> stay at the system DPI
    /// when awareness was set too late, so a move onto another scale kept the old bitmap.
    /// </summary>
    private int OverlayDpi
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

    /// <summary>Overlay scale: screen DPI times the user's size setting.</summary>
    private float RenderScale => ScaleFor(OverlayDpi);

    private float ScaleFor(int dpi) => dpi / 96f * Math.Clamp(_settings.OverlayScalePct, 50, 200) / 100f;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplySettings();
    }

    /// <summary>
    /// Physical size for the monitor DPI Windows is about to switch to. Returning false lets Font autoscaling
    /// invent a size from the cached pixel bounds; this window's bitmap has to match <see cref="OverlayRenderer.Measure"/>.
    /// </summary>
    protected override bool OnGetDpiScaledSize(int deviceDpiOld, int deviceDpiNew, ref Size desiredSize)
    {
        desiredSize = OverlayRenderer.Measure(ScaleFor(deviceDpiNew), _settings.ShowEnemies);
        return true;
    }

    /// <summary>
    /// Settings relayouts here because WinForms scales its controls. This window is a layered bitmap, so the
    /// suggested rect only resizes the HWND — the previous bitmap stays stretched until we paint at the new DPI.
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        if (_dpiDepth > 0) return; // Size/UpdateLayeredWindow can re-enter this message; don't loop
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

    /// <summary>Re-reads size and opacity (called after every change in the settings window).</summary>
    public void ApplySettings()
    {
        var size = OverlayRenderer.Measure(RenderScale, _settings.ShowEnemies);
        if (Size != size) Size = size;
        foreach (var (item, mode) in _modeItems) item.Checked = mode == _settings.OverlayMode;
        if (IsHandleCreated) UpdateVisibility(); // not from the constructor: the window is not placed yet
        if (_arrow.Visible) _arrow.ApplySettings();
        Redraw();
    }

    /// <summary>Radar, arrow or both (tray menu and settings).</summary>
    public void SetMode(string mode) => Ui(() =>
    {
        if (Array.FindIndex(Modes, m => m.Value == mode) < 0) return;
        _settings.OverlayMode = mode;
        _settings.Save();
        ApplySettings();
    });

    /// <summary>
    /// Paints the overlay into a bitmap and hands it to Windows with per-pixel alpha (UpdateLayeredWindow), so the radar
    /// and the enemy list below it each get their own opacity. A window updated this way does not use WM_PAINT.
    /// The bitmap is always the current DPI size, never the pixel size cached before a monitor change.
    /// </summary>
    private void Redraw()
    {
        if (!IsHandleCreated) return;
        var size = OverlayRenderer.Measure(RenderScale, _settings.ShowEnemies);
        if (size.Width <= 0 || size.Height <= 0) return;
        if (Size != size) Size = size;
        using var frame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
            OverlayRenderer.Render(g, size, _last, RenderScale, _settings.OverlayRangeM, _settings.ShowEnemies, _settings.MapNudges,
                _settings.ShowMapCubes, _settings.ShowFlightRings, _settings.ShowKibelisks);
        OverlayRenderer.ApplyOpacity(frame, RenderScale, Math.Clamp(_settings.OverlayOpacityPct, 40, 100),
            Math.Clamp(_settings.OverlayListOpacityPct ?? _settings.OverlayOpacityPct, 40, 100));
        var bitmap = PremultipliedBitmap(frame); // keeps per-pixel alpha; GetHbitmap would flatten the rounded corners
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var old = SelectObject(dc, bitmap);
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
            DeleteObject(bitmap);
        }
    }

    public void OpenSettings() => Ui(() =>
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings, _actions, ApplySettings, ResetPosition, ConfirmUpdate, _heading, _arrow.ResetPosition);
        _settingsForm.FormClosed += (_, _) => UpdateVisibility(); // the arrow stops being draggable at once
        _settingsForm.Show();
        UpdateVisibility();
    });

    /// <summary>Ctrl+Shift+F11: settings open (overlay visible to move and zoom it) or closed again.</summary>
    public void ToggleSettings() => Ui(() =>
    {
        if (SettingsOpen) _settingsForm!.Close();
        else OpenSettings();
    });

    private bool SettingsOpen => _settingsForm is { IsDisposed: false, Visible: true };

    private void ResetPosition()
    {
        Location = DefaultLocation();
        _settings.OverlayX = _settings.OverlayY = null;
        _settings.Save();
    }

    private Point DefaultLocation()
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        return new Point(screen.Right - Width - (int)(24 * RenderScale), screen.Top + (int)(300 * RenderScale));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var i = Math.Max(0, Array.IndexOf(Ranges, _settings.OverlayRangeM));
        i = Math.Clamp(i + (e.Delta > 0 ? -1 : 1), 0, Ranges.Length - 1); // wheel up = zoom in
        _settings.OverlayRangeM = Ranges[i];
        _settings.Save();
        Redraw();
    }

    /// <summary>Ctrl+Shift+F10: the user's on/off switch (auto-hiding works on top of it).</summary>
    public void ToggleVisible() => Ui(() =>
    {
        _settings.OverlayVisible = !_settings.OverlayVisible;
        _settings.Save();
        UpdateVisibility();
    });

    public void CycleRange() => Ui(() =>
    {
        var i = Array.IndexOf(Ranges, _settings.OverlayRangeM);
        _settings.OverlayRangeM = Ranges[(i + 1) % Ranges.Length];
        _settings.Save();
        Redraw();
    });

    public void Shutdown() => Ui(() =>
    {
        if (_tray is not null)
        {
            _tray.Visible = false; // otherwise a dead icon stays in the tray until the mouse passes over it
            _tray.Dispose();
        }
        _heading.Dispose();
        if (_arrow.IsHandleCreated) _arrow.Close();
        else _arrow.Dispose();
        Close();
        Application.ExitThread();
    });

    /// <summary>Tray icon with a menu: overlay on/off, range, web view, quit; and an update entry once one is found.</summary>
    public void ShowTray() => Ui(() =>
    {
        var menu = new ContextMenuStrip();
        _updateItem = new ToolStripMenuItem { Visible = false, Font = new Font(menu.Font, FontStyle.Bold) };
        _updateItem.Click += (_, _) => ConfirmUpdate();
        menu.Items.Add(_updateItem);
        _settingsItem = new ToolStripMenuItem();
        _settingsItem.Click += (_, _) => OpenSettings();
        _toggleItem = new ToolStripMenuItem();
        _toggleItem.Click += (_, _) => ToggleVisible();
        _modeMenu = new ToolStripMenuItem();
        foreach (var (value, _) in Modes)
        {
            var mode = value;
            var item = new ToolStripMenuItem { Checked = mode == _settings.OverlayMode };
            item.Click += (_, _) => SetMode(mode);
            _modeMenu.DropDownItems.Add(item);
            _modeItems.Add((item, mode));
        }
        _zoomItem = new ToolStripMenuItem();
        _zoomItem.Click += (_, _) => CycleRange();
        _webItem = new ToolStripMenuItem();
        _webItem.Click += (_, _) => _actions.OpenWeb();
        _capturesItem = new ToolStripMenuItem();
        _capturesItem.Click += (_, _) => _actions.OpenCaptures();
        _quitItem = new ToolStripMenuItem();
        _quitItem.Click += (_, _) => _actions.Quit();
        menu.Items.AddRange([_settingsItem, _toggleItem, _modeMenu, _zoomItem, _webItem, _capturesItem, new ToolStripSeparator(), _quitItem]);
        ApplyChrome();
        _tray = new NotifyIcon
        {
            Icon = AppIcon(),
            Text = "Cube Radar",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) => ConfirmUpdate();
        if (_actions.Updater is { } updater) updater.Found += OnUpdateFound;
    });

    /// <summary>Tray captions and the painted overlay follow the active language.</summary>
    private void ApplyChrome()
    {
        if (_settingsItem is null) return;
        _settingsItem.Text = UiText.T("tray.settings");
        _toggleItem!.Text = UiText.T("tray.toggle");
        _modeMenu!.Text = UiText.T("tray.display");
        foreach (var (item, mode) in _modeItems)
        {
            item.Text = UiText.T(Modes.First(m => m.Value == mode).Key);
            item.Checked = mode == _settings.OverlayMode;
        }
        _zoomItem!.Text = UiText.T("tray.zoom");
        _webItem!.Text = UiText.T("tray.web");
        _capturesItem!.Text = UiText.T("tray.captures");
        _quitItem!.Text = UiText.T("tray.quit");
        if (_actions.Updater?.Available is { } release && _updateItem is { Visible: true })
        {
            var version = release.Version.ToString(3);
            _updateItem.Text = release.Installable ? UiText.T("tray.updateInstall", version) : UiText.T("tray.updateDownload", version);
        }
        Invalidate();
    }

    /// <summary>A newer version exists: tray entry plus a one-time notification (it never takes the focus).</summary>
    private void OnUpdateFound(ReleaseInfo release) => Ui(() =>
    {
        if (_tray is null || _updateItem is null) return;
        var version = release.Version.ToString(3);
        _updateItem.Text = release.Installable ? UiText.T("tray.updateInstall", version) : UiText.T("tray.updateDownload", version);
        _updateItem.Visible = true;
        _tray.ShowBalloonTip(10_000, "Cube Radar", UiText.T("tray.balloon", version), ToolTipIcon.Info);
    });

    /// <summary>Asks before installing the available update; without a verifiable exe it offers the release page.</summary>
    public void ConfirmUpdate() => Ui(async () =>
    {
        if (_actions.Updater is not { Available: { } release, Installing: false } updater) return;
        var version = release.Version.ToString(3);
        // The settings window is topmost: an unowned message box could open hidden behind it.
        var owner = SettingsOpen ? _settingsForm : null;
        DialogResult Ask(string text, MessageBoxIcon icon) => MessageBox.Show(owner, text, UiText.T("update.title"), MessageBoxButtons.YesNo, icon);

        if (!release.Installable)
        {
            if (Ask(UiText.T("update.notInstallable", version), MessageBoxIcon.Information) == DialogResult.Yes)
                updater.OpenPage();
            return;
        }
        if (Ask(UiText.T("update.ask", version, Updater.Current.ToString(3)), MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _tray?.ShowBalloonTip(5_000, "Cube Radar", UiText.T("tray.downloading", version), ToolTipIcon.Info);
        if (await updater.InstallAsync() is { } error
            && Ask(UiText.T("update.failedAsk", error), MessageBoxIcon.Warning) == DialogResult.Yes)
            updater.OpenPage();
    });

    /// <summary>The program icon (assets/cuberadar.ico, compiled into the exe); a drawn glyph if that fails.</summary>
    public static Icon AppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
        }
        return TrayGlyph();
    }

    /// <summary>A small radar glyph: rings and a green cube.</summary>
    private static Icon TrayGlyph()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Palette.Background);
            g.FillEllipse(bg, 0, 0, 31, 31);
            using var ring = new Pen(Palette.Me, 2);
            g.DrawEllipse(ring, 2, 2, 27, 27);
            g.DrawEllipse(ring, 10, 10, 11, 11);
            using var cube = new SolidBrush(Palette.Visible);
            g.FillRectangle(cube, 19, 6, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private void Tick()
    {
        UpdateVisibility();
        if (!Visible && !_arrow.Visible) return;
        _last = _snapshot(); // the arrow draws from this one too
        // Some games push themselves to the top when they regain focus: re-assert every 2 s.
        if (++_ticks % 13 == 0)
        {
            if (Visible) SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            if (_arrow.Visible) SetWindowPos(_arrow.Handle, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        if (Visible) Redraw();
    }

    private void UpdateVisibility()
    {
        var wanted = _settings.OverlayVisible && (_moving || WantedOverGame());
        var arrow = wanted && _settings.ShowsArrow;
        _heading.Active = arrow || SettingsOpen; // the settings show what the minimap reading sees
        if (arrow && _last is null) _last = _snapshot();
        _arrow.SetShown(arrow);
        _arrow.Positioning = SettingsOpen && arrow;

        var show = wanted && _settings.ShowsRadar;
        if (show == Visible) return;
        if (show)
        {
            _last = _snapshot();
            Redraw(); // the content must be there before the window shows
            Show(); // without activation (ShowWithoutActivation)
        }
        else Hide();
    }

    /// <summary>Only while the game is in front, and (optionally) not while its cursor is out for a menu.</summary>
    private bool WantedOverGame()
    {
        if (SettingsOpen) return true; // the user is adjusting it: keep it visible to drag and zoom
        var now = Environment.TickCount64;
        if (now - _gamePidsAt > 5_000)
        {
            _gamePids = CubeRadar.Core.Capture.GameProcessLocator.FindGameProcessIds();
            _gamePidsAt = now;
        }
        if (_gamePids.Length == 0) return true; // no game running (replay / testing): just show it

        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        // Our own window is in front (tray menu, a dialog): leave the overlay as it is. Showing it now would close the
        // tray menu that is just opening (right-click on the tray icon brings our process to the front).
        if (pid == Environment.ProcessId) return Visible || _arrow.Visible;
        if (!_gamePids.Contains((int)pid)) return !_settings.OverlayOnlyWhenGameFocused;

        if (!_settings.OverlayHideInMenus) return true;
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        var cursorShown = GetCursorInfo(ref info) && (info.Flags & CursorShowing) != 0;
        if (!cursorShown)
        {
            _cursorShownSince = -1;
            return true;
        }
        // Without admin rights Windows reports no keys while the (elevated) game is in front: then Alt counts as a menu.
        var altDown = (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
        if (_cursorShownSince < 0)
        {
            _cursorShownSince = now;
            _cursorByAlt = altDown; // came out while Alt is held (AION 2 shows it then), not for a menu
        }
        if (_cursorByAlt)
        {
            if (altDown) return true;
            _cursorByAlt = false; // Alt released but the cursor stays out: a menu after all
            _cursorShownSince = now;
        }
        return now - _cursorShownSince < 300; // brief cursor flashes do not make it flicker
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private static bool OnSomeScreen(Point p) => Screen.AllScreens.Any(s => s.WorkingArea.Contains(p));

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Point Position;
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

    /// <summary>A top-down 32-bit DIB whose bits are the bitmap, alpha included. Shared with the arrow window.</summary>
    internal static IntPtr PremultipliedBitmap(Bitmap frame)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = frame.Width,
                Height = -frame.Height,
                Planes = 1,
                BitCount = 32,
            },
        };
        var screen = CreateCompatibleDC(IntPtr.Zero);
        var bits = IntPtr.Zero;
        var bitmap = CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
        DeleteDC(screen);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return frame.GetHbitmap(Color.FromArgb(0));
        var data = frame.LockBits(new Rectangle(Point.Empty, frame.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[frame.Width * 4];
            for (var y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                Marshal.Copy(row, 0, bits + y * row.Length, row.Length);
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Unused;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}

internal static class Palette
{
    public static readonly Color Background = Color.FromArgb(15, 18, 22);
    public static readonly Color Text = SettingsChrome.Fg;
    public static readonly Color Muted = SettingsChrome.Muted;
    public static readonly Color Line = SettingsChrome.Line;
    public static readonly Color Visible = Color.FromArgb(61, 220, 132);
    public static readonly Color OutOfView = Color.FromArgb(242, 179, 61);
    public static readonly Color Opening = Color.FromArgb(255, 225, 77);
    public static readonly Color Me = SettingsChrome.Accent;
    public static readonly Color Warn = SettingsChrome.Danger;
    public static readonly Color Ink = Color.FromArgb(8, 10, 12);
}

/// <summary>Draws the mini radar; also used to render a PNG for checking the layout.</summary>
internal static class OverlayRenderer
{
    private const float Side = 230, Header = 36, Margin = 16, EnemyFooter = 132;
    private const double LevelMetres = 3, SendRadiusM = 100;

    public static Size Measure(float scale, bool showEnemies = true) =>
        new((int)(Side * scale), (int)((Side + Header + (showEnemies ? EnemyFooter : 0)) * scale));

    /// <summary>
    /// "Segoe UI Semibold" is its own family. <see cref="FontStyle"/> has no semibold, and Bold on that family
    /// synthesizes a heavier face.
    /// </summary>
    private static Font Medium(float points) =>
        new("Segoe UI Semibold", points, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>The settings button in the header, in client pixels. Matches <see cref="GearBox"/>.</summary>
    public static Rectangle GearRect(float scale)
    {
        var box = GearBox();
        return Rectangle.Round(new RectangleF(box.X * scale, box.Y * scale, box.Width * scale, box.Height * scale));
    }

    private static RectangleF GearBox()
    {
        const float size = 22f;
        return new RectangleF(Side - 8f - size, (Header - size) / 2f, size, size);
    }

    public static void Render(Graphics g, Size size, RadarSnapshot? s, float scale, int rangeM, bool showEnemies = true,
        IReadOnlyDictionary<string, MapNudge>? nudges = null, bool showMapCubes = true, bool showFlightRings = true,
        bool showKibelisks = true)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        g.ScaleTransform(scale, scale);

        // Segoe UI Semibold: Regular was hairline over the game, Bold too heavy. FontStyle has no Semibold.
        using var small = Medium(9.5f);
        using var strong = Medium(9.5f);
        using var label = Medium(9f);
        using var tiny = Medium(9f);
        using var rankFont = Medium(9f);

        var totalH = Side + Header + (showEnemies ? EnemyFooter : 0f);
        var radius = SettingsChrome.Radius(small);
        var panel = new RectangleF(0.5f, 0.5f, Side - 1f, totalH - 1f);
        using (var plate = SettingsChrome.Round(panel, radius))
        using (var bg = new SolidBrush(Palette.Background))
            g.FillPath(bg, plate);
        var clipped = g.Save();
        using (var plate = SettingsChrome.Round(panel, radius))
            g.SetClip(plate);

        var me = Me(s);
        DrawHeader(g, s, me, rangeM, small, strong);

        var cx = Side / 2;
        var cy = Header + Side / 2;
        var r = Side / 2 - Margin;
        var px2world = r / (rangeM * 100f); // pixels per game unit
        DrawWash(g, cx, cy, r);

        void Rings()
        {
            StrokeRing(g, cx, cy, r, Color.FromArgb(210, 186, 198, 214), 1.15f, DashStyle.Solid);
            StrokeRing(g, cx, cy, r / 2f, Color.FromArgb(150, 186, 198, 214), 1f, DashStyle.Solid);
            using (var cross = new Pen(Color.FromArgb(72, Palette.Text), 1f))
            {
                g.DrawLine(cross, cx - r, cy, cx + r, cy);
                g.DrawLine(cross, cx, cy - r, cx, cy + r);
            }
            using (var tick = new Pen(Color.FromArgb(210, 214, 222, 230), 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                const float len = 7f;
                g.DrawLine(tick, cx, cy - r, cx, cy - r + len);
                g.DrawLine(tick, cx, cy + r, cx, cy + r - len);
                g.DrawLine(tick, cx - r, cy, cx - r + len, cy);
                g.DrawLine(tick, cx + r, cy, cx + r - len, cy);
            }
            Text(g, $"{rangeM / 2} m", tiny, Palette.Muted, cx + 6, cy - r / 2 - 12, halo: true);
            Text(g, UiText.T("overlay.north"), strong, Palette.Text, cx, cy - r + 3, center: true, halo: true);
            if (SendRadiusM < rangeM)
                StrokeRing(g, cx, cy, (float)(SendRadiusM * 100 * px2world), Color.FromArgb(140, 150, 190, 224), 1.05f, DashStyle.Dash);
        }

        if (s is null || me is null)
        {
            Rings();
            if (showEnemies) EnemyList(g, s, strong, tiny);
            var message = s is null ? UiText.T("overlay.waiting") : UiText.T("overlay.noPosition");
            var send = SendRadiusM < rangeM ? (float)(SendRadiusM * 100 * px2world) : 0f;
            var wide = g.MeasureString(message, strong).Width > Math.Max(24f, send * 1.5f);
            var messageY = send > 16f && wide ? cy + send + 8f : cy - 8f;
            Text(g, message, strong, Palette.Text, cx, messageY, center: true, halo: true);
            if (s is not null) Text(g, UiText.T("overlay.buffHint"), tiny, Palette.Muted, cx, messageY + 18f, center: true, halo: true);
            g.Restore(clipped);
            DrawFrame(g, panel, radius);
            return;
        }

        var o = s.Orientation;
        PointF ToScreen(double x, double y)
        {
            double dx = x - me.X, dy = y - me.Y;
            var n = dx * o.NorthX + dy * o.NorthY;
            var e = dx * o.EastX + dy * o.EastY;
            return new PointF(cx + (float)(e * px2world), cy - (float)(n * px2world));
        }

        // Zone picture, the whole square under the header. Static web-map marks (cube spots, flight rings) sit under the rings.
        var mapArea = new RectangleF(1, Header, Side - 2, Side);
        OverlayMap.Draw(g, s, me.X, me.Y, ToScreen, mapArea, nudges);
        OverlayMap.DrawMarks(g, s, ToScreen, new PointF(cx, cy), r, showMapCubes, showFlightRings, showKibelisks);
        Rings();

        // own trail (last ~2 minutes): shows where we came from, i.e. roughly where we are heading
        using (var trail = new Pen(Color.FromArgb(150, Palette.Me), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            PointF? prev = null;
            foreach (var p in s.Trail.TakeLast(40))
            {
                if (p.Length < 2) { prev = null; continue; }
                var q = ToScreen(p[0], p[1]);
                if (prev is { } a && Inside(a, cx, cy, r) && Inside(q, cx, cy, r)) g.DrawLine(trail, a, q);
                prev = q;
            }
        }

        // us: a small ringed dot, so it stays readable on the map without covering it
        var meColor = me.AgeMs <= s.FixFreshSeconds * 1000 ? Palette.Me : Color.FromArgb(150, Palette.Me);
        if (me.Ring > 0)
        {
            using var unc = new Pen(Color.FromArgb(150, Palette.Me), 1f) { DashStyle = DashStyle.Dot };
            var ur = Math.Max(4f, (float)(me.Ring * px2world));
            g.DrawEllipse(unc, cx - ur, cy - ur, 2 * ur, 2 * ur);
        }
        using (var halo = new Pen(Color.FromArgb(180, Palette.Ink), 2.2f))
            g.DrawEllipse(halo, cx - 6.2f, cy - 6.2f, 12.4f, 12.4f);
        using (var ring = new Pen(meColor, 1.45f))
            g.DrawEllipse(ring, cx - 5.4f, cy - 5.4f, 10.8f, 10.8f);
        using (var meBrush = new SolidBrush(meColor))
            g.FillEllipse(meBrush, cx - 2.15f, cy - 2.15f, 4.3f, 4.3f);

        // cubes, nearest drawn last (on top). Labels that would stack on each other are dropped; the marker stays.
        var live = s.Cubes.Where(c => c.Status is nameof(CubeStatus.Visible) or nameof(CubeStatus.Opening) or nameof(CubeStatus.OutOfView))
            .OrderByDescending(c => c.DistanceM ?? double.MaxValue).ToList();
        var cubeLabels = new List<RectangleF>();
        foreach (var c in live)
        {
            var color = c.Status switch
            {
                nameof(CubeStatus.Visible) => Palette.Visible,
                nameof(CubeStatus.Opening) => Palette.Opening,
                _ => Palette.OutOfView,
            };
            var dist = Math.Sqrt(Math.Pow(c.X - me.X, 2) + Math.Pow(c.Y - me.Y, 2)) / 100;
            (string Kind, string Text) height = c.DzUnits is { } dz
                ? Math.Abs(dz / 100) <= LevelMetres ? ("level", "≈") : dz > 0 ? ("up", $"▲{dz / 100:0}") : ("down", $"▼{-dz / 100:0}")
                : ("level", "");
            var text = $"{dist:0} m {height.Text}".Trim();
            var p = ToScreen(c.X, c.Y);
            if (Inside(p, cx, cy, r - 4))
            {
                Marker(g, p.X, p.Y, 6.5f, height.Kind, color, filled: c.Status != nameof(CubeStatus.OutOfView));
                var measured = g.MeasureString(text, label);
                var lx = p.X + 10;
                if (lx + measured.Width > Side - 8) lx = p.X - 10 - measured.Width;
                var labelBounds = new RectangleF(lx - 1, p.Y - 9, measured.Width + 2, measured.Height);
                if (cubeLabels.Any(other => other.IntersectsWith(labelBounds)))
                {
                    var alt = Math.Abs(lx - (p.X + 10)) < 1f ? p.X - 10 - measured.Width : p.X + 10;
                    var altBounds = new RectangleF(alt - 1, p.Y - 9, measured.Width + 2, measured.Height);
                    if (alt > 4 && alt + measured.Width < Side - 4 && !cubeLabels.Any(other => other.IntersectsWith(altBounds)))
                    {
                        lx = alt;
                        labelBounds = altBounds;
                    }
                    else labelBounds = RectangleF.Empty;
                }
                if (!labelBounds.IsEmpty)
                {
                    Text(g, text, label, color, lx, p.Y - 8, halo: true);
                    cubeLabels.Add(labelBounds);
                }
            }
            else
            {
                // beyond the range: an open chevron on the rim (not a filled triangle, which would read as ▲/▼ height)
                var a = Math.Atan2(p.Y - cy, p.X - cx);
                var ax = cx + (float)Math.Cos(a) * (r - 7);
                var ay = cy + (float)Math.Sin(a) * (r - 7);
                var state = g.Save();
                g.TranslateTransform(ax, ay);
                g.RotateTransform((float)(a * 180 / Math.PI));
                PointF[] armA = [new(-4, -6.5f), new(4.5f, 0), new(-4, 6.5f)];
                PointF[] armB = [new(-9, -6.5f), new(-0.5f, 0), new(-9, 6.5f)];
                using (var shadow = new Pen(Color.FromArgb(170, Palette.Ink), 3.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLines(shadow, armA);
                    g.DrawLines(shadow, armB);
                }
                using (var chevron = new Pen(color, 1.7f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLines(chevron, armA);
                    g.DrawLines(chevron, armB);
                }
                g.Restore(state);
                var tx = cx + (float)Math.Cos(a) * (r - 30);
                var ty = cy + (float)Math.Sin(a) * (r - 22);
                Text(g, text, label, color, tx, ty - 7, center: true, halo: true);
            }
        }
        if (showEnemies)
        {
            // Fresh positions only. Red diamonds cannot be mistaken for green square/height cube markers.
            const float markerRadius = 10;
            var placed = new List<RectangleF>();
            var markers = new List<(PointF Point, PlayerView Player)>();
            foreach (var p in s.Players.Where(p => p.IsEnemy == true && !p.PositionStale && p.X is not null && p.Y is not null)
                         .OrderBy(p => p.DistanceM ?? double.MaxValue).Take(12))
            {
                var q = ToScreen(p.X!.Value, p.Y!.Value);
                if (!Inside(q, cx, cy, r - markerRadius - 1))
                {
                    var angle = Math.Atan2(q.Y - cy, q.X - cx);
                    q = new PointF(cx + (float)Math.Cos(angle) * (r - markerRadius - 1), cy + (float)Math.Sin(angle) * (r - markerRadius - 1));
                }
                markers.Add((q, p));
                var name = p.Name.Length > 9 ? p.Name[..9] + "…" : p.Name;
                var text = $"{name} {(p.DistanceEstimated ? "~" : "")}{p.DistanceM:0}m";
                var width = g.MeasureString(text, tiny).Width;
                var x = Math.Clamp(q.X + markerRadius + 4, 3, Math.Max(3, Side - width - 3));
                var y = Math.Clamp(q.Y - 24, Header + 1, Header + Side - 14);
                var bounds = new RectangleF(x - 2, y - 1, width + 4, 14);
                if (!placed.Any(other => other.IntersectsWith(bounds)))
                {
                    Text(g, text, tiny, Palette.Warn, x, y, halo: true);
                    placed.Add(bounds); // full names/ranks remain in the list if point labels would overlap
                }
            }
            // Paint badges last, nearest on top, so adjacent name labels can never obscure a class glyph.
            using var red = new SolidBrush(Palette.Warn);
            using var ink = new SolidBrush(Palette.Ink);
            using var edge = new Pen(Color.FromArgb(210, 42, 16, 14), 1.35f) { LineJoin = LineJoin.Round };
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap };
            foreach (var marker in markers.AsEnumerable().Reverse())
            {
                var q = marker.Point;
                PointF[] diamond = [new(q.X, q.Y - markerRadius), new(q.X + markerRadius, q.Y),
                    new(q.X, q.Y + markerRadius), new(q.X - markerRadius, q.Y)];
                g.FillPolygon(red, diamond);
                g.DrawPolygon(edge, diamond);
                g.DrawString(marker.Player.RankSymbol, rankFont, ink,
                    new RectangleF(q.X - markerRadius, q.Y - markerRadius + 0.5f, 2 * markerRadius, 2 * markerRadius), centered);
            }
            EnemyList(g, s, strong, tiny);
        }
        g.Restore(clipped);
        DrawFrame(g, panel, radius);
    }

    private static void EnemyList(Graphics g, RadarSnapshot? s, Font title, Font detail)
    {
        var top = Side + Header;
        using var line = new Pen(Color.FromArgb(140, Palette.Line), 1f);
        g.DrawLine(line, 12, top + 0.5f, Side - 12, top + 0.5f);
        var enemies = s?.Players.Where(p => p.IsEnemy == true).ToList() ?? [];
        Text(g, UiText.T("overlay.enemies", enemies.Count), title, Palette.Text, 12, top + 8);
        if (s is null || s.OwnFaction == "Unknown")
        {
            Text(g, UiText.T("overlay.pickFaction"), detail, Palette.OutOfView, 12, top + 30);
            return;
        }
        if (enemies.Count == 0)
        {
            Text(g, UiText.T("overlay.noEnemies"), detail, Palette.Muted, 12, top + 30);
            return;
        }
        var state = g.Save();
        g.SetClip(new RectangleF(8, top + 28, Side - 16, EnemyFooter - 32));
        var y = top + 30f;
        foreach (var p in enemies.Take(3))
        {
            var faction = p.Faction switch
            {
                "Elyos" => UiText.T("faction.elyos"),
                "Asmodian" => UiText.T("faction.asmodian"),
                _ => UiText.T("faction.unknown"),
            };
            var distance = p.DistanceM is { } d ? $"{(p.PositionStale || p.DistanceEstimated ? "~" : "")}{d:0} m" : UiText.T("overlay.posUnknown");
            using var bar = new SolidBrush(p.PositionStale ? Palette.Muted : Palette.Warn);
            g.FillRectangle(bar, 12, y + 3, 2, 24);
            Text(g, $"{p.Name} · {faction} · {distance}", detail, Palette.Warn, 18, y);
            var rank = p.RankName is { } name && !name.StartsWith("PvP-Rang", StringComparison.Ordinal) ? name
                : p.RankCode is { } code ? UiText.T("rank.code", code) : UiText.T("rank.unknown");
            if (p.RankCode is not null && !p.RankVerified) rank += " *";
            if (p.PositionStale && p.PositionAgeMs is { } age) rank += UiText.T("overlay.posOld", age / 1000);
            Text(g, rank, detail, Palette.Muted, 18, y + 16);
            y += 34;
        }
        g.Restore(state);
    }

    /// <summary>
    /// Scales the alpha of the radar (header and map) and of the enemy list below it, then premultiplies.
    /// UpdateLayeredWindow expects premultiplied pixels, including the anti-aliased rounded corners.
    /// </summary>
    public static void ApplyOpacity(Bitmap frame, float scale, int radarPct, int listPct)
    {
        var listTop = (int)Math.Round((Side + Header) * scale);
        var data = frame.LockBits(new Rectangle(Point.Empty, frame.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[frame.Width * 4];
            for (var y = 0; y < frame.Height; y++)
            {
                var pct = y < listTop ? radarPct : listPct;
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

    internal sealed record MeView(double X, double Y, long AgeMs, double Ring);

    /// <summary>Our position: the later of real fix and ring estimate (as in the web UI).</summary>
    internal static MeView? Me(RadarSnapshot? s)
    {
        if (s is null) return null;
        if (s.Ring is { } r && (s.Self is null || r.TimeMs > s.Self.TimeMs)) return new MeView(r.X, r.Y, r.AgeMs, r.Uncertainty);
        return s.Self is { } f ? new MeView(f.X, f.Y, f.AgeMs, 0) : null;
    }

    private static bool Inside(PointF p, float cx, float cy, float r) => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) <= r * r;

    /// <summary>Square for "same height", triangle up / down for above / below us.</summary>
    private static void Marker(Graphics g, float x, float y, float s, string kind, Color color, bool filled)
    {
        PointF[] pts = kind switch
        {
            "up" => [new(x, y - s), new(x + s, y + s * 0.8f), new(x - s, y + s * 0.8f)],
            "down" => [new(x, y + s), new(x + s, y - s * 0.8f), new(x - s, y - s * 0.8f)],
            _ => [new(x - s * 0.8f, y - s * 0.8f), new(x + s * 0.8f, y - s * 0.8f), new(x + s * 0.8f, y + s * 0.8f), new(x - s * 0.8f, y + s * 0.8f)],
        };
        if (filled)
        {
            using var edge = new Pen(Color.FromArgb(190, Palette.Ink), 2.4f) { LineJoin = LineJoin.Round };
            g.DrawPolygon(edge, pts);
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, pts);
        }
        else
        {
            using var pen = new Pen(color, 1.7f) { LineJoin = LineJoin.Round };
            g.DrawPolygon(pen, pts);
        }
    }

    private static void Text(Graphics g, string text, Font font, Color color, float x, float y, bool right = false, bool center = false, bool halo = false)
    {
        var w = g.MeasureString(text, font).Width;
        if (right) x -= w;
        else if (center) x -= w / 2;
        if (halo)
        {
            using var shadow = new SolidBrush(Color.FromArgb(220, Palette.Ink));
            g.DrawString(text, font, shadow, x - 1f, y);
            g.DrawString(text, font, shadow, x + 1f, y);
            g.DrawString(text, font, shadow, x, y - 1f);
            g.DrawString(text, font, shadow, x, y + 1f);
        }
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, x, y);
    }

    private static void DrawHeader(Graphics g, RadarSnapshot? s, MeView? me, int rangeM, Font font, Font strong)
    {
        var rangeText = $"{rangeM} m";
        var rangeSize = g.MeasureString(rangeText, strong);
        Text(g, rangeText, strong, Palette.Text, 10, (Header - rangeSize.Height) / 2f);

        var gear = GearBox();
        var saved = g.Save();
        g.TranslateTransform(gear.X, gear.Y);
        SettingsChrome.PaintField(g, new Rectangle(0, 0, (int)gear.Width, (int)gear.Height), strong, hover: false, pressed: false, focused: false, enabled: true);
        g.Restore(saved);
        DrawSettings(g, gear, Palette.Text);

        if (s is null) return;
        var (posText, posColor) = me switch
        {
            { Ring: > 0 } m => (UiText.T("overlay.posRing", Math.Round(m.Ring / 100)), Palette.OutOfView),
            { } m => (UiText.T("overlay.posAge", m.AgeMs / 1000), m.AgeMs <= s.FixFreshSeconds * 1000 ? Palette.Visible : m.AgeMs <= 30_000 ? Palette.OutOfView : Palette.Warn),
            null => (UiText.T("overlay.posUnknown"), Palette.Warn),
        };
        var textSize = g.MeasureString(posText, font);
        var left = 10 + rangeSize.Width + 8;
        var right = gear.X - 6;
        var x = left + Math.Max(0, (right - left - textSize.Width - 12) / 2f) + 12;
        using var dot = new SolidBrush(posColor);
        g.FillEllipse(dot, x - 12, (Header - 6f) / 2f, 6, 6);
        Text(g, posText, font, Palette.Muted, x, (Header - textSize.Height) / 2f);
    }

    private static void DrawWash(Graphics g, float cx, float cy, float radius)
    {
        var rect = new RectangleF(cx - radius, cy - radius, radius * 2f, radius * 2f);
        using var path = new GraphicsPath();
        path.AddEllipse(rect);
        using var brush = new PathGradientBrush(path)
        {
            CenterPoint = new PointF(cx, cy),
            CenterColor = Color.FromArgb(24, 32, 40),
            SurroundColors = [Color.FromArgb(12, 15, 19)],
        };
        g.FillEllipse(brush, rect);
    }

    private static void DrawFrame(Graphics g, RectangleF panel, float radius)
    {
        using var path = SettingsChrome.Round(panel, radius);
        using var pen = new Pen(Palette.Line, 1f);
        g.DrawPath(pen, path);
    }

    private static void StrokeRing(Graphics g, float cx, float cy, float radius, Color color, float width, DashStyle dash)
    {
        if (radius < 2f) return;
        var d = radius * 2f;
        using var under = new Pen(Color.FromArgb(110, Palette.Ink), width + 1.15f) { DashStyle = dash };
        g.DrawEllipse(under, cx - radius, cy - radius, d, d);
        using var pen = new Pen(color, width) { DashStyle = dash };
        g.DrawEllipse(pen, cx - radius, cy - radius, d, d);
    }

    /// <summary>Three slider rows. A gear made of a circle and ticks read as a second crosshair on the radar.</summary>
    private static void DrawSettings(Graphics g, RectangleF box, Color color)
    {
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        using var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var knob = new SolidBrush(color);
        void Row(float y, float shift)
        {
            var kx = cx + shift;
            var ky = cy + y;
            g.DrawLine(pen, cx - 5.1f, ky, kx - 2.15f, ky);
            g.DrawLine(pen, kx + 2.15f, ky, cx + 5.1f, ky);
            g.FillEllipse(knob, kx - 1.55f, ky - 1.55f, 3.1f, 3.1f);
        }
        Row(-3.5f, 1.7f);
        Row(0f, -1.8f);
        Row(3.5f, 2.3f);
    }

    /// <summary>One frame as PNG (for checking the layout without a game).</summary>
    public static void SavePng(string path, RadarSnapshot? s, int rangeM = 120, float scale = 1.5f)
    {
        var size = Measure(scale);
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp)) Render(g, size, s, scale, rangeM);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
