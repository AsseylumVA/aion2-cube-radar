using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CubeRadar.Cli;

/// <summary>
/// Shared colours and chrome for the settings window: toggles, the play button and dropdowns use one height, radius
/// and palette so the panel reads as a single control set.
/// </summary>
internal static class SettingsChrome
{
    public static readonly Color Bg = Color.FromArgb(22, 27, 33);
    public static readonly Color Field = Color.FromArgb(32, 39, 47);
    public static readonly Color FieldHover = Color.FromArgb(40, 49, 60);
    public static readonly Color FieldPressed = Color.FromArgb(26, 32, 40);
    public static readonly Color Fg = Color.FromArgb(230, 235, 240);
    public static readonly Color Muted = Color.FromArgb(139, 150, 163);
    public static readonly Color Accent = Color.FromArgb(77, 163, 255);
    public static readonly Color AccentHover = Color.FromArgb(112, 184, 255);
    public static readonly Color AccentPressed = Color.FromArgb(54, 132, 214);
    /// <summary>Dark text on <see cref="Accent"/>, the same pairing as the web view's selected control.</summary>
    public static readonly Color AccentText = Color.FromArgb(7, 18, 32);
    public static readonly Color FocusOnAccent = Color.FromArgb(232, 242, 255);
    public static readonly Color Danger = Color.FromArgb(255, 107, 91);
    public static readonly Color DangerHover = Color.FromArgb(48, 34, 36);
    public static readonly Color DangerPressed = Color.FromArgb(38, 26, 28);
    public static readonly Color Line = Color.FromArgb(60, 70, 82);
    public static readonly Color LineHover = Color.FromArgb(120, 134, 150);
    public static readonly Color TrackOff = Color.FromArgb(54, 64, 76);
    public static readonly Color Thumb = Color.FromArgb(244, 247, 250);
    public static readonly Color ListHover = Color.FromArgb(46, 56, 68);
    public static readonly Color ListOn = Color.FromArgb(30, 70, 112);

    /// <summary>Square play button and closed dropdown, matched to the toggle row.</summary>
    public static int ControlHeight(Font font) => Math.Max(28, font.Height + 12);

