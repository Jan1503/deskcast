namespace DesktopStreamer;

/// <summary>Modal dialog to edit the streaming target and colour parameters.</summary>
public sealed class SettingsForm : Form
{
    private readonly ComboBox _protocol = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _ip = new();
    private readonly ListBox _panelList = new();
    private readonly NumericUpDown _panelX = new() { Minimum = 0, Maximum = 1792, Increment = 256 };
    private readonly NumericUpDown _panelY = new() { Minimum = 0, Maximum = 896, Increment = 128 };
    private readonly List<StreamPanel> _panels = [];
    private bool _loadingPanel;
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535 };
    private readonly NumericUpDown _tpm2Port = new() { Minimum = 1, Maximum = 65535 };
    private readonly NumericUpDown _tpm2Buf = new() { Minimum = 1024, Maximum = 262144, Increment = 1024 };
    private readonly NumericUpDown _fps = new() { Minimum = 1, Maximum = 120 };
    private readonly NumericUpDown _mbps = new() { Minimum = 1, Maximum = 132, Increment = 1, DecimalPlaces = 1 };
    private readonly ComboBox _bits = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _swap = new() { Text = "Swap Red/Blue (send BGR)" };
    private readonly NumericUpDown _gamma = new() { Minimum = 0.10m, Maximum = 4.00m, Increment = 0.05m, DecimalPlaces = 2 };
    private readonly NumericUpDown _contrast = new() { Minimum = 0.10m, Maximum = 3.00m, Increment = 0.05m, DecimalPlaces = 2 };
    private readonly NumericUpDown _brightness = new() { Minimum = 0.10m, Maximum = 4.00m, Increment = 0.05m, DecimalPlaces = 2 };

    private readonly StreamSettings _settings;

    public SettingsForm(StreamSettings s)
    {
        _settings = s;
        Text = "DeskCast settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(360, 620);

        _protocol.Items.AddRange(["PixPlane (direct)", "TPM2 (verpixeld)"]);
        _protocol.SelectedIndex =
            string.Equals(s.Protocol, PanelSenderFactory.ProtocolTpm2, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _bits.Items.AddRange(["8", "10", "13", "14"]);
        _ip.Text = s.Ip;
        _port.Value = Clamp(s.Port, 1, 65535);
        _tpm2Port.Value = Clamp(s.Tpm2Port, 1, 65535);
        _tpm2Buf.Value = Math.Clamp(s.Tpm2BufferSize, 1024, 262144);
        _fps.Value = Clamp(s.Fps, 1, 120);
        _mbps.Value = (decimal)Math.Clamp(s.TargetMbps, 1, 132);
        _bits.SelectedItem = s.ColorBits.ToString();
        if (_bits.SelectedIndex < 0) _bits.SelectedItem = "13";
        _swap.Checked = s.SwapRedBlue;
        _gamma.Value = (decimal)Math.Clamp(s.Gamma, 0.10, 4.00);
        _contrast.Value = (decimal)Math.Clamp(s.Contrast, 0.10, 3.00);
        _brightness.Value = (decimal)Math.Clamp(s.Brightness, 0.10, 4.00);

        var y = 12;
        AddRow("Protocol", _protocol, ref y);
        AddRow("Receiver IP", _ip, ref y);
        AddPanelEditor(s, ref y);
        AddRow("PixPlane port", _port, ref y);
        AddRow("TPM2 port", _tpm2Port, ref y);
        AddRow("TPM2 buffer", _tpm2Buf, ref y);
        AddRow("Target FPS", _fps, ref y);
        AddRow("Pacing (Mbit/s)", _mbps, ref y);
        AddRow("Colour bits", _bits, ref y);

        // Push the matching colour/buffer mode to the panel firmware (it saves + reboots): 8 -> triple
        // buffer (min UDP drops, max fps), 14 -> double buffer (max colour depth). Same brightness.
        var applyPanel = new Button { Text = "Send mode to panel (reboot)" };
        applyPanel.SetBounds(120, y, 220, 24);
        applyPanel.Click += async (_, _) =>
        {
            var bits = int.TryParse(_bits.SelectedItem?.ToString(), out var b) ? b : 14;
            var mode = bits <= 8 ? 8 : 14;
            CommitPanelFields();
            var host = _ip.Text.Trim();
            var prev = applyPanel.Text;
            try
            {
                applyPanel.Enabled = false;
                applyPanel.Text = "Rebooting…";
                await PixPlane.PanelControl.SetColorModeAsync(host, mode);
                MessageBox.Show(this,
                    $"Panel switching to {mode}-bit and rebooting (~5 s).\n" +
                    $"Keep 'Colour bits' = {mode} so the stream matches.",
                    "Panel mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not reach the panel web server at {host}:5000\n\n{ex.Message}",
                    "Panel mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { applyPanel.Enabled = true; applyPanel.Text = prev; }
        };
        Controls.Add(applyPanel);
        y += 30;

        _swap.SetBounds(120, y, 190, 24);
        Controls.Add(_swap);
        y += 30;

        AddRow("Gamma (1.0=off)", _gamma, ref y);
        AddRow("Contrast (1.0=off)", _contrast, ref y);
        AddRow("Brightness (1.0=off)", _brightness, ref y);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        ok.SetBounds(190, y + 6, 75, 26);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(275, y + 6, 75, 26);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>Copies the edited values back into the settings object.</summary>
    public void Apply()
    {
        _settings.Protocol = _protocol.SelectedIndex == 1
            ? PanelSenderFactory.ProtocolTpm2 : PanelSenderFactory.ProtocolRgbPanel;
        CommitPanelFields();
        _settings.Panels = _panels.Select(p => new StreamPanel
        {
            Host = p.Host.Trim(),
            Port = p.Port,
            X = p.X,
            Y = p.Y,
            Enabled = p.Enabled
        }).ToList();
        _settings.EnsurePanels();
        _settings.Port = (int)_port.Value;
        _settings.Tpm2Port = (int)_tpm2Port.Value;
        _settings.Tpm2BufferSize = (int)_tpm2Buf.Value;
        _settings.Fps = (int)_fps.Value;
        _settings.TargetMbps = (double)_mbps.Value;
        _settings.ColorBits = int.TryParse(_bits.SelectedItem?.ToString(), out var b) ? b : 13;
        _settings.SwapRedBlue = _swap.Checked;
        _settings.Gamma = (double)_gamma.Value;
        _settings.Contrast = (double)_contrast.Value;
        _settings.Brightness = (double)_brightness.Value;
    }

    private void AddPanelEditor(StreamSettings s, ref int y)
    {
        s.EnsurePanels();
        _panels.AddRange(s.Panels.Select(p => new StreamPanel
        {
            Host = p.Host, Port = p.Port, X = p.X, Y = p.Y, Enabled = p.Enabled
        }));

        Controls.Add(new Label
        {
            Text = "Panels (each 256×128). Same origin = clone.",
            AutoSize = false,
            Bounds = new Rectangle(12, y, 330, 18)
        });
        y += 20;
        _panelList.SetBounds(12, y, 220, 88);
        _panelList.SelectedIndexChanged += (_, _) => LoadSelectedPanel();
        Controls.Add(_panelList);

        var add = new Button { Text = "Add", Bounds = new Rectangle(240, y, 100, 24) };
        var side = new Button { Text = "Side by side", Bounds = new Rectangle(240, y + 28, 100, 24) };
        var remove = new Button { Text = "Remove", Bounds = new Rectangle(240, y + 56, 100, 24) };
        add.Click += (_, _) =>
        {
            CommitPanelFields();
            var x = _panels.Count == 0 ? 0 : _panels.Max(p => p.X) + StreamSettings.PanelWidth;
            _panels.Add(new StreamPanel { Host = "", Port = (int)_port.Value, X = x, Y = 0, Enabled = true });
            RefreshPanelList(_panels.Count - 1);
        };
        side.Click += (_, _) =>
        {
            CommitPanelFields();
            for (var i = 0; i < _panels.Count; i++)
            {
                _panels[i].X = i * StreamSettings.PanelWidth;
                _panels[i].Y = 0;
            }
            RefreshPanelList(_panelList.SelectedIndex);
        };
        remove.Click += (_, _) =>
        {
            var i = _panelList.SelectedIndex;
            if (i < 0 || _panels.Count <= 1) return;
            _panels.RemoveAt(i);
            RefreshPanelList(Math.Min(i, _panels.Count - 1));
        };
        Controls.Add(add);
        Controls.Add(side);
        Controls.Add(remove);
        y += 94;

        _panelX.ValueChanged += (_, _) => CommitPanelFields();
        _panelY.ValueChanged += (_, _) => CommitPanelFields();
        _ip.TextChanged += (_, _) => CommitPanelFields();
        AddRow("Panel X", _panelX, ref y);
        AddRow("Panel Y", _panelY, ref y);
        RefreshPanelList(0);
    }

    private void RefreshPanelList(int select)
    {
        _loadingPanel = true;
        _panelList.Items.Clear();
        foreach (var p in _panels)
            _panelList.Items.Add($"{p.Host}   {p.X},{p.Y}");
        if (_panels.Count > 0)
            _panelList.SelectedIndex = Math.Clamp(select, 0, _panels.Count - 1);
        _loadingPanel = false;
        LoadSelectedPanel();
    }

    private void LoadSelectedPanel()
    {
        if (_loadingPanel) return;
        var i = _panelList.SelectedIndex;
        if (i < 0 || i >= _panels.Count) return;
        _loadingPanel = true;
        var p = _panels[i];
        _ip.Text = p.Host;
        _panelX.Value = Math.Clamp(p.X, (int)_panelX.Minimum, (int)_panelX.Maximum);
        _panelY.Value = Math.Clamp(p.Y, (int)_panelY.Minimum, (int)_panelY.Maximum);
        _loadingPanel = false;
    }

    private void CommitPanelFields()
    {
        if (_loadingPanel) return;
        var i = _panelList.SelectedIndex;
        if (i < 0 || i >= _panels.Count) return;
        var p = _panels[i];
        p.Host = _ip.Text.Trim();
        p.X = (int)_panelX.Value;
        p.Y = (int)_panelY.Value;
        p.Port = (int)_port.Value;
        _loadingPanel = true;
        _panelList.Items[i] = $"{p.Host}   {p.X},{p.Y}";
        _loadingPanel = false;
    }

    private void AddRow(string label, Control input, ref int y)
    {
        Controls.Add(new Label { Text = label, AutoSize = false, Bounds = new Rectangle(12, y + 3, 100, 20) });
        input.SetBounds(120, y, 220, 24);
        Controls.Add(input);
        y += 30;
    }

    private static decimal Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
}
