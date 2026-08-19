namespace DesktopStreamer;

/// <summary>
///     Teams-style source picker: lists the open windows so the user can choose one to stream (scaled to
///     the panel), or switch back to the free "screen region" (overlay-hole) capture.
/// </summary>
public sealed class WindowPickerForm : Form
{
    private readonly ListBox _list = new()
    {
        IntegralHeight = false,
        Font = new Font("Segoe UI", 9f)
    };
    private readonly IntPtr _exclude;

    /// <summary>Chosen window, or null when the user picked "screen region".</summary>
    public WindowInfo? Selected { get; private set; }

    /// <summary>True when the user chose the free screen-region (overlay) capture instead of a window.</summary>
    public bool RegionMode { get; private set; }

    public WindowPickerForm(IntPtr excludeOwnWindow)
    {
        _exclude = excludeOwnWindow;

        Text = "Choose what to stream";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(460, 420);
        MinimumSize = new Size(360, 300);

        var lbl = new Label
        {
            Text = "Pick a window to stream (scaled to the panel), or use a free screen region:",
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(10, 10, 10, 0)
        };

        _list.Dock = DockStyle.Fill;
        _list.DoubleClick += (_, _) => ChooseWindow();

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(8)
        };
        var stream = new Button { Text = "Stream window", Width = 120, Height = 30 };
        var region = new Button { Text = "Screen region", Width = 110, Height = 30 };
        var refresh = new Button { Text = "Refresh", Width = 80, Height = 30 };
        var cancel = new Button { Text = "Cancel", Width = 80, Height = 30, DialogResult = DialogResult.Cancel };
        stream.Click += (_, _) => ChooseWindow();
        region.Click += (_, _) => { RegionMode = true; Selected = null; DialogResult = DialogResult.OK; };
        refresh.Click += (_, _) => Populate();
        bar.Controls.AddRange([stream, region, refresh, cancel]);

        Controls.Add(_list);
        Controls.Add(bar);
        Controls.Add(lbl);
        AcceptButton = stream;
        CancelButton = cancel;

        Populate();
    }

    private void Populate()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var w in WindowCapture.Enumerate(_exclude))
            _list.Items.Add(w);
        _list.DisplayMember = nameof(WindowInfo.Display);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        _list.EndUpdate();
    }

    private void ChooseWindow()
    {
        if (_list.SelectedItem is not WindowInfo w) return;
        Selected = w;
        RegionMode = false;
        DialogResult = DialogResult.OK;
    }
}
