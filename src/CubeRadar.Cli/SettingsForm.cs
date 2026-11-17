using System.Drawing;
using System.Windows.Forms;

namespace CubeRadar.Cli;

/// <summary>What the overlay and its settings window can ask the radar to do.</summary>
internal sealed record OverlayActions(
    Action OpenWeb,
    Action Quit,
    Action TestSound,
    Func<string> GetNorth,
    Action<string> SetNorth,
    Action OpenCaptures,
    Updater? Updater = null,
    Func<string>? GetFaction = null,
    Action<string>? SetFaction = null,
    Action? TestEnemySound = null);

/// <summary>
/// Settings opened from the overlay's gear button (or the tray). Every change applies at once and is saved; there is
/// no OK / Cancel. Runs on the overlay's UI thread. Labels follow <see cref="UiText"/>.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly Color Bg = SettingsChrome.Bg, Field = SettingsChrome.Field, Fg = SettingsChrome.Fg,
        Muted = SettingsChrome.Muted, Accent = SettingsChrome.Accent;

    private static readonly (string Value, string Key)[] Norths =
        [("-y", "settings.northMinusY"), ("+y", "+Y"), ("+x", "+X"), ("-x", "−X")];

    private readonly List<(Control Control, string Key)> _labels = [];
    private bool _applying;
    private bool _preview;

    /// <summary>The PNG preview must not steal the focus from the game.</summary>
    protected override bool ShowWithoutActivation => _preview;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE: dark title bar
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public SettingsForm(RadarSettings settings, OverlayActions actions, Action applied, Action resetPosition, Action confirmUpdate,
        MinimapHeading heading, Action resetArrowPosition)
    {
        Icon = OverlayWindow.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        // The dialog used to grow with its contents, so on a 1080p screen the bottom sat off-screen with no way to scroll.
        AutoSize = false;
        Padding = Padding.Empty;

        void Save()
        {
            settings.Save();
            applied();
        }

        var grid = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.None, BackColor = Bg, Padding = new Padding(16, 12, 16, 12),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Section(string key)
        {
            var l = new Label { AutoSize = true, ForeColor = Accent, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) };
            _labels.Add((l, key));
            grid.Controls.Add(l);
            grid.SetColumnSpan(l, 2);
        }

        void Row(string key, Control control)
        {
            var label = new Label { AutoSize = true, ForeColor = Fg, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };
            _labels.Add((label, key));
            grid.Controls.Add(label);
            control.Margin = new Padding(0, 3, 0, 3);
            if (control is SettingsDropDown drop) drop.BindLabel(label);
            grid.Controls.Add(control);
        }

        void Full(Control control)
        {
            grid.Controls.Add(control);
            grid.SetColumnSpan(control, 2);
        }

        Label Hint(string key, int left = 0)
        {
            var label = new Label { AutoSize = true, ForeColor = Muted, MaximumSize = new Size(480, 0), Margin = new Padding(left, 0, 0, 4) };
            _labels.Add((label, key));
            return label;
        }

        ToggleSwitch Check(string key, bool value)
        {
            var box = new ToggleSwitch { Checked = value, ForeColor = Fg, Margin = new Padding(0, 3, 0, 3) };
            _labels.Add((box, key));
            return box;
        }

        /// <summary>Play icon. The translated text is the accessible name and the tooltip, not a caption.</summary>
        PlayButton PlayBtn(string key)
        {
            var b = new PlayButton { Margin = new Padding(0, 3, 0, 3) };
            _labels.Add((b, key));
            return b;
        }

        CommandButton Btn(string key, CommandTone tone = CommandTone.Quiet)
        {
            var b = new CommandButton { Tone = tone };
            _labels.Add((b, key));
            return b;
        }

        SettingsDropDown Combo() => new() { Width = 240, ForeColor = Fg };

        void Refill(SettingsDropDown box, IEnumerable<string> texts, int selected) =>
            box.Replace(texts, selected);

        // ---- language
        var lang = Combo();
        lang.SelectedIndexChanged += (_, _) =>
        {
            if (_applying) return;
            settings.Language = lang.SelectedIndex == 1 ? UiText.English : UiText.Russian;
            Save();
            UiText.Set(settings.Language);
        };
        Row("settings.language", lang);

        // ---- sound
        Section("settings.soundSection");
        var sound = Check("settings.soundEnabled", settings.SoundEnabled);
        sound.CheckedChanged += (_, _) => { settings.SoundEnabled = sound.Checked; Save(); };
        var test = PlayBtn("settings.test");
        test.Click += (_, _) => actions.TestSound();
        Full(Flow(sound, test));

        // Older files have no enemy-sound flag: keep whatever the cube sound was set to until the user chooses.
        settings.EnemySoundEnabled ??= settings.SoundEnabled;
        var enemySound = Check("settings.enemySound", settings.EnemySoundEnabled.Value);
        enemySound.CheckedChanged += (_, _) => { settings.EnemySoundEnabled = enemySound.Checked; Save(); };
        var enemyPreview = PlayBtn("settings.previewSound");
        enemyPreview.Click += (_, _) => actions.TestEnemySound?.Invoke();
        Full(Flow(enemySound, enemyPreview));

        var volume = new TrackBar { Minimum = 5, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Width = 200,
            Value = Math.Clamp(settings.SoundVolumePct, 5, 100), BackColor = Bg };
        var volumeText = new Label { AutoSize = true, ForeColor = Muted, Text = $"{volume.Value} %", Margin = new Padding(6, 8, 0, 0) };
        var preview = new System.Windows.Forms.Timer { Interval = 200 };
        preview.Tick += (_, _) => { preview.Stop(); actions.TestSound(); };
        Disposed += (_, _) => preview.Dispose();
        volume.ValueChanged += (_, _) =>
        {
            settings.SoundVolumePct = volume.Value;
            volumeText.Text = $"{volume.Value} %";
            Save();
            preview.Stop();
            preview.Start();
        };
        Row("settings.volume", Flow(volume, volumeText));

        var file = new TextBox { Width = 230, ReadOnly = true, BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Text = settings.SoundFile ?? "" };
        var browse = Btn("settings.browse");
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = UiText.T("settings.wavFilter"), Title = UiText.T("settings.wavTitle") };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            settings.SoundFile = file.Text = dlg.FileName;
            Save();
        };
        var clear = Btn("settings.defaultBeep");
        clear.Click += (_, _) => { settings.SoundFile = null; file.Text = ""; Save(); };
        Row("settings.wavRow", Flow(file, browse, clear));

        Section("settings.enemiesSection");
        var enemies = Check("settings.showEnemies", settings.ShowEnemies);
        enemies.CheckedChanged += (_, _) => { settings.ShowEnemies = enemies.Checked; Save(); };
        Full(enemies);
        SettingsDropDown? faction = null;
        string[] factionValues = ["Auto", "Elyos", "Asmodian"];
        var factionIndex = 0;
        if (actions.SetFaction is { } setFaction)
        {
            faction = Combo();
            factionIndex = Math.Max(0, Array.IndexOf(factionValues, actions.GetFaction?.Invoke() ?? settings.OwnFaction));
            faction.SelectedIndexChanged += (_, _) =>
            {
                if (!_applying) setFaction(factionValues[faction.SelectedIndex]);
            };
            Row("settings.faction", faction);
        }
        Full(Hint("settings.enemyHint"));

        // ---- overlay look
        Section("settings.overlaySection");
        var mode = Combo();
        var modeIndex = Math.Max(0, Array.FindIndex(OverlayWindow.Modes, m => m.Value == settings.OverlayMode));
        mode.SelectedIndexChanged += (_, _) =>
        {
            if (_applying || mode.SelectedIndex < 0) return;
            settings.OverlayMode = OverlayWindow.Modes[mode.SelectedIndex].Value;
            Save();
        };
        Row("settings.display", mode);
        var mapCubes = Check("settings.showMapCubes", settings.ShowMapCubes);
        mapCubes.CheckedChanged += (_, _) => { settings.ShowMapCubes = mapCubes.Checked; Save(); };
        Full(mapCubes);
        var flightRings = Check("settings.showFlightRings", settings.ShowFlightRings);
        flightRings.CheckedChanged += (_, _) => { settings.ShowFlightRings = flightRings.Checked; Save(); };
        Full(flightRings);
        var kibelisks = Check("settings.showKibelisks", settings.ShowKibelisks);
        kibelisks.CheckedChanged += (_, _) => { settings.ShowKibelisks = kibelisks.Checked; Save(); };
        Full(kibelisks);
        var range = Combo();
        range.Replace(OverlayWindow.Ranges.Select(r => $"{r} m"), Math.Max(0, Array.IndexOf(OverlayWindow.Ranges, settings.OverlayRangeM)));
        range.SelectedIndexChanged += (_, _) =>
        {
            if (_applying) return;
            settings.OverlayRangeM = OverlayWindow.Ranges[range.SelectedIndex];
            Save();
        };
        Row("settings.zoom", range);

        int[] scales = [75, 100, 125, 150];
        var scale = Combo();
        scale.Replace(scales.Select(s => $"{s} %"), Math.Max(0, Array.IndexOf(scales, settings.OverlayScalePct)));
        scale.SelectedIndexChanged += (_, _) =>
        {
            if (_applying) return;
            settings.OverlayScalePct = scales[scale.SelectedIndex];
            Save();
        };
        Row("settings.size", scale);

        TrackBar Opacity(string key, int value, Action<int> set)
        {
            var bar = new TrackBar { Minimum = 40, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Width = 200,
                Value = Math.Clamp(value, 40, 100), BackColor = Bg };
            var text = new Label { AutoSize = true, ForeColor = Muted, Text = $"{bar.Value} %", Margin = new Padding(6, 8, 0, 0) };
            bar.ValueChanged += (_, _) => { set(bar.Value); text.Text = $"{bar.Value} %"; Save(); };
            Row(key, Flow(bar, text));
            return bar;
        }
        Opacity("settings.opacityRadar", settings.OverlayOpacityPct, v => settings.OverlayOpacityPct = v);
        var listOpacity = Opacity("settings.opacityList", settings.OverlayListOpacityPct ?? settings.OverlayOpacityPct,
            v => settings.OverlayListOpacityPct = v);
        listOpacity.Enabled = enemies.Checked;
        enemies.CheckedChanged += (_, _) => listOpacity.Enabled = enemies.Checked;

        var north = Combo();
        var northIndex = Math.Max(0, Array.FindIndex(Norths, n => n.Value == actions.GetNorth()));
        north.SelectedIndexChanged += (_, _) =>
        {
            if (!_applying) actions.SetNorth(Norths[north.SelectedIndex].Value);
        };
        Row("settings.north", north);
        Full(Hint("settings.northHint"));

        // ---- arrow over the character
        Section("settings.arrowSection");
        int[] arrowScales = [75, 100, 125, 150, 200];
        var arrowScale = Combo();
        var arrowScaleIndex = Array.IndexOf(arrowScales, settings.ArrowScalePct);
        if (arrowScaleIndex < 0) arrowScaleIndex = Array.IndexOf(arrowScales, 100);
        arrowScale.SelectedIndexChanged += (_, _) =>
        {
            if (_applying || arrowScale.SelectedIndex < 0) return;
            settings.ArrowScalePct = arrowScales[arrowScale.SelectedIndex];
            Save();
        };
        Row("settings.size", arrowScale);
        Opacity("settings.arrowOpacity", settings.ArrowOpacityPct, v => settings.ArrowOpacityPct = v);
        var arrowStatus = new Label
        {
            AutoSize = true, ForeColor = Muted, MaximumSize = new Size(480, 0), Margin = new Padding(0, 4, 0, 2),
            Text = heading.Status,
        };
        var arrowPoll = new System.Windows.Forms.Timer { Interval = 300 };
        var calibrating = false;
        arrowPoll.Tick += (_, _) =>
        {
            if (!calibrating && !IsDisposed) arrowStatus.Text = heading.Status;
        };
        arrowPoll.Start();
        Disposed += (_, _) => arrowPoll.Dispose();
        var findMinimap = Btn("settings.findMinimap");
        findMinimap.Click += async (_, _) =>
        {
            calibrating = true;
            findMinimap.Enabled = false;
            arrowStatus.Text = UiText.T("heading.search");
            try
            {
                var text = await heading.CalibrateAsync();
                if (!IsDisposed) arrowStatus.Text = text;
            }
            finally
            {
                if (!IsDisposed) findMinimap.Enabled = true;
                await Task.Delay(4_000); // leave the result readable before the live status takes over again
                calibrating = false;
            }
        };
        var standardMinimap = Btn("settings.minimapDefault");
        standardMinimap.Click += (_, _) =>
        {
            heading.ResetCalibration();
            arrowStatus.Text = heading.Status;
        };
        var resetArrow = Btn("settings.resetArrow");
        resetArrow.Click += (_, _) => resetArrowPosition();
        var arrowTools = Flow(findMinimap, standardMinimap, resetArrow);
        arrowTools.WrapContents = true;
        arrowTools.MaximumSize = new Size(480, 0);
        Full(arrowStatus);
        Full(arrowTools);
        Full(Hint("settings.arrowHint"));

        // ---- visibility
        Section("settings.visibleSection");
        var focus = Check("settings.onlyFocused", settings.OverlayOnlyWhenGameFocused);
        focus.CheckedChanged += (_, _) => { settings.OverlayOnlyWhenGameFocused = focus.Checked; Save(); };
        Full(focus);
        var menus = Check("settings.hideMenus", settings.OverlayHideInMenus);
        menus.CheckedChanged += (_, _) => { settings.OverlayHideInMenus = menus.Checked; Save(); };
        Full(menus);
        var admin = Check("settings.admin", settings.OverlayAdminRights);
        admin.CheckedChanged += (_, _) => { settings.OverlayAdminRights = admin.Checked; Save(); };
        admin.Enabled = menus.Checked;
        menus.CheckedChanged += (_, _) => admin.Enabled = menus.Checked;
        admin.Margin = new Padding(20, 3, 0, 0);
        Full(admin);
        var adminHint = new Label { AutoSize = true, ForeColor = Muted, MaximumSize = new Size(480, 0), Margin = new Padding(20, 0, 0, 4) };
        Full(adminHint);

        // ---- updates (single exe only)
        Label? updateState = null;
        Button? check = null, install = null;
        if (actions.Updater is { } updater)
        {
            Section("settings.updatesSection");
            var auto = Check("settings.autoUpdate", settings.UpdateCheckEnabled);
            auto.CheckedChanged += (_, _) => updater.SetEnabled(auto.Checked);
            Full(auto);
            updateState = new Label { AutoSize = true, ForeColor = Muted, MaximumSize = new Size(480, 0), Margin = new Padding(0, 2, 0, 2) };
            check = Btn("settings.checkNow");
            check.Click += (_, _) => _ = updater.CheckAsync();
            install = Btn("settings.updateNow", CommandTone.Primary);
            install.Click += (_, _) => confirmUpdate();
            var page = Btn("settings.github");
            page.Click += (_, _) => updater.OpenPage();
            void OnChanged()
            {
                try
                {
                    if (IsHandleCreated) BeginInvoke(ApplyLanguage);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                }
            }
            updater.Changed += OnChanged;
            Disposed += (_, _) => updater.Changed -= OnChanged;
            Full(updateState);
            Full(Flow(check, install, page));
        }

        // ---- actions
        Section("settings.otherSection");
        var reset = Btn("settings.resetPosition");
        reset.Click += (_, _) => resetPosition();
        var web = Btn("settings.openWeb");
        web.Click += (_, _) => actions.OpenWeb();
        var captures = Btn("settings.openCaptures");
        captures.Click += (_, _) => actions.OpenCaptures();
        Full(Flow(reset, web, captures));
        Full(Hint("settings.hotkeys"));

        var quit = Btn("settings.quit", CommandTone.Danger);
        quit.Click += (_, _) => { Close(); actions.Quit(); };
        var close = Btn("settings.close", CommandTone.Primary);
        close.Click += (_, _) => Close();
        var bottom = Flow(quit, close);
        bottom.Margin = new Padding(0, 14, 0, 0);
        Full(bottom);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Bg };
        grid.Location = Point.Empty;
        scroll.Controls.Add(grid);
        Controls.Add(scroll);
        Load += (_, _) => FitToScreen(scroll, grid);
        HookWheel(scroll, this);

        void ApplyLanguage()
        {
            _applying = true;
            Text = UiText.T("settings.title");
            foreach (var (control, key) in _labels) control.Text = UiText.T(key);
            adminHint.Text = UiText.T("settings.adminHint") + (Elevation.IsElevated ? "" : "\n" + UiText.T("settings.adminOff"));
            Refill(lang, ["Русский", "English"], UiText.Language == UiText.English ? 1 : 0);
            if (faction is not null)
                Refill(faction, [UiText.T("settings.factionAuto"), UiText.T("settings.factionElyos"), UiText.T("settings.factionAsmodian")],
                    faction.SelectedIndex < 0 ? factionIndex : faction.SelectedIndex);
            Refill(north, Norths.Select(n => n.Key.StartsWith("settings.", StringComparison.Ordinal) ? UiText.T(n.Key) : n.Key),
                north.SelectedIndex < 0 ? northIndex : north.SelectedIndex);
            Refill(mode, OverlayWindow.Modes.Select(m => UiText.T(m.Key)), mode.SelectedIndex < 0 ? modeIndex : mode.SelectedIndex);
            Refill(arrowScale, arrowScales.Select(s => $"{s} %"), arrowScale.SelectedIndex < 0 ? arrowScaleIndex : arrowScale.SelectedIndex);
            if (!calibrating) arrowStatus.Text = heading.Status;
            if (updateState is not null && actions.Updater is { } current)
            {
                updateState.Text = UiText.T("settings.installed", Updater.Current.ToString(3), current.State);
                if (check is not null) check.Enabled = !current.Installing;
                if (install is not null) install.Enabled = current.Available is not null && !current.Installing;
            }
            _applying = false;
        }

        void OnUiLanguage()
        {
            try
            {
                if (IsHandleCreated) BeginInvoke(ApplyLanguage);
                else ApplyLanguage();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        }
        UiText.Changed += OnUiLanguage;
        Disposed += (_, _) => UiText.Changed -= OnUiLanguage;
        ApplyLanguage();
    }

    /// <summary>
    /// Keeps the dialog inside the working area. What does not fit scrolls; the window itself stays put.
    /// </summary>
    private static void FitToScreen(Panel scroll, TableLayoutPanel grid)
    {
        var form = scroll.FindForm();
        if (form is null) return;
        grid.PerformLayout();
        var preferred = grid.GetPreferredSize(Size.Empty);
        if (grid.Height > preferred.Height) preferred = new Size(Math.Max(preferred.Width, grid.Width), grid.Height);
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        var chromeH = form.Height - form.ClientSize.Height;
        var chromeW = form.Width - form.ClientSize.Width;
        if (chromeH < 8) chromeH = SystemInformation.CaptionHeight + 8;
        if (chromeW < 8) chromeW = 16;
        var maxH = Math.Max(320, work.Height - chromeH - 8);
        var maxW = Math.Max(480, work.Width - chromeW - 8);
        var needsBar = preferred.Height > maxH;
        var bar = needsBar ? SystemInformation.VerticalScrollBarWidth : 0;
        form.ClientSize = new Size(Math.Min(preferred.Width + bar, maxW), Math.Min(preferred.Height, maxH));
        // A docked child is excluded from AutoScroll, so the grid stays at its own height and the panel scrolls it.
        var overflow = grid.Right - scroll.ClientSize.Width;
        if (overflow > 0) form.ClientSize = new Size(Math.Min(form.ClientSize.Width + overflow, maxW), form.ClientSize.Height);
        if (form.StartPosition == FormStartPosition.CenterScreen)
            form.Location = new Point(
                work.Left + Math.Max(0, (work.Width - form.Width) / 2),
                work.Top + Math.Max(0, (work.Height - form.Height) / 2));
    }

    /// <summary>The wheel scrolls the dialog. A slider keeps the wheel so volume and opacity still move.</summary>
    private static void HookWheel(Panel scroll, Control root)
    {
        if (root is not TrackBar)
            root.MouseWheel += (_, e) =>
            {
                if (!scroll.VerticalScroll.Visible) return;
                scroll.AutoScrollPosition = new Point(0, Math.Max(0, -scroll.AutoScrollPosition.Y - e.Delta));
                if (e is HandledMouseEventArgs handled) handled.Handled = true;
            };
        foreach (Control child in root.Controls) HookWheel(scroll, child);
    }

    /// <summary>Renders the window to a PNG without showing it (for checking the layout).</summary>
    public static void SavePng(string path, RadarSettings settings, OverlayActions actions)
    {
        var t = new Thread(() =>
        {
            using var heading = new MinimapHeading(settings);
            using var form = new SettingsForm(settings, actions, () => { }, () => { }, () => { }, heading, () => { })
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-30_000, -30_000),
                ShowInTaskbar = false,
                _preview = true,
            };
            form.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Bg, Margin = new Padding(0) };
        foreach (var c in controls)
        {
            c.Margin = new Padding(0, 3, 8, 3);
            f.Controls.Add(c);
        }
        return f;
    }
}
