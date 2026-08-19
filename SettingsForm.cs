namespace DesktopStreamer;

/// <summary>Modal dialog to edit the streaming target and colour parameters.</summary>
public sealed class SettingsForm : Form
{
    private readonly ComboBox _protocol = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _ip = new();
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
        ClientSize = new Size(320, 466);

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
        AddRow("PixPlane port", _port, ref y);
        AddRow("TPM2 port", _tpm2Port, ref y);
        AddRow("TPM2 buffer", _tpm2Buf, ref y);
        AddRow("Target FPS", _fps, ref y);
        AddRow("Pacing (Mbit/s)", _mbps, ref y);
        AddRow("Colour bits", _bits, ref y);

        // Push the matching colour/buffer mode to the panel firmware (it saves + reboots): 8 -> triple
        // buffer (min UDP drops, max fps), 14 -> double buffer (max colour depth). Same brightness.
        var applyPanel = new Button { Text = "Send mode to panel (reboot)" };
        applyPanel.SetBounds(120, y, 190, 24);
        applyPanel.Click += async (_, _) =>
        {
            var bits = int.TryParse(_bits.SelectedItem?.ToString(), out var b) ? b : 14;
            var mode = bits <= 8 ? 8 : 14;
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
        ok.SetBounds(150, y + 6, 75, 26);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(235, y + 6, 75, 26);
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
        _settings.Ip = _ip.Text.Trim();
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

    private void AddRow(string label, Control input, ref int y)
    {
        Controls.Add(new Label { Text = label, AutoSize = false, Bounds = new Rectangle(12, y + 3, 100, 20) });
        input.SetBounds(120, y, 190, 24);
        Controls.Add(input);
        y += 30;
    }

    private static decimal Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
}
