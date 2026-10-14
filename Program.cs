using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Pointer;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Always give the user their cursor back, even on crash or abnormal exit.
        CursorHider.ForceRestore(); // in case a previous run was killed while hidden
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            CursorHider.ForceRestore();
            MessageBox.Show(e.Exception.ToString(), "Pointer crashed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => CursorHider.ForceRestore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CursorHider.ForceRestore();
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => CursorHider.ForceRestore();

        var settings = Settings.Load();
        using var overlay = new OverlayForm(settings);
        using var tray = new NotifyIcon
        {
            Icon = CreateIcon(Color.FromArgb(settings.LineColor)),
            Text = settings.TrayText,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        // Tray dot follows the current line color.
        void UpdateIcon()
        {
            var old = tray.Icon;
            tray.Icon = CreateIcon(Color.FromArgb(settings.LineColor));
            tray.Text = settings.TrayText;
            old?.Dispose();
        }
        SettingsForm? open = null;
        void ShowSettings()
        {
            if (open != null) { open.Activate(); return; }
            using (open = new SettingsForm(settings, () => { settings.Save(); overlay.ApplySettings(); UpdateIcon(); }))
                open.ShowDialog();
            open = null;
        }
        tray.ContextMenuStrip.Items.Add("Settings...", null, (_, _) => ShowSettings());
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Application.Exit());
        tray.DoubleClick += (_, _) => ShowSettings();
        overlay.LineWidthChanged += w => open?.SetLineWidth(w);
        overlay.LineColorChanged += c => { open?.SetLineColor(c); UpdateIcon(); };

        overlay.Show();
        PromoteTrayIcon();
        tray.ShowBalloonTip(3000, "Pointer is running", settings.BindingHelp, ToolTipIcon.Info);
        Application.Run();
        CursorHider.ForceRestore();
        tray.Visible = false;
        tray.Icon?.Dispose();
    }

    // Windows 11 hides new tray icons in the overflow menu. Explorer registers the icon under
    // NotifyIconSettings shortly after it first appears; mark it as shown on the taskbar unless
    // the user has already chosen a setting for it.
    static void PromoteTrayIcon()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        int tries = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            bool done = ++tries >= 15;
            try
            {
                using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
                foreach (var name in root?.GetSubKeyNames() ?? [])
                {
                    using var key = root!.OpenSubKey(name, writable: true);
                    if (key?.GetValue("ExecutablePath") is not string path || !string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    if (key.GetValue("IsPromoted") == null) key.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    done = true;
                }
            }
            catch { done = true; }
            if (done) timer.Dispose();
        };
        timer.Start();
    }

    static Icon CreateIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var glow = new SolidBrush(Color.FromArgb(160, 255, 255, 255));
            using var dot = new SolidBrush(Color.FromArgb(255, color));
            g.FillEllipse(glow, 8, 8, 16, 16);
            g.FillEllipse(dot, 11, 11, 10, 10);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        return icon;
    }
}

[Flags]
enum HotkeyModifiers { Ctrl = 1, Alt = 2, Win = 4, Shift = 8 }

enum PointerInput { Left, Right, Middle, Wheel }

sealed record MouseBinding(HotkeyModifiers Modifiers, PointerInput Input)
{
    public bool Matches(HotkeyModifiers held) => Modifiers != 0 && (held & Modifiers) == Modifiers;
    public override string ToString() => $"{Settings.FormatHotkey(Modifiers)}+{(Input == PointerInput.Wheel ? "Wheel" : $"{Input}-drag")}";
}

sealed class Settings
{
    public const int MinLine = 1, MaxLine = 30, MaxGlow = 20;
    public const int MinDelayMs = 300, MaxDelayMs = 10000;
    public const int MinOpacity = 10;
    public const int MaxSnap = 90, MinSnapTolerance = 1, MaxSnapTolerance = 15;

    // Quick-pick colors (RGB), also cycled with the activation hotkey + Shift + Wheel.
    public static readonly int[] Palette =
    [
        0xF44336, 0xE91E63, 0x9C27B0, 0x673AB7, 0x3F51B5, 0x2196F3, 0x03A9F4, 0x00BCD4,
        0x009688, 0x4CAF50, 0x8BC34A, 0xCDDC39, 0xFFEB3B, 0xFFC107, 0xFF9800, 0xFF5722,
        0x795548, 0x9E9E9E, 0x607D8B,
    ];
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pointer", "settings.json");

    public int LineColor { get; set; } = Color.FromArgb(0xFF, 0x00, 0x00).ToArgb();
    public int GlowColor { get; set; } = Color.FromArgb(0xF5, 0xF5, 0xF5).ToArgb();
    public int LineWidth { get; set; } = 3;
    public int GlowWidth { get; set; } = 1;
    public int LineOpacity { get; set; } = 75;
    public int GlowOpacity { get; set; } = 10;
    public int DelayMs { get; set; } = 500;
    public int SnapAngle { get; set; } = 30; // straight-line angle step in degrees; 0 = free
    public int SnapTolerance { get; set; } = 3; // degrees around a snap angle within which the line locks to it
    // Retained to migrate settings written before per-action bindings.
    public HotkeyModifiers ActivationModifiers { get; set; } = HotkeyModifiers.Ctrl | HotkeyModifiers.Win;
    MouseBinding? _drawBinding, _straightBinding, _resizeBinding, _colorBinding;
    public MouseBinding DrawBinding
    {
        get => _drawBinding ??= new(ActivationModifiers, PointerInput.Left);
        set => _drawBinding = value;
    }
    public MouseBinding StraightBinding
    {
        get => _straightBinding ??= new(ActivationModifiers, PointerInput.Right);
        set => _straightBinding = value;
    }
    public MouseBinding ResizeBinding
    {
        get => _resizeBinding ??= new(ActivationModifiers, PointerInput.Wheel);
        set => _resizeBinding = value;
    }
    public MouseBinding ColorBinding
    {
        get => _colorBinding ??= new(ActivationModifiers | HotkeyModifiers.Shift, PointerInput.Wheel);
        set => _colorBinding = value;
    }
    public HotkeyModifiers StraightModifier { get; set; } = HotkeyModifiers.Shift;

