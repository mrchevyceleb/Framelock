using Framelock.Core;
using Framelock.Engine;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Framelock.Ui;

/// <summary>Notification-area icon: red while recording, full control from the menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MainWindow _win;
    private readonly RecorderController _rec;
    private readonly AppSettings _s;
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _idle, _recording, _paused;
    private readonly Forms.ToolStripMenuItem _record, _pause, _replay, _replayToggle;

    public TrayIcon(MainWindow win, RecorderController rec, AppSettings s)
    {
        _win = win;
        _rec = rec;
        _s = s;
        _idle = MakeIcon(Drawing.Color.FromArgb(0x5B, 0x8C, 0xFF), false);
        _recording = MakeIcon(Drawing.Color.FromArgb(0xEF, 0x44, 0x44), true);
        _paused = MakeIcon(Drawing.Color.FromArgb(0xFB, 0xBF, 0x24), true);

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColors()) };
        menu.ForeColor = Drawing.Color.FromArgb(0xE6, 0xE8, 0xEE);
        menu.Items.Add("Show Framelock", null, (_, _) => _win.BringToFront());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _record = new Forms.ToolStripMenuItem("Start recording", null, (_, _) => _ = _rec.ToggleRecordingAsync());
        _pause = new Forms.ToolStripMenuItem("Pause", null, (_, _) => _rec.TogglePause());
        _replay = new Forms.ToolStripMenuItem("Save replay", null, (_, _) => _ = _rec.SaveReplayAsync());
        _replayToggle = new Forms.ToolStripMenuItem("Replay buffer", null, (_, _) => _s.ReplayEnabled = !_s.ReplayEnabled);
        menu.Items.Add(_record);
        menu.Items.Add(_pause);
        menu.Items.Add("Screenshot", null, (_, _) => _ = _rec.TakeScreenshotAsync());
        menu.Items.Add(_replay);
        menu.Items.Add(_replayToggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Select region…", null, (_, _) => _win.PickRegion());
        menu.Items.Add("Open recordings folder", null, (_, _) => MainViewModel.OpenFolder(_s.OutputFolder));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _win.ExitApp());
        menu.Opening += (_, _) => Update();

        _icon = new Forms.NotifyIcon { Icon = _idle, Text = "Framelock", Visible = true, ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _win.BringToFront(); };
        _rec.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RecorderController.Elapsed)) UpdateTooltip();
        };
        Update();
    }

    public void Update()
    {
        bool recording = _rec.IsRecording;
        _icon.Icon = _rec.IsPaused ? _paused : recording ? _recording : _idle;
        _record.Text = recording || _rec.State == RecorderState.Starting ? $"Stop recording\t{_s.HotkeyRecord}" : $"Start recording\t{_s.HotkeyRecord}";
        _pause.Text = (_rec.IsPaused ? "Resume" : "Pause") + $"\t{_s.HotkeyPause}";
        _pause.Enabled = recording;
        _replay.Text = $"Save replay\t{_s.HotkeyReplay}";
        _replay.Enabled = _rec.ReplayActive;
        _replayToggle.Checked = _s.ReplayEnabled;
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        var t = _rec.IsRecording ? $"Framelock · {(_rec.IsPaused ? "paused" : "recording")} {RecorderController.FormatDuration(_rec.Elapsed)}" : "Framelock";
        if (_icon.Text != t) _icon.Text = t.Length > 63 ? t[..63] : t;
    }

    /// <summary>Rounded frame with a dot - drawn at runtime so it's crisp at any tray scaling.</summary>
    private static Drawing.Icon MakeIcon(Drawing.Color dot, bool filled)
    {
        int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width * 2);
        using var bmp = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            float s = size, pad = s * 0.08f, stroke = s * 0.11f, r = s * 0.26f;
            var rect = new Drawing.RectangleF(pad + stroke / 2, pad + stroke / 2, s - 2 * pad - stroke, s - 2 * pad - stroke);
            using var path = RoundedRect(rect, r);
            if (filled) using (var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(60, dot))) g.FillPath(fill, path);
            using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(0xE6, 0xE8, 0xEE), stroke)) g.DrawPath(pen, path);
            float d = s * 0.36f;
            using var b = new Drawing.SolidBrush(dot);
            g.FillEllipse(b, (s - d) / 2, (s - d) / 2, d, d);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private static Drawing.Drawing2D.GraphicsPath RoundedRect(Drawing.RectangleF r, float radius)
    {
        var p = new Drawing.Drawing2D.GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in new[] { _idle, _recording, _paused })
        {
            Native.DestroyIcon(i.Handle);
            i.Dispose();
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(0x1B, 0x1D, 0x23), Hover = Drawing.Color.FromArgb(0x2A, 0x2D, 0x36), Line = Drawing.Color.FromArgb(0x33, 0x36, 0x40);
        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Line;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color CheckBackground => Hover;
        public override Drawing.Color CheckSelectedBackground => Hover;
    }
}
