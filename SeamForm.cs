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
    private readonly FlowLayoutPanel _knots;
    private readonly Label _hint;
    private bool _syncing;
    private List<SeamKnot> _curve = SeamColumn.IdentityKnots();

    public SeamForm(StreamSettings settings, OverlayForm overlay)
    {
        _settings = settings;
        _overlay = overlay;

        Text = "Seam correction (live)";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ClientSize = new Size(700, 500);
        MinimumSize = new Size(640, 360);

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

        var curveBox = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(6, 4, 6, 4) };
        var curveLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 18,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            Text = "Tone curve (8-bit remap, shared by all seam columns). Show a grey window and match the neighbour."
        };
        _knots = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0)
        };
        curveBox.Controls.Add(_knots);
        curveBox.Controls.Add(curveLabel);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
        var add = new Button { Text = "+ Add column", AutoSize = true };
        var reset = new Button { Text = "Reset defaults", AutoSize = true };
        var idCurve = new Button { Text = "Curve identity", AutoSize = true };
        var idGain = new Button { Text = "Gain/lift → 1 / 0", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        add.Click += (_, _) => { AddColumn(new SeamColumn { X = 0, GainR = 1, GainG = 1, GainB = 1, Knots = _curve }); ApplyLive(); };
        reset.Click += (_, _) => ResetDefaults();
        idCurve.Click += (_, _) =>
        {
            _curve = SeamColumn.IdentityKnots();
            RebuildKnots();
            PushCurve();
            ApplyLive();
        };
        idGain.Click += (_, _) =>
        {
            foreach (var s in _settings.Seam ?? [])
            {
                s.GainR = s.GainG = s.GainB = 1;
                s.LiftR = s.LiftG = s.LiftB = 0;
            }
            RebuildRows();
            ApplyLive();
        };
        close.Click += (_, _) => Close();
        bottom.Controls.AddRange([add, reset, idCurve, idGain, close]);

        _hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 32,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };

        Controls.Add(_rows);
        Controls.Add(header);
        Controls.Add(curveBox);
        Controls.Add(_hint);
        Controls.Add(bottom);

        LoadCurveFromSettings();
        RebuildKnots();
        RebuildRows();
        UpdateHint();
    }

    private void UpdateHint()
    {
        var tpm2 = string.Equals(_settings.Protocol, PanelSenderFactory.ProtocolTpm2, StringComparison.OrdinalIgnoreCase);
        _hint.Text = tpm2
            ? "TPM2 mode: seam correction is applied by the receiver, not here (values are still saved)."
            : "8-bit remap (knots) → colour LUT → out = in * Gain + Lift. Changes apply instantly.";
        _hint.ForeColor = tpm2 ? Color.Firebrick : Color.DimGray;
    }

    private void LoadCurveFromSettings()
    {
        var first = _settings.Seam?.FirstOrDefault();
        _curve = first != null ? first.SampleUiKnots() : SeamColumn.IdentityKnots();
    }

    private void RebuildKnots()
    {
        _syncing = true;
        _knots.SuspendLayout();
        _knots.Controls.Clear();
        foreach (var knot in _curve)
        {
            var k = knot;
            var cell = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(4, 0, 4, 0)
            };
            var lab = new Label
            {
                Text = $"in {k.In}",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter
            };
            var n = NumBox(k.Out, 0, 255, 1, 0, 52);
            n.ValueChanged += (_, _) =>
            {
                if (_syncing) return;
                k.Out = (int)n.Value;
                PushCurve();
                ApplyLive();
            };
            cell.Controls.Add(lab);
            cell.Controls.Add(n);
            _knots.Controls.Add(cell);
        }
        _knots.ResumeLayout();
        _syncing = false;
    }

    private void PushCurve()
    {
        _settings.Seam ??= [];
        foreach (var s in _settings.Seam)
            s.Knots = _curve;
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
        _curve = SeamColumn.IdentityKnots();
        _settings.Seam = [];
        foreach (var col in new[] { 63, 127, 191, 255 })
            _settings.Seam.Add(new SeamColumn
            {
                X = col, GainR = 0.85, GainG = 0.85, GainB = 0.85,
                LiftR = 0.004, LiftG = 0.004, LiftB = 0.004,
                Knots = _curve
            });
        RebuildKnots();
        RebuildRows();
        ApplyLive();
    }

    private void ApplyLive()
    {
        PushCurve();
        _overlay.ApplySeamLive();
    }
}