    [System.Text.Json.Serialization.JsonIgnore]
    public string TrayText => $"Pointer - Draw: {DrawBinding}; Straight: {StraightBinding}";

    [System.Text.Json.Serialization.JsonIgnore]
    public string BindingHelp => $"Draw: {DrawBinding}. Straight lines: {StraightBinding} (kept until its modifiers are released).\n" +
        $"Resize: {ResizeBinding}. Color: {ColorBinding}.\n" +
        (StraightModifier == 0 ? "Straight-line modifier: disabled." : $"Hold {FormatHotkey(StraightModifier)} during freehand drawing for straight lines.");

    public static string FormatHotkey(HotkeyModifiers modifiers) =>
        string.Join("+", new[] { HotkeyModifiers.Ctrl, HotkeyModifiers.Alt, HotkeyModifiers.Win, HotkeyModifiers.Shift }
            .Where(m => (modifiers & m) != 0).Select(m => m.ToString()));

    // Extra modifiers are allowed; the more specific matching combination wins.
    public static MouseBinding? SelectBinding(MouseBinding first, MouseBinding second, PointerInput input, HotkeyModifiers held)
    {
        bool a = first.Input == input && first.Matches(held), b = second.Input == input && second.Matches(held);
        if (!a) return b ? second : null;
        return b && System.Numerics.BitOperations.PopCount((uint)second.Modifiers) >=
            System.Numerics.BitOperations.PopCount((uint)first.Modifiers) ? second : first;
    }

    public string? BindingError()
    {
        var bindings = new[] { DrawBinding, StraightBinding, ResizeBinding, ColorBinding };
        if (bindings.Any(b => (int)b.Modifiers is < 1 or > 15))
            return "Each action needs at least one Ctrl, Alt, Win, or Shift modifier.";
        if (DrawBinding.Input is < PointerInput.Left or > PointerInput.Middle ||
            StraightBinding.Input is < PointerInput.Left or > PointerInput.Middle ||
            ResizeBinding.Input != PointerInput.Wheel || ColorBinding.Input != PointerInput.Wheel)
            return "Drawing needs a left, right, or middle button; resizing and color changes need the wheel.";
        if (DrawBinding == StraightBinding || ResizeBinding == ColorBinding)
            return "Each action must use a different binding.";
        if ((int)StraightModifier is < 0 or > 15)
            return "The straight-line modifier is invalid.";
        if (StraightModifier != 0 && (DrawBinding.Modifiers & StraightModifier) == StraightModifier)
            return "The straight-line modifier must include a key not already required for freehand drawing.";
        return null;
    }