    public static int Radius(Font font) => Math.Max(5, ControlHeight(font) / 5);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(Math.Max(0, radius) * 2, Math.Min(r.Width, r.Height));
        if (d < 1)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Rounded field used by the play button and the closed dropdown.</summary>
    public static void PaintField(Graphics g, Rectangle bounds, Font font, bool hover, bool pressed, bool focused, bool enabled)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Math.Max(1, bounds.Width - 1f), Math.Max(1, bounds.Height - 1f));
        var fill = !enabled ? Bg : pressed ? FieldPressed : hover ? FieldHover : Field;
        var border = !enabled ? Line : focused ? Accent : hover ? LineHover : Line;
        PaintPlate(g, rect, Radius(font), fill, border);
    }

    /// <summary>Primary and danger command buttons. Quiet buttons use <see cref="PaintField"/> so they match dropdowns.</summary>
    public static void PaintTone(Graphics g, Rectangle bounds, Font font, CommandTone tone, bool hover, bool pressed, bool focused)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill, border;
        if (tone == CommandTone.Primary)
        {
            fill = pressed ? AccentPressed : hover ? AccentHover : Accent;
            border = focused ? FocusOnAccent : fill;
        }
        else
        {
            fill = pressed ? DangerPressed : hover ? DangerHover : Field;
            border = focused || hover ? Danger : Line;
        }
        var rect = new RectangleF(0.5f, 0.5f, Math.Max(1, bounds.Width - 1f), Math.Max(1, bounds.Height - 1f));
        PaintPlate(g, rect, Radius(font), fill, border);
    }

    private static void PaintPlate(Graphics g, RectangleF rect, float radius, Color fill, Color border)
    {
        using var path = Round(rect, radius);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// Checkbox drawn as a sliding switch. The text is the label. It stays a <see cref="CheckBox"/>, so UI Automation
/// still reports the name, the checked state and the toggle action; Space and click still flip it.
/// </summary>
internal sealed class ToggleSwitch : CheckBox
{
    private readonly System.Windows.Forms.Timer _motion = new() { Interval = 15 };
    private float _knob;
    private bool _live;
    private bool _hover;

    public ToggleSwitch()
    {
        AutoSize = true;
        Cursor = Cursors.Hand;
        TabStop = true;
        UseMnemonic = false;
        UseVisualStyleBackColor = false;
        BackColor = SettingsChrome.Bg;
        ForeColor = SettingsChrome.Fg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _motion.Tick += (_, _) => Step();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var track = TrackSize();
        return new Size(track.Width + 10 + text.Width + 2, Math.Max(SettingsChrome.ControlHeight(Font), text.Height + 6));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _knob = Checked ? 1f : 0f;
        _live = true;
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        if (!_live)
        {
            _knob = Checked ? 1f : 0f;
            return;
        }
        _motion.Start();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = TrackRect();
        var on = Enabled && Checked;
        var fill = !Enabled ? Color.FromArgb(40, 48, 58) : on ? SettingsChrome.Accent : _hover ? SettingsChrome.FieldHover : SettingsChrome.TrackOff;
        var edge = Focused && Enabled ? SettingsChrome.Accent : _hover && Enabled && !on ? SettingsChrome.LineHover : SettingsChrome.Line;
        using (var path = SettingsChrome.Round(track, track.Height / 2f))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(on && Enabled ? SettingsChrome.Accent : edge))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        var thumb = track.Height - 6f;
        var travel = Math.Max(0, track.Width - thumb - 6f);
        var x = track.X + 3f + travel * _knob;
        var y = track.Y + (track.Height - thumb) / 2f;
        using (var brush = new SolidBrush(Enabled ? SettingsChrome.Thumb : SettingsChrome.Muted))
            g.FillEllipse(brush, x, y, thumb, thumb);
        var textColor = Enabled ? ForeColor : SettingsChrome.Muted;
        var textRect = new Rectangle((int)track.Right + 10, 0, Math.Max(0, Width - (int)track.Right - 10), Height);
        TextRenderer.DrawText(g, Text, Font, textRect, textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _motion.Dispose();
        base.Dispose(disposing);
    }

    private void Step()
    {
        if (IsDisposed)
        {
            _motion.Stop();
            return;
        }
        var goal = Checked ? 1f : 0f;
        _knob += (goal - _knob) * 0.45f;
        if (Math.Abs(goal - _knob) < 0.02f)
        {
            _knob = goal;
            _motion.Stop();
        }
        Invalidate();
    }

    private Size TrackSize()
    {
        var h = Math.Max(16, SettingsChrome.ControlHeight(Font) - 10);
        return new Size(h * 2 - 2, h);
    }

    private RectangleF TrackRect()
    {
        var size = TrackSize();
        return new RectangleF(1, (Height - size.Height) / 2f, size.Width, size.Height);
    }
}

/// <summary>Square button that draws a play triangle. <see cref="Control.Text"/> is the accessible name and tooltip, not a caption.</summary>
internal sealed class PlayButton : Button
{
    private readonly ToolTip _tip = new() { ShowAlways = true };
    private bool _hover, _down;

    public PlayButton()
    {
        AutoSize = false;
        TabStop = true;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = SettingsChrome.Bg;
        ForeColor = SettingsChrome.Fg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(SettingsChrome.ControlHeight(Font), SettingsChrome.ControlHeight(Font));
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        var h = SettingsChrome.ControlHeight(Font);
        Size = new Size(h, h);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        if (IsHandleCreated && Text.Length > 0) _tip.SetToolTip(this, Text);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Text.Length > 0) _tip.SetToolTip(this, Text);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _down = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _down = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _down = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        SettingsChrome.PaintField(e.Graphics, ClientRectangle, Font, _hover, _down, Focused, Enabled);
        var color = !Enabled ? SettingsChrome.Muted
            : _down ? SettingsChrome.AccentPressed
            : _hover || Focused ? SettingsChrome.Accent
            : SettingsChrome.Fg;
        var h = Math.Max(9f, Height * 0.40f);
        var w = h * 0.88f;
        var cx = Width / 2f + w * 0.12f;
        var cy = Height / 2f + 0.5f;
        PointF[] tri = [new(cx - w / 2f, cy - h / 2f), new(cx + w / 2f, cy), new(cx - w / 2f, cy + h / 2f)];
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        e.Graphics.FillPolygon(brush, tri);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

internal enum CommandTone { Quiet, Primary, Danger }

/// <summary>
/// Flat command button at the same height and radius as the dropdowns. Quiet matches the field, primary is the accent
/// fill, danger keeps the field and uses the danger colour for the label.
/// </summary>
internal sealed class CommandButton : Button
{
    private bool _hover, _down;

    public CommandButton()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TabStop = true;
        Cursor = Cursors.Hand;
        UseMnemonic = false;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = SettingsChrome.Bg;
        FlatAppearance.MouseDownBackColor = SettingsChrome.Bg;
        UseVisualStyleBackColor = false;
        BackColor = SettingsChrome.Bg;
        ForeColor = SettingsChrome.Fg;
        Padding = Padding.Empty;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = Measure();
        AccessibleRole = AccessibleRole.PushButton;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public CommandTone Tone { get; set; } = CommandTone.Quiet;

    public override Size GetPreferredSize(Size proposedSize) => Measure();

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Size = Measure();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = Measure();
        Parent?.PerformLayout();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _down = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _down = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _down = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(SettingsChrome.Bg);
        if (!Enabled || Tone == CommandTone.Quiet)
            SettingsChrome.PaintField(e.Graphics, ClientRectangle, Font, _hover, _down, Focused, Enabled);
        else
            SettingsChrome.PaintTone(e.Graphics, ClientRectangle, Font, Tone, _hover, _down, Focused);
        var color = !Enabled ? SettingsChrome.Muted
            : Tone == CommandTone.Primary ? SettingsChrome.AccentText
            : Tone == CommandTone.Danger ? SettingsChrome.Danger
            : SettingsChrome.Fg;
        var rect = new Rectangle(8, 0, Math.Max(0, Width - 16), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, rect, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private Size Measure()
    {
        var h = SettingsChrome.ControlHeight(Font);
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return new Size(Math.Max(h, text.Width + 28), h);
    }
}

/// <summary>
/// Closed list styled like the other settings controls. Replaces the native combo, which cannot take the same radius,
/// border and colours. Keyboard: Alt+Down or Enter opens, arrows move, Enter commits, Escape cancels, letters jump.
/// </summary>
internal sealed class SettingsDropDown : Control
{
    private readonly List<string> _items = [];
    private DropPopup? _popup;
    private DismissClicks? _filter;
    private int _index = -1;
    private int _highlight;
    private bool _hover;
    private bool _open;
    private bool _opening;
    private string _typed = "";
    private DateTime _typedAt;

    public SettingsDropDown()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = SettingsChrome.Bg;
        ForeColor = SettingsChrome.Fg;
        Cursor = Cursors.Hand;
        TabStop = true;
        Height = SettingsChrome.ControlHeight(Font);
        AccessibleRole = AccessibleRole.ComboBox;
    }

    public event EventHandler? SelectedIndexChanged;

    public int Count => _items.Count;

    public string? SelectedText => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _index;
        set => SelectIndex(value, notify: true);
    }

    /// <summary>The row label. Kept in sync when the language changes.</summary>
    public void BindLabel(Control label)
    {
        AccessibleName = label.Text;
        label.TextChanged += (_, _) => AccessibleName = label.Text;
    }

    /// <summary>Swaps the list without a notification when the selected row does not change.</summary>
    public void Replace(IEnumerable<string> texts, int selected)
    {
        ClosePopup();
        _items.Clear();
        _items.AddRange(texts);
        var next = _items.Count == 0 ? -1 : Math.Clamp(selected, 0, _items.Count - 1);
        var changed = next != _index;
        _index = next;
        _highlight = Math.Max(0, next);
        Invalidate();
        if (changed) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = SettingsChrome.ControlHeight(Font);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        if (!Enabled) ClosePopup();
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && Enabled) TogglePopup();
    }

    protected override bool IsInputKey(Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        if (code is Keys.Up or Keys.Down or Keys.Enter or Keys.Escape or Keys.F4 or Keys.Home or Keys.End)
            return true;
        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!Enabled) return;
        if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode is Keys.Down or Keys.Up))
        {
            TogglePopup();
            e.Handled = true;
            return;
        }
        if (_open)
        {
            switch (e.KeyCode)
            {
                case Keys.Down: MoveHighlight(1); break;
                case Keys.Up: MoveHighlight(-1); break;
                case Keys.Home: HoverItem(0); break;
                case Keys.End: HoverItem(_items.Count - 1); break;
                case Keys.Enter:
                case Keys.Space: CommitHighlight(); break;
                case Keys.Escape:
                case Keys.Tab: ClosePopup(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        switch (e.KeyCode)
        {
            case Keys.Down: MoveSelection(1); break;
            case Keys.Up: MoveSelection(-1); break;
            case Keys.Home: SelectIndex(0, notify: true); break;
            case Keys.End: SelectIndex(_items.Count - 1, notify: true); break;
            case Keys.Enter:
            case Keys.Space: OpenPopup(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (!Enabled || e.KeyChar < ' ') return;
        if ((DateTime.UtcNow - _typedAt).TotalSeconds > 0.8) _typed = "";
        _typed += char.ToLowerInvariant(e.KeyChar);
        _typedAt = DateTime.UtcNow;
        var start = _open ? _highlight : _index;
        for (var n = 1; n <= _items.Count; n++)
        {
            var i = ((start < 0 ? 0 : start) + n) % _items.Count;
            if (_items[i].StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase))
            {
                if (_open) HoverItem(i);
                else SelectIndex(i, notify: true);
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        SettingsChrome.PaintField(e.Graphics, ClientRectangle, Font, _hover, pressed: false, Focused || _open, Enabled);
        var text = SelectedText ?? "";
        var color = Enabled ? (text.Length == 0 ? SettingsChrome.Muted : ForeColor) : SettingsChrome.Muted;
        var textRect = new Rectangle(10, 0, Math.Max(0, Width - 32), Height);
        TextRenderer.DrawText(e.Graphics, text, Font, textRect, color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        PaintChevron(e.Graphics);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!_opening) ClosePopup();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new DropAccess(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClosePopup();
        base.Dispose(disposing);
    }

    internal int Highlight => _highlight;
    internal IReadOnlyList<string> Items => _items;

    internal void HoverItem(int index)
    {
        if (_items.Count == 0) return;
        _highlight = Math.Clamp(index, 0, _items.Count - 1);
        _popup?.Invalidate();
    }

    internal void Commit(int index)
    {
        SelectIndex(index, notify: true);
        ClosePopup();
    }

    private void SelectIndex(int value, bool notify)
    {
        var next = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
        if (next == _index)
        {
            Invalidate();
            return;
        }
        _index = next;
        _highlight = Math.Max(0, next);
        Invalidate();
        if (notify) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        SelectIndex((_index < 0 ? 0 : _index) + delta, notify: true);
    }

    private void MoveHighlight(int delta) => HoverItem((_highlight < 0 ? 0 : _highlight) + delta);

    private void CommitHighlight()
    {
        if (_items.Count == 0) { ClosePopup(); return; }
        Commit(_highlight);
    }

    private void TogglePopup()
    {
        if (_open) ClosePopup();
        else OpenPopup();
    }

    private void OpenPopup()
    {
        if (_open || _items.Count == 0 || FindForm() is not { } host) return;
        _highlight = Math.Max(0, _index);
        _open = true;
        _opening = true;
        _popup = new DropPopup(this);
        var origin = PointToScreen(new Point(0, Height + 4));
        var work = Screen.FromControl(this).WorkingArea;
        if (origin.Y + _popup.Height > work.Bottom)
            origin.Y = PointToScreen(Point.Empty).Y - _popup.Height - 4;
        origin.X = Math.Clamp(origin.X, work.Left, Math.Max(work.Left, work.Right - _popup.Width));
        _popup.Location = origin;
        _filter = new DismissClicks(this);
        Application.AddMessageFilter(_filter);
        host.Move += CloseOnHost;
        host.Resize += CloseOnHost;
        try
        {
            _popup.Show(host);
        }
        finally
        {
            _opening = false;
        }
        Invalidate();
    }

    private void CloseOnHost(object? sender, EventArgs e)
    {
        if (!_opening) ClosePopup();
    }

    private void ClosePopup()
    {
        if (!_open && _popup is null) return;
        _open = false;
        if (_filter is not null)
        {
            Application.RemoveMessageFilter(_filter);
            _filter = null;
        }
        if (FindForm() is { } host)
        {
            host.Move -= CloseOnHost;
            host.Resize -= CloseOnHost;
        }
        if (_popup is { } popup)
        {
            _popup = null;
            popup.Close();
            popup.Dispose();
        }
        Invalidate();
    }

    private void PaintChevron(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var cx = Width - 16f;
        var cy = Height / 2f + (_open ? -1f : 1f);
        var color = Enabled ? (_open || Focused ? SettingsChrome.Accent : SettingsChrome.Muted) : SettingsChrome.Line;
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (_open)
            g.DrawLines(pen, new[] { new PointF(cx - 4, cy + 2), new PointF(cx, cy - 2), new PointF(cx + 4, cy + 2) });
        else
            g.DrawLines(pen, new[] { new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2) });
    }

    private sealed class DropAccess : ControlAccessibleObject
    {
        private readonly SettingsDropDown _owner;
        public DropAccess(SettingsDropDown owner) : base(owner) => _owner = owner;
        public override string? Name => _owner.AccessibleName ?? _owner.SelectedText;
        public override string? Value => _owner.SelectedText;
        public override AccessibleRole Role => AccessibleRole.ComboBox;
    }

    /// <summary>Closes the list when the pointer goes down outside it. Clicks on the closed control are left to toggle it.</summary>
    private sealed class DismissClicks : IMessageFilter
    {
        private readonly SettingsDropDown _owner;
        public DismissClicks(SettingsDropDown owner) => _owner = owner;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is not (0x201 or 0x204 or 0x207 or 0xA1 or 0x20A)) return false;
            if (_owner.IsDisposed) return false;
            var p = MousePosition;
            if (_owner.RectangleToScreen(_owner.ClientRectangle).Contains(p)) return false;
            if (_owner._popup is { IsDisposed: false } pop && pop.Bounds.Contains(p) && m.Msg != 0x20A) return false;
            _owner.BeginInvoke(new Action(_owner.ClosePopup));
            return false;
        }
    }

    private sealed class DropPopup : Form
    {
        private readonly SettingsDropDown _owner;
        private const int Pad = 4;

        public DropPopup(SettingsDropDown owner)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = SettingsChrome.Field;
            Font = owner.Font;
            var item = owner.Height;
            Width = owner.Width;
            Height = Pad * 2 + Math.Max(1, owner.Count) * item;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            var radius = SettingsChrome.Radius(Font);
            using var path = SettingsChrome.Round(new RectangleF(0, 0, Width, Height), radius);
            Region = new Region(path);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = SettingsChrome.Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), SettingsChrome.Radius(Font)))
            using (var brush = new SolidBrush(SettingsChrome.Field))
            using (var pen = new Pen(SettingsChrome.Line))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            var itemH = _owner.Height;
            for (var i = 0; i < _owner.Count; i++)
            {
                var row = new Rectangle(Pad, Pad + i * itemH, Width - Pad * 2, itemH);
                var selected = i == _owner.SelectedIndex;
                var hot = i == _owner.Highlight;
                if (selected || hot)
                {
                    using var fill = new SolidBrush(selected ? SettingsChrome.ListOn : SettingsChrome.ListHover);
                    using var path = SettingsChrome.Round(row, Math.Max(4, SettingsChrome.Radius(Font) - 1));
                    g.FillPath(fill, path);
                }
                var text = new Rectangle(row.X + 8, row.Y, Math.Max(0, row.Width - 16), row.Height);
                TextRenderer.DrawText(g, _owner.Items[i], Font, text, SettingsChrome.Fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var i = IndexAt(e.Y);
            if (i >= 0) _owner.HoverItem(i);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var i = IndexAt(e.Y);
            if (i >= 0) _owner.Commit(i);
        }

        private int IndexAt(int y)
        {
            var i = (y - Pad) / Math.Max(1, _owner.Height);
            return i >= 0 && i < _owner.Count ? i : -1;
        }
    }
}
