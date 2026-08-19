using PixPlane;

namespace DesktopStreamer;

/// <summary>
///     Modeless live editor for the per-column seam correction. Every change is pushed straight to the
///     running sender (PixPlane path) so you can dial in the boundary columns while watching the wall.
/// </summary>
public sealed class SeamForm : Form
{
    private readonly StreamSettings _settings;
    private readonly OverlayForm _overlay;
    private readonly FlowLayoutPanel _rows;
    private readonly Label _hint;

    public SeamForm(StreamSettings settings, OverlayForm overlay)
    {
        _settings = settings;
        _overlay = overlay;

        Text = "Seam correction (live)";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ClientSize = new Size(640, 380);
        MinimumSize = new Size(560, 240);

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            Text = "   X        GainR   GainG   GainB     LiftR    LiftG    LiftB"
        };

        _rows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(6)
        };

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
        var add = new Button { Text = "+ Add column", AutoSize = true };
        var reset = new Button { Text = "Reset defaults", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        add.Click += (_, _) => { AddColumn(new SeamColumn { X = 0, GainR = 1, GainG = 1, GainB = 1 }); ApplyLive(); };
        reset.Click += (_, _) => ResetDefaults();
        close.Click += (_, _) => Close();
        bottom.Controls.AddRange([add, reset, close]);

        _hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };

        Controls.Add(_rows);
        Controls.Add(_hint);
        Controls.Add(bottom);
        Controls.Add(header);

        RebuildRows();
        UpdateHint();
    }

    private void UpdateHint()
    {
        var tpm2 = string.Equals(_settings.Protocol, PanelSenderFactory.ProtocolTpm2, StringComparison.OrdinalIgnoreCase);
        _hint.Text = tpm2
            ? "TPM2 mode: seam correction is applied by the receiver, not here (values are still saved)."
            : "out = in * Gain + Lift. Changes apply instantly to the running stream.";
        _hint.ForeColor = tpm2 ? Color.Firebrick : Color.DimGray;
    }

    private void RebuildRows()
    {
        _rows.SuspendLayout();
        _rows.Controls.Clear();
        _settings.Seam ??= [];
        foreach (var s in _settings.Seam.ToList())
            _rows.Controls.Add(BuildRow(s));
        _rows.ResumeLayout();
    }

    private Control BuildRow(SeamColumn s)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };

        var x = NumBox(s.X, 0, 255, 1, 0, 46);
        x.ValueChanged += (_, _) => { s.X = (int)x.Value; ApplyLive(); };

        var gr = Gain(s.GainR, v => s.GainR = v);
        var gg = Gain(s.GainG, v => s.GainG = v);
        var gb = Gain(s.GainB, v => s.GainB = v);
        var lr = Lift(s.LiftR, v => s.LiftR = v);
        var lg = Lift(s.LiftG, v => s.LiftG = v);
        var lb = Lift(s.LiftB, v => s.LiftB = v);

        var del = new Button { Text = "\u2715", Width = 26, Height = 22, Margin = new Padding(8, 1, 0, 0) };
        del.Click += (_, _) =>
        {
            _settings.Seam?.Remove(s);
            _rows.Controls.Remove(panel);
            panel.Dispose();
            ApplyLive();
        };

        panel.Controls.AddRange([x, gr, gg, gb, lr, lg, lb, del]);
        return panel;
    }

    private NumericUpDown Gain(double value, Action<double> set)
    {
        var n = NumBox((decimal)Math.Clamp(value, 0, 4), 0m, 4m, 0.01m, 2, 62);
        n.ValueChanged += (_, _) => { set((double)n.Value); ApplyLive(); };
        return n;
    }

    private NumericUpDown Lift(double value, Action<double> set)
    {
        var n = NumBox((decimal)Math.Clamp(value, 0, 1), 0m, 1m, 0.001m, 3, 62);
        n.ValueChanged += (_, _) => { set((double)n.Value); ApplyLive(); };
        return n;
    }

    private static NumericUpDown NumBox(decimal value, decimal min, decimal max, decimal step, int dp, int width) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        DecimalPlaces = dp,
        Value = value,
        Width = width,
        Margin = new Padding(2, 1, 2, 1)
    };

    private void AddColumn(SeamColumn s)
    {
        _settings.Seam ??= [];
        _settings.Seam.Add(s);
        _rows.Controls.Add(BuildRow(s));
    }

    private void ResetDefaults()
    {
        _settings.Seam = [];
        foreach (var col in new[] { 63, 127, 191, 255 })
            _settings.Seam.Add(new SeamColumn
            {
                X = col, GainR = 0.85, GainG = 0.85, GainB = 0.85,
                LiftR = 0.004, LiftG = 0.004, LiftB = 0.004
            });
        RebuildRows();
        ApplyLive();
    }

    private void ApplyLive() => _overlay.ApplySeamLive();
}