    public static Settings Load()
    {
        try
        {
            var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            s.LineWidth = Math.Clamp(s.LineWidth, MinLine, MaxLine);
            s.GlowWidth = Math.Clamp(s.GlowWidth, 0, MaxGlow);
            s.LineOpacity = Math.Clamp(s.LineOpacity, MinOpacity, 100);
            s.GlowOpacity = Math.Clamp(s.GlowOpacity, MinOpacity, 100);
            s.DelayMs = Math.Clamp(s.DelayMs, MinDelayMs, MaxDelayMs);
            s.SnapAngle = Math.Clamp(s.SnapAngle, 0, MaxSnap);
            s.SnapTolerance = Math.Clamp(s.SnapTolerance, MinSnapTolerance, MaxSnapTolerance);
            if ((int)s.ActivationModifiers is < 1 or > 7)
            {
                MessageBox.Show("The saved activation hotkey is invalid. Using Ctrl+Win instead.",
                    "Pointer Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                s.ActivationModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Win;
            }
            if (s.BindingError() is string error)
            {
                MessageBox.Show($"{error}\nUsing the default mouse bindings.", "Pointer Settings",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                var defaults = new Settings();
                s.DrawBinding = defaults.DrawBinding;
                s.StraightBinding = defaults.StraightBinding;
                s.ResizeBinding = defaults.ResizeBinding;
                s.ColorBinding = defaults.ColorBinding;
                s.StraightModifier = defaults.StraightModifier;
            }
            return s;
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save settings:\n{ex.Message}", "Pointer Settings",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

sealed class BindingEditor : FlowLayoutPanel
{
    readonly ComboBox _modifiers = ModifierPicker(false);
    readonly ComboBox _input = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };

    public static ComboBox ModifierPicker(bool allowNone)
    {
        var picker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        if (allowNone) picker.Items.Add("Disabled");
        picker.Items.AddRange(Enumerable.Range(1, 15).Select(i => Settings.FormatHotkey((HotkeyModifiers)i)).ToArray());
        picker.SelectedIndex = 0;
        return picker;
    }

    public BindingEditor(bool wheel)
    {
        AutoSize = true;
        Margin = Padding.Empty;
        _input.Items.AddRange(wheel ? new object[] { "Wheel" } : new object[] { "Left-drag", "Right-drag", "Middle-drag" });
        _input.SelectedIndex = 0;
        Controls.AddRange([_modifiers, _input]);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public MouseBinding Binding
    {
        get => new((HotkeyModifiers)(_modifiers.SelectedIndex + 1),
            _input.Items.Count == 1 ? PointerInput.Wheel : (PointerInput)_input.SelectedIndex);
        set
        {
            _modifiers.SelectedIndex = (int)value.Modifiers - 1;
            _input.SelectedIndex = value.Input == PointerInput.Wheel ? 0 : (int)value.Input;
        }
    }
}

sealed class SettingsForm : Form
{
    readonly Settings _s;
    readonly Action _onApply;
    readonly Button _line = new() { Width = 80 }, _glow = new() { Width = 80 };
    readonly TrackBar _size = new() { Width = 200, Minimum = Settings.MinLine, Maximum = Settings.MaxLine, TickFrequency = 5, SmallChange = 1, LargeChange = 5 };
    readonly Label _sizeText = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly TrackBar _glowSize = new() { Width = 200, Minimum = 0, Maximum = Settings.MaxGlow, TickFrequency = 5, SmallChange = 1, LargeChange = 5 };
    readonly Label _glowSizeText = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly TrackBar _lineAlpha = NewOpacityBar(), _glowAlpha = NewOpacityBar();
    readonly Label _lineAlphaText = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Label _glowAlphaText = new() { AutoSize = true, Anchor = AnchorStyles.Left };

    static TrackBar NewOpacityBar() => new() { Width = 150, Minimum = Settings.MinOpacity, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10 };
    readonly TrackBar _delay = new() { Width = 200, Minimum = Settings.MinDelayMs / 100, Maximum = Settings.MaxDelayMs / 100, TickFrequency = 10, SmallChange = 1, LargeChange = 5 };
    readonly Label _delayText = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly TrackBar _snap = new() { Width = 200, Minimum = 0, Maximum = Settings.MaxSnap / 5, TickFrequency = 3, SmallChange = 1, LargeChange = 3 };
    readonly Label _snapText = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly TrackBar _snapTol = new() { Width = 150, Minimum = Settings.MinSnapTolerance, Maximum = Settings.MaxSnapTolerance, TickFrequency = 5, SmallChange = 1, LargeChange = 3 };
    readonly Label _snapTolText = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly BindingEditor _drawBinding = new(false), _straightBinding = new(false), _resizeBinding = new(true), _colorBinding = new(true);
    readonly ComboBox _straightModifier = BindingEditor.ModifierPicker(true);
    readonly Label _hotkeyHelp = new() { AutoSize = true, MaximumSize = new Size(580, 0), Margin = new Padding(3, 6, 3, 10) };
    Color _lineColor, _glowColor;

    public SettingsForm(Settings s, Action onApply)
    {
        _onApply = onApply;
        _s = s;
        _lineColor = Color.FromArgb(s.LineColor);
        _glowColor = Color.FromArgb(s.GlowColor);
        _size.Value = Math.Clamp(s.LineWidth, _size.Minimum, _size.Maximum);
        _glowSize.Value = Math.Clamp(s.GlowWidth, _glowSize.Minimum, _glowSize.Maximum);
        _lineAlpha.Value = Math.Clamp(s.LineOpacity, Settings.MinOpacity, 100);
        _glowAlpha.Value = Math.Clamp(s.GlowOpacity, Settings.MinOpacity, 100);
        _delay.Value = Math.Clamp(s.DelayMs / 100, _delay.Minimum, _delay.Maximum);
        _snap.Value = Math.Clamp(s.SnapAngle / 5, _snap.Minimum, _snap.Maximum);
        _snapTol.Value = Math.Clamp(s.SnapTolerance, _snapTol.Minimum, _snapTol.Maximum);
        LoadBindings(s);

        Text = "Pointer Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var ok = new Button { Text = "OK" };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = "Defaults", AutoSize = true };
        var apply = new Button { Text = "Apply" };
        AcceptButton = ok;
        CancelButton = cancel;

        _line.Click += (_, _) => Pick(_line, _lineColor, c => _lineColor = c);
        _glow.Click += (_, _) => Pick(_glow, _glowColor, c => _glowColor = c);
        _size.ValueChanged += (_, _) => UpdateUi();
        _glowSize.ValueChanged += (_, _) => UpdateUi();
        _lineAlpha.ValueChanged += (_, _) => UpdateUi();
        _glowAlpha.ValueChanged += (_, _) => UpdateUi();
        _delay.ValueChanged += (_, _) => UpdateUi();
        _snap.ValueChanged += (_, _) => UpdateUi();
        _snapTol.ValueChanged += (_, _) => UpdateUi();
        reset.Click += (_, _) =>
        {
            var d = new Settings();
            _lineColor = Color.FromArgb(d.LineColor); _glowColor = Color.FromArgb(d.GlowColor);
            _size.Value = d.LineWidth; _glowSize.Value = d.GlowWidth;
            _lineAlpha.Value = d.LineOpacity; _glowAlpha.Value = d.GlowOpacity;
            _delay.Value = d.DelayMs / 100;
            _snap.Value = d.SnapAngle / 5;
            _snapTol.Value = d.SnapTolerance;
            LoadBindings(d);
            UpdateUi();
        };
        ok.Click += (_, _) => { if (Apply()) DialogResult = DialogResult.OK; };
        apply.Click += (_, _) => Apply();

        var grid = new TableLayoutPanel { ColumnCount = 6, AutoSize = true, Dock = DockStyle.Fill };
        Label L(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left };
        _line.Anchor = _glow.Anchor = AnchorStyles.Left;
        grid.Controls.Add(L("Size"), 2, 0); grid.Controls.Add(L("Opacity"), 4, 0);
        grid.Controls.Add(L("Line:"), 0, 1); grid.Controls.Add(_line, 1, 1); grid.Controls.Add(_size, 2, 1); grid.Controls.Add(_sizeText, 3, 1);
        grid.Controls.Add(_lineAlpha, 4, 1); grid.Controls.Add(_lineAlphaText, 5, 1);
        grid.Controls.Add(L("Glow:"), 0, 2); grid.Controls.Add(_glow, 1, 2); grid.Controls.Add(_glowSize, 2, 2); grid.Controls.Add(_glowSizeText, 3, 2);
        grid.Controls.Add(_glowAlpha, 4, 2); grid.Controls.Add(_glowAlphaText, 5, 2);
        grid.Controls.Add(L("Fade delay:"), 0, 3); grid.Controls.Add(_delay, 2, 3); grid.Controls.Add(_delayText, 3, 3);
        grid.Controls.Add(L("Snap angle:"), 0, 4); grid.Controls.Add(_snap, 2, 4); grid.Controls.Add(_snapText, 3, 4);
        grid.Controls.Add(_snapTol, 4, 4); grid.Controls.Add(_snapTolText, 5, 4);
        var bindingRows = new (string Label, Control Editor)[]
        {
            ("Freehand:", _drawBinding), ("Straight line:", _straightBinding),
            ("Resize:", _resizeBinding), ("Cycle color:", _colorBinding), ("Straight modifier:", _straightModifier)
        };
        for (int i = 0; i < bindingRows.Length; i++)
        {
            grid.Controls.Add(L(bindingRows[i].Label), 0, 5 + i);
            grid.SetColumnSpan(grid.GetControlFromPosition(0, 5 + i)!, 2);
            grid.Controls.Add(bindingRows[i].Editor, 2, 5 + i);
            grid.SetColumnSpan(bindingRows[i].Editor, 4);
        }
        grid.Controls.Add(_hotkeyHelp, 0, 10);
        grid.SetColumnSpan(_hotkeyHelp, 6);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { apply, cancel, ok, reset });
        grid.Controls.Add(buttons, 0, 11);
        grid.SetColumnSpan(buttons, 6);
        Controls.Add(grid);

        UpdateUi();
    }

    public void SetLineWidth(int w) => _size.Value = Math.Clamp(w, _size.Minimum, _size.Maximum);
    public void SetLineColor(Color c) { _lineColor = c; UpdateUi(); }

    void LoadBindings(Settings settings)
    {
        _drawBinding.Binding = settings.DrawBinding;
        _straightBinding.Binding = settings.StraightBinding;
        _resizeBinding.Binding = settings.ResizeBinding;
        _colorBinding.Binding = settings.ColorBinding;
        _straightModifier.SelectedIndex = (int)settings.StraightModifier;
    }

    bool Apply()
    {
        var bindings = new Settings
        {
            DrawBinding = _drawBinding.Binding, StraightBinding = _straightBinding.Binding,
            ResizeBinding = _resizeBinding.Binding, ColorBinding = _colorBinding.Binding,
            StraightModifier = (HotkeyModifiers)_straightModifier.SelectedIndex
        };
        if (bindings.BindingError() is string error)
        {
            MessageBox.Show(this, error, "Conflicting bindings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        _s.LineColor = _lineColor.ToArgb();
        _s.GlowColor = _glowColor.ToArgb();
        _s.LineWidth = _size.Value;
        _s.GlowWidth = _glowSize.Value;
        _s.LineOpacity = _lineAlpha.Value;
        _s.GlowOpacity = _glowAlpha.Value;
        _s.DelayMs = _delay.Value * 100;
        _s.SnapAngle = _snap.Value * 5;
        _s.SnapTolerance = _snapTol.Value;
        _s.DrawBinding = bindings.DrawBinding;
        _s.StraightBinding = bindings.StraightBinding;
        _s.ResizeBinding = bindings.ResizeBinding;
        _s.ColorBinding = bindings.ColorBinding;
        _s.StraightModifier = bindings.StraightModifier;
        _onApply();
        return true;
    }

    static int[] Palette => Settings.Palette;

    void Pick(Button anchor, Color current, Action<Color> set)
    {
        var drop = new ToolStripDropDown { Padding = new Padding(4) };
        var panel = new FlowLayoutPanel { Width = 8 * 26, AutoSize = true, MaximumSize = new Size(8 * 26, 0), Margin = Padding.Empty };
        void Choose(Color c) { drop.Close(); set(c); UpdateUi(); }

        foreach (var rgb in Palette)
        {
            var c = Color.FromArgb(unchecked((int)0xFF000000) | rgb);
            var sw = new Button { Size = new Size(22, 22), Margin = new Padding(2), BackColor = c, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            sw.FlatAppearance.BorderColor = c.ToArgb() == current.ToArgb() ? Color.Black : c;
            sw.FlatAppearance.BorderSize = 2;
            sw.Click += (_, _) => Choose(c);
            panel.Controls.Add(sw);
        }
        var more = new Button { Text = "More…", AutoSize = true, Margin = new Padding(2) };
        more.Click += (_, _) =>
        {
            drop.Close();
            using var dlg = new ColorDialog { Color = current, FullOpen = true, CustomColors = Palette.Take(16).Select(ToColorRef).ToArray() };
            if (dlg.ShowDialog(this) == DialogResult.OK) Choose(dlg.Color);
        };
        panel.Controls.Add(more);
        panel.SetFlowBreak(panel.Controls[^2], true);

        drop.Items.Add(new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty });
        drop.Closed += (_, _) => BeginInvoke(drop.Dispose);
        drop.Show(anchor, new Point(0, anchor.Height));
    }

    // ColorDialog custom colors are COLORREF (0x00BBGGRR).
    static int ToColorRef(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    void UpdateUi()
    {
        Style(_line, _lineColor);
        Style(_glow, _glowColor);
        _sizeText.Text = $"{_size.Value} px";
        _glowSizeText.Text = $"{_glowSize.Value} px";
        _lineAlphaText.Text = $"{_lineAlpha.Value} %";
        _glowAlphaText.Text = $"{_glowAlpha.Value} %";
        _delayText.Text = $"{_delay.Value / 10.0:0.0} s";
        _snapText.Text = _snap.Value == 0 ? "Off" : $"{_snap.Value * 5}°";
        _snapTolText.Text = $"±{_snapTol.Value}°";
        _snapTol.Enabled = _snap.Value > 0;
        _hotkeyHelp.Text = "Straight-line strokes stay until their binding's modifiers are released, then fade together.\n" +
            "The straight modifier changes freehand drawing to straight lines while held. Extra keys are allowed; " +
            "the matching binding with more modifiers wins (ties favor straight lines/color). Windows shortcuts may still apply.";
    }

    static void Style(Button b, Color c)
    {
        b.BackColor = c;
        b.ForeColor = c.GetBrightness() > 0.5f ? Color.Black : Color.White;
        b.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}

sealed class OverlayForm : Form
{
    const int TailMs = 450; // how long the "falling star" retraction takes after the delay
    const int MaxStrokes = 50, MaxPoints = 5000;
    const int SaveDebounceMs = 500;

    sealed class Stroke
    {
        public readonly List<Point> Pts = [];
        public long? Up;
        public bool Group;
        public HotkeyModifiers ReleaseModifiers;
    }

    readonly Settings _settings;
    readonly List<Stroke> _strokes = [];
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    readonly ImageAttributes _attrs = new();
    readonly Native.HookProc _hookProc;
    Rectangle _screen = SystemInformation.VirtualScreen;
    Rectangle _lastBox; // area drawn in the previous frame, cleared on the next one
    IntPtr _hook, _memDc, _dib;
    Bitmap? _bmp, _layer;
    Graphics? _g, _lg;
    Pen? _glowPen, _linePen;
    bool _drawing, _dirty = true, _fullUpdate = true;
    PointerInput _drawingButton;
    int _wheelSteps, _colorSteps;
    int? _anchor; // start of the current straight segment
    long _saveAt = -1;
    Point? _dot;

    public event Action<int>? LineWidthChanged;
    public event Action<Color>? LineColorChanged;

    public OverlayForm(Settings settings)
    {
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = _screen;

        BuildPens();

        _hookProc = MouseHook;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // Layered (per-pixel alpha), click-through, no focus stealing, hidden from Alt+Tab.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CreateSurface();
        Render();
    }

    static HotkeyModifiers HeldModifiers
    {
        get
        {
            HotkeyModifiers held = 0;
            if ((Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0) held |= HotkeyModifiers.Ctrl;
            if ((Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0) held |= HotkeyModifiers.Alt;
            if (((Native.GetAsyncKeyState(Native.VK_LWIN) | Native.GetAsyncKeyState(Native.VK_RWIN)) & 0x8000) != 0)
                held |= HotkeyModifiers.Win;
            if ((Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0) held |= HotkeyModifiers.Shift;
            return held;
        }
    }

    public void ApplySettings() => BuildPens();

    void BuildPens()
    {
        static Pen Make(Color c, float w) => new(Color.FromArgb(255, c), w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        float lineW = _settings.LineWidth, glowW = _settings.GlowWidth;
        _glowPen?.Dispose();
        _linePen?.Dispose();
        // Glow: solid band of glowW px on each side of the line. Line: solid core on top.
        _glowPen = glowW > 0 ? Make(Color.FromArgb(_settings.GlowColor), lineW + 2 * glowW) : null;
        _linePen = Make(Color.FromArgb(_settings.LineColor), lineW);
        _dirty = true;
    }

    // Low-level hooks must return fast (Windows silently removes slow ones), and an exception
    // escaping here would crash the app; heavy work is deferred to Tick.
    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                var p = new Point(info.pt.X - _screen.X, info.pt.Y - _screen.Y);
                if (HandleMouse((int)wParam, p, info.mouseData, HeldModifiers)) return 1;
            }
            catch { /* never let the hook fail */ }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static PointerInput ButtonFor(int message) => message switch
    {
        Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP => PointerInput.Left,
        Native.WM_RBUTTONDOWN or Native.WM_RBUTTONUP => PointerInput.Right,
        _ => PointerInput.Middle
    };

    bool HandleMouse(int message, Point p, int mouseData, HotkeyModifiers held)
    {
        switch (message)
        {
            case Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN when !_drawing:
                var binding = Settings.SelectBinding(_settings.DrawBinding, _settings.StraightBinding, ButtonFor(message), held);
                if (binding == null) return false;
                _drawing = true;
                _drawingButton = binding.Input;
                _anchor = null;
                var stroke = new Stroke
                {
                    Group = binding == _settings.StraightBinding,
                    ReleaseModifiers = binding.Modifiers
                };
                stroke.Pts.Add(p);
                _strokes.Add(stroke);
                if (_strokes.Count > MaxStrokes) _strokes.RemoveAt(0);
                return true; // swallow: the click must not reach other apps
            case Native.WM_LBUTTONUP or Native.WM_RBUTTONUP or Native.WM_MBUTTONUP when _drawing && _drawingButton == ButtonFor(message):
                _drawing = false;
                if (!_strokes[^1].Group) _strokes[^1].Up = _clock.ElapsedMilliseconds; // fade countdown starts now
                return true;
            case Native.WM_MOUSEMOVE when _drawing:
                var pts = _strokes[^1].Pts;
                if (_strokes[^1].Group || (_settings.StraightModifier != 0 &&
                    (held & _settings.StraightModifier) == _settings.StraightModifier))
                {
                    // Keep the straight segment's anchor, replacing the preview as the mouse moves.
                    // Intermediate points keep the falling-star tail smooth along the line.
                    _anchor ??= pts.Count - 1;
                    int a = _anchor.Value;
                    if (pts.Count > a + 1) pts.RemoveRange(a + 1, pts.Count - a - 1);
                    var s0 = pts[a];
                    p = Snap(s0, p);
                    int n = Math.Min(MaxPoints - pts.Count, (int)(Math.Sqrt((p.X - s0.X) * (p.X - s0.X) + (p.Y - s0.Y) * (p.Y - s0.Y)) / 8));
                    for (int k = 1; k < n; k++)
                    {
                        var q = new Point(s0.X + (p.X - s0.X) * k / n, s0.Y + (p.Y - s0.Y) * k / n);
                        if (q != pts[^1]) pts.Add(q);
                    }
                }
                else _anchor = null;
                // Duplicate points make GDI+ throw "Out of memory"; also cap very long strokes.
                if (pts[^1] != p && pts.Count < MaxPoints) pts.Add(p);
                _dirty = true;
                break;
            case Native.WM_MOUSEWHEEL:
                var wheel = Settings.SelectBinding(_settings.ResizeBinding, _settings.ColorBinding, PointerInput.Wheel, held);
                if (wheel == null) return false;
                int step = Math.Sign((short)(mouseData >> 16));
                if (wheel == _settings.ColorBinding) _colorSteps += step; else _wheelSteps += step;
                return true; // swallow: don't scroll/zoom the app underneath
        }
        return false;
    }

    // Magnet snap: the line locks to the nearest snap angle only when its direction is within
    // ±SnapTolerance degrees of it; otherwise the angle is free. Length is preserved.
    Point Snap(Point a, Point b)
    {
        int step = _settings.SnapAngle;
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (step <= 0 || len < 1) return b;
        double unit = step * Math.PI / 180, raw = Math.Atan2(dy, dx), ang = Math.Round(raw / unit) * unit;
        double tol = Math.Min(_settings.SnapTolerance, step / 2.0) * Math.PI / 180;
        if (Math.Abs(raw - ang) > tol) return b;
        return new Point(a.X + (int)Math.Round(len * Math.Cos(ang)), a.Y + (int)Math.Round(len * Math.Sin(ang)));
    }

    void Tick()
    {
        long now = _clock.ElapsedMilliseconds;

        if (_wheelSteps != 0)
        {
            _settings.LineWidth = Math.Clamp(_settings.LineWidth + _wheelSteps, Settings.MinLine, Settings.MaxLine);
            _wheelSteps = 0;
            BuildPens();
            LineWidthChanged?.Invoke(_settings.LineWidth);
            _saveAt = now + SaveDebounceMs;
        }
        if (_colorSteps != 0)
        {
            var pal = Settings.Palette;
            int i = Array.IndexOf(pal, _settings.LineColor & 0xFFFFFF);
            if (i < 0) i = _colorSteps > 0 ? -1 : 0; // not a palette color: start at either end
            i = ((i + _colorSteps) % pal.Length + pal.Length) % pal.Length;
            _colorSteps = 0;
            _settings.LineColor = unchecked((int)0xFF000000) | pal[i];
            BuildPens();
            LineColorChanged?.Invoke(Color.FromArgb(_settings.LineColor));
            _saveAt = now + SaveDebounceMs;
        }
        if (_saveAt >= 0 && now >= _saveAt) { _saveAt = -1; _settings.Save(); }

        var held = HeldModifiers;
        // Capture release modifiers per stroke so applying new bindings cannot strand existing lines.
        if (!_drawing)
            foreach (var s in _strokes)
                if (s.Group && s.Up == null && (held & s.ReleaseModifiers) != s.ReleaseModifiers) s.Up = now;

        foreach (var s in _strokes)
            if (s.Up is long up && now - up > _settings.DelayMs) _dirty = true; // tail animation running
        _strokes.RemoveAll(s => s.Up is long up && now - up >= _settings.DelayMs + TailMs);

        // Laser dot replaces the cursor while the hotkey is held or a stroke is in progress.
        bool active = _settings.DrawBinding.Matches(held) || _settings.StraightBinding.Matches(held) || _drawing;
        Point? dot = active ? new Point(Cursor.Position.X - _screen.X, Cursor.Position.Y - _screen.Y) : null;
        if (dot != _dot) { _dot = dot; _dirty = true; }
        if (active) CursorHider.Hide(); else CursorHider.Restore();

        if (_dirty) Render();
    }

    void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _strokes.Clear();
            _drawing = false;
            DestroySurface();
            _screen = SystemInformation.VirtualScreen;
            Bounds = _screen;
            CreateSurface();
            Render();
        });
    }

    void CreateSurface()
    {
        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = _screen.Width,
            biHeight = -_screen.Height, // top-down
            biPlanes = 1,
            biBitCount = 32
        };
        _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        _dib = Native.CreateDIBSection(_memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        Native.SelectObject(_memDc, _dib);

        // GDI+ draws straight into the DIB memory used by UpdateLayeredWindow.
        _bmp = new Bitmap(_screen.Width, _screen.Height, _screen.Width * 4, PixelFormat.Format32bppPArgb, bits);
        _g = Graphics.FromImage(_bmp);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        _g.InterpolationMode = InterpolationMode.NearestNeighbor;
        _g.PixelOffsetMode = PixelOffsetMode.Half;
        _g.Clear(Color.Transparent);

        // Scratch layer: each layer is drawn opaque here, then blended with its opacity,
        // so overlapping segments don't get darker.
        _layer = new Bitmap(_screen.Width, _screen.Height, PixelFormat.Format32bppPArgb);
        _lg = Graphics.FromImage(_layer);
        _lg.SmoothingMode = SmoothingMode.AntiAlias;

        _lastBox = Rectangle.Empty;
        _fullUpdate = true;
    }

    void DestroySurface()
    {
        _lg?.Dispose(); _lg = null;
        _layer?.Dispose(); _layer = null;
        _g?.Dispose(); _g = null;
        _bmp?.Dispose(); _bmp = null;
        if (_dib != IntPtr.Zero) { Native.DeleteObject(_dib); _dib = IntPtr.Zero; }
        if (_memDc != IntPtr.Zero) { Native.DeleteDC(_memDc); _memDc = IntPtr.Zero; }
    }

    void Render()
    {
        if (_g == null || _lg == null || _layer == null || _linePen == null) return;
        _dirty = false;

        var pieces = BuildPieces(_clock.ElapsedMilliseconds);
        var box = ContentBox(pieces);

        // Only the union of last frame's area and this frame's area needs clearing.
        var dirty = _lastBox.IsEmpty ? box : box.IsEmpty ? _lastBox : Rectangle.Union(_lastBox, box);
        if (dirty.IsEmpty && !_fullUpdate) return;

        if (!dirty.IsEmpty)
        {
            _g.SetClip(dirty);
            _g.Clear(Color.Transparent);
            _g.ResetClip();
        }
        _lastBox = box;

        if (!box.IsEmpty)
        {
            try
            {
                if (_glowPen != null)
                {
                    DrawLayer(box, pieces, _glowPen, cutLine: _settings.LineOpacity < 100);
                    Composite(box, _settings.GlowOpacity);
                }
                DrawLayer(box, pieces, _linePen, cutLine: false);
                Composite(box, _settings.LineOpacity);
            }
            catch (Exception ex) when (ex is OutOfMemoryException or ExternalException or ArgumentException or InvalidOperationException)
            {
                // GDI+ reports bad geometry as "Out of memory". Drop this frame rather than crash.
                _lg.ResetClip();
                _g.SetClip(box);
                _g.Clear(Color.Transparent);
                _g.ResetClip();
            }
        }
        _g.Flush(FlushIntention.Sync);

        Present();
        _fullUpdate = false;
    }

    // Always upload the whole surface: partial (dirty-rect) updates left stale specks on screen.
    void Present()
    {
        var dst = new Native.POINT { X = _screen.X, Y = _screen.Y };
        var src = new Native.POINT();
        var size = new Native.SIZE { cx = _screen.Width, cy = _screen.Height };
        var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
        Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
    }

    // Bounding box of everything to draw this frame (strokes + laser dot), padded for pen width and AA.
    Rectangle ContentBox(List<(PointF[] Pts, float Scale)> pieces)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        void Add(float x, float y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        foreach (var (pts, _) in pieces) foreach (var p in pts) Add(p.X, p.Y);
        if (_dot is Point d) Add(d.X, d.Y);
        if (x0 > x1) return Rectangle.Empty;

        int pad = (int)Math.Ceiling((_glowPen ?? _linePen)!.Width / 2) + 2;
        var r = Rectangle.FromLTRB((int)x0 - pad, (int)y0 - pad, (int)Math.Ceiling(x1) + pad, (int)Math.Ceiling(y1) + pad);
        r.Intersect(new Rectangle(0, 0, _screen.Width, _screen.Height));
        return r.Width > 0 && r.Height > 0 ? r : Rectangle.Empty;
    }

    // Splits strokes into drawable pieces. A fading stroke retracts from its start toward its end,
    // and its trailing part tapers to a point, like a falling star's tail.
    List<(PointF[] Pts, float Scale)> BuildPieces(long now)
    {
        var pieces = new List<(PointF[] Pts, float Scale)>();
        foreach (var s in _strokes)
        {
            int n = s.Pts.Count;
            if (n < 2) continue;
            float progress = s.Up is long up ? Math.Clamp((now - up - _settings.DelayMs) / (float)TailMs, 0f, 1f) : 0f;
            if (progress <= 0)
            {
                pieces.Add((s.Pts.ConvertAll(p => (PointF)p).ToArray(), 1f));
                continue;
            }

            // Fractional start index so the tail moves smoothly.
            float start = progress * (n - 1);
            int i0 = (int)start;
            if (i0 >= n - 1) continue;
            var a = s.Pts[i0]; var b = s.Pts[i0 + 1]; float f = start - i0;
            var vis = new List<PointF>(n - i0) { new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f) };
            for (int i = i0 + 1; i < n; i++)
            {
                PointF q = s.Pts[i];
                if (Math.Abs(q.X - vis[^1].X) + Math.Abs(q.Y - vis[^1].Y) > 0.01f) vis.Add(q);
            }
            if (vis.Count < 2) continue;

            int taper = Math.Min(vis.Count - 1, (int)Math.Ceiling(Math.Max(6, n / 3) * Math.Min(1f, progress * 4)));
            for (int i = 0; i < taper; i++)
                pieces.Add(([vis[i], vis[i + 1]], (i + 1f) / (taper + 1)));
            if (vis.Count - taper >= 2)
                pieces.Add((vis.GetRange(taper, vis.Count - taper).ToArray(), 1f));
        }
        return pieces;
    }

    static Pen Scaled(Pen pen, float scale) =>
        new(pen.Color, Math.Max(0.5f, pen.Width * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    void DrawLayer(Rectangle box, List<(PointF[] Pts, float Scale)> pieces, Pen pen, bool cutLine)
    {
        _lg!.SetClip(box);
        _lg.Clear(Color.Transparent);
        if (cutLine)
        {
            // Exclude the line's area so a see-through line shows the screen, not the glow.
            using var region = new Region();
            region.MakeEmpty();
            foreach (var (pts, scale) in pieces)
            {
                using var path = new GraphicsPath();
                path.AddLines(pts);
                using var lp = Scaled(_linePen!, scale);
                try { path.Widen(lp); region.Union(path); }
                catch (OutOfMemoryException) { /* degenerate segment: nothing to cut */ }
            }
            if (_dot is Point d)
            {
                using var path = new GraphicsPath();
                float w = _linePen!.Width;
                path.AddEllipse(d.X - w / 2, d.Y - w / 2, w, w);
                region.Union(path);
            }
            _lg.SetClip(region, CombineMode.Exclude);
        }
        DrawShape(_lg, pieces, pen);
        _lg.ResetClip();
    }

    void DrawShape(Graphics g, List<(PointF[] Pts, float Scale)> pieces, Pen pen)
    {
        foreach (var (pts, scale) in pieces)
        {
            if (scale >= 1f) { g.DrawLines(pen, pts); continue; }
            using var p = Scaled(pen, scale);
            g.DrawLines(p, pts);
        }
        if (_dot is Point d)
        {
            using var brush = new SolidBrush(pen.Color);
            float w = pen.Width;
            g.FillEllipse(brush, d.X - w / 2, d.Y - w / 2, w, w);
        }
    }

    void Composite(Rectangle box, int opacityPercent)
    {
        // Clip + exact pixel mapping: DrawImage otherwise bleeds 1px past the box, leaving specks
        // outside the area that gets cleared on the next frame.
        _g!.SetClip(box);
        if (opacityPercent >= 100)
            _g.DrawImage(_layer!, box, box, GraphicsUnit.Pixel);
        else
        {
            _attrs.SetColorMatrix(new ColorMatrix { Matrix33 = opacityPercent / 100f });
            _g.DrawImage(_layer!, box, box.X, box.Y, box.Width, box.Height, GraphicsUnit.Pixel, _attrs);
        }
        _g.ResetClip();
    }

    protected override void Dispose(bool disposing)
    {
        if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _timer.Dispose();
            if (_saveAt >= 0) _settings.Save();
            _glowPen?.Dispose();
            _linePen?.Dispose();
            _attrs.Dispose();
        }
        DestroySurface();
        base.Dispose(disposing);
    }
}

static class CursorHider
{
    static readonly uint[] CursorIds =
        { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };
    static bool _hidden;

    public static void Hide()
    {
        if (_hidden) return;
        var and = Enumerable.Repeat((byte)0xFF, 32 * 4).ToArray();
        var xor = new byte[32 * 4];
        var blank = Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor);
        if (blank == IntPtr.Zero) return;
        // SetSystemCursor takes ownership of the handle, so each slot gets its own copy.
        foreach (var id in CursorIds) Native.SetSystemCursor(Native.CopyIcon(blank), id);
        Native.DestroyCursor(blank);
        _hidden = true;
    }

    public static void Restore()
    {
        if (_hidden) ForceRestore();
    }

    public static void ForceRestore()
    {
        Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        _hidden = false;
    }
}

static class Native
{
    public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    public const int WH_MOUSE_LL = 14, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, ULW_ALPHA = 2;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public int mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    public const uint SPI_SETCURSORS = 0x57;
    [DllImport("user32.dll")] public static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);
    [DllImport("user32.dll")] public static extern bool DestroyCursor(IntPtr hCursor);
    [DllImport("user32.dll")] public static extern IntPtr CopyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool SetSystemCursor(IntPtr hcur, uint id);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
