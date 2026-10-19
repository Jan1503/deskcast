using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PixPlane;

namespace DesktopStreamer;

/// <summary>
///     A borderless, always-on-top, movable/resizable "capture frame". The hollow center is a
///     transparent, click-through hole: whatever desktop shows through it is captured, downscaled to
///     the 256x128 panel and streamed straight to the RP2350 + W6300 receiver via <see cref="PanelStreamer"/>.
/// </summary>
public sealed class OverlayForm : Form
{
    private const int Border = 3;
    private const int Header = 26;
    private static readonly Color KeyColor = Color.Magenta; // transparent + click-through hole

    private readonly StreamSettings _settings;
    private readonly Button _btnSettings;
    private readonly Button _btnSeam;
    private readonly Button _btnClose;
    private readonly Button _btn1To1;
    private readonly Button _btnSource;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private readonly object _sync = new();
    private IPanelSender? _sender;
    private SeamForm? _seamForm;
    private string? _lastShownDiag;
    private Rectangle _captureRect; // screen coords of the hole
    private byte[]? _bgra;
    private int _stride;
    private volatile IntPtr _targetWindow = IntPtr.Zero; // non-zero => stream this window instead of the hole

    private Thread? _captureThread;
    private volatile bool _running;
    private volatile string _statusLine = "starting…";
    private volatile bool _ok;
    private long _frames;

    public OverlayForm(StreamSettings settings)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        MinimumSize = new Size(120, Header + 20);
        Text = "DeskCast";
        DoubleBuffered = true;

        _btn1To1 = MakeHeaderButton("1:1");
        _btn1To1.Size = new Size(30, 20);
        _btnSource = MakeHeaderButton("\u25A4"); // window/source picker
        _btnSeam = MakeHeaderButton("\u25A6"); // seam/grid glyph
        _btnSettings = MakeHeaderButton("\u2699"); // gear
        _btnClose = MakeHeaderButton("\u2715");     // x
        _btn1To1.Click += (_, _) => SizeToPanel(SenderWidth(), SenderHeight());
        _btnSource.Click += (_, _) => OpenWindowPicker();
        _btnSeam.Click += (_, _) => OpenSeamEditor();
        _btnSettings.Click += (_, _) => OpenSettings();
        _btnClose.Click += (_, _) => Close();
        _btnSource.Tag = "Choose window / screen region to stream";
        Controls.Add(_btn1To1);
        Controls.Add(_btnSource);
        Controls.Add(_btnSeam);
        Controls.Add(_btnSettings);
        Controls.Add(_btnClose);

        _uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _uiTimer.Tick += (_, _) => Invalidate(new Rectangle(0, 0, ClientSize.Width, Header));
        _uiTimer.Start();

        Bounds = new Rectangle(settings.WinX, settings.WinY, settings.WinW, settings.WinH);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED (reduce flicker)
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wall = _settings.WallSize();
        SizeToPanel(wall.Width, wall.Height);
        LayoutHeaderButtons();
        UpdateCaptureRect();
        _running = true;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "capture" };
        _captureThread.Start();
        RebuildSender();

        // Restore a previously chosen window (best-effort by title) so it reconnects on launch.
        if (string.Equals(_settings.CaptureMode, "window", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_settings.TargetWindowTitle))
        {
            _targetWindow = WindowCapture.FindByTitle(_settings.TargetWindowTitle, Handle);
            if (_targetWindow != IntPtr.Zero) _statusLine = "\u25A4 " + _settings.TargetWindowTitle;
        }
    }

    /// <summary>Size the window so the capture hole is exactly <paramref name="w"/>x<paramref name="h"/> pixels.</summary>
    private void SizeToPanel(int w, int h)
    {
        if (w < 1 || h < 1) return;
        SetBounds(Left, Top, w + 2 * Border, h + Header + Border);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutHeaderButtons();
        UpdateCaptureRect();
        Invalidate();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        UpdateCaptureRect();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        var accent = _ok ? Color.FromArgb(60, 200, 90) : Color.FromArgb(220, 140, 40);

        using (var headerBrush = new SolidBrush(Color.FromArgb(28, 28, 32)))
            g.FillRectangle(headerBrush, 0, 0, w, Header);

        using (var b = new SolidBrush(accent))
        {
            g.FillRectangle(b, 0, Header, Border, h - Header);           // left
            g.FillRectangle(b, w - Border, Header, Border, h - Header);  // right
            g.FillRectangle(b, 0, h - Border, w, Border);                // bottom
            g.FillRectangle(b, 0, Header - 1, w, 1);                     // header underline
        }

        var wall = _settings.WallSize();
        var panels = _settings.ActivePanels().Count;
        var text = $"{panels} panel{(panels == 1 ? "" : "s")} {wall.Width}×{wall.Height}   {_statusLine}   {Interlocked.Read(ref _frames)}f";
        using var f = new Font("Segoe UI", 8f);
        using var tb = new SolidBrush(Color.Gainsboro);
        g.DrawString(text, f, tb, 6, 5);
    }

    // ---- capture ---------------------------------------------------------------------------

    private void UpdateCaptureRect()
    {
        var hole = new Rectangle(Border, Header,
            Math.Max(0, ClientSize.Width - 2 * Border),
            Math.Max(0, ClientSize.Height - Header - Border));
        var screen = RectangleToScreen(hole);
        lock (_sync) _captureRect = screen;
    }

    private void CaptureLoop()
    {
        while (_running)
        {
            var start = Environment.TickCount64;

            IPanelSender? sender;
            Rectangle rect;
            int fps;
            lock (_sync)
            {
                sender = _sender;
                rect = _captureRect;
                fps = _settings.Fps;
            }

            var target = _targetWindow;
            if (sender != null && (target != IntPtr.Zero || (rect.Width >= 2 && rect.Height >= 2)))
            {
                try
                {
                    var sent = target != IntPtr.Zero
                        ? CaptureWindowAndSend(sender, target)
                        : CaptureAndSend(sender, rect);
                    if (sent)
                    {
                        Interlocked.Increment(ref _frames);
                        var diag = sender.Diagnostics;
                        if (diag != null) { _ok = false; _statusLine = diag; MaybeShowDiag(diag); }
                        else _ok = true;
                    }
                }
                catch (Exception ex)
                {
                    _ok = false;
                    _statusLine = "send error: " + ex.Message;
                }
            }

            var period = Math.Max(1, 1000 / Math.Max(1, fps));
            var elapsed = (int)(Environment.TickCount64 - start);
            var sleep = period - elapsed;
            if (sleep > 0) Thread.Sleep(sleep);
        }
    }

    // Region mode: grab the desktop area under the overlay hole.
    private bool CaptureAndSend(IPanelSender sender, Rectangle rect)
    {
        using var src = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var gc = Graphics.FromImage(src))
            gc.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        return ScaleAndSend(sender, src);
    }

    // Window mode: grab a chosen app window off-screen (works when occluded), even if it moved.
    private bool CaptureWindowAndSend(IPanelSender sender, IntPtr hWnd)
    {
        using var src = WindowCapture.Capture(hWnd);
        if (src == null)
        {
            _ok = false;
            _statusLine = "window unavailable (closed/minimised?)";
            return false;
        }
        return ScaleAndSend(sender, src);
    }

    // Downscale a source frame to the sender's ACTUAL output size (PixPlane = 256x128; TPM2 = negotiated
    // canvas) and stream it. The 32bpp bits are copied top-down BGRA into a reusable buffer for the DLL.
    private bool ScaleAndSend(IPanelSender sender, Bitmap src)
    {
        int w = sender.Width, h = sender.Height;
        if (w < 1 || h < 1) return false;

        using var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var gd = Graphics.FromImage(dst))
        {
            gd.InterpolationMode = InterpolationMode.HighQualityBilinear;
            gd.PixelOffsetMode = PixelOffsetMode.Half;
            gd.DrawImage(src, new Rectangle(0, 0, w, h));
        }

        var data = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var need = data.Stride * h;
            if (_bgra is null || _bgra.Length != need) _bgra = new byte[need];
            Marshal.Copy(data.Scan0, _bgra, 0, need);
            _stride = data.Stride;
        }
        finally { dst.UnlockBits(data); }

        sender.SendFrameBgra(_bgra, _stride);
        return true;
    }

    private void OpenWindowPicker()
    {
        using var dlg = new WindowPickerForm(Handle);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (dlg.RegionMode)
        {
            _targetWindow = IntPtr.Zero;
            _settings.CaptureMode = "region";
            _settings.TargetWindowTitle = "";
            _statusLine = "screen region";
        }
        else if (dlg.Selected != null)
        {
            _targetWindow = dlg.Selected.Handle;
            _settings.CaptureMode = "window";
            _settings.TargetWindowTitle = dlg.Selected.Title;
            _statusLine = "\u25A4 " + dlg.Selected.Title;
        }

        _settings.Save();
        Invalidate(new Rectangle(0, 0, ClientSize.Width, Header));
    }

    // ---- sender lifecycle ------------------------------------------------------------------

    private int SenderWidth() { lock (_sync) return _sender?.Width ?? StreamSettings.PanelWidth; }
    private int SenderHeight() { lock (_sync) return _sender?.Height ?? StreamSettings.PanelHeight; }

    // Show a persistent transport problem once (per unique message) as an unmissable dialog, since the
    // slim overlay header is easy to overlook.
    private void MaybeShowDiag(string diag)
    {
        if (diag == _lastShownDiag) return;
        _lastShownDiag = diag;
        if (IsDisposed || !IsHandleCreated) return;
        var isTpm2 = string.Equals(_settings.Protocol, PanelSenderFactory.ProtocolTpm2, StringComparison.OrdinalIgnoreCase);
        var port = isTpm2 ? _settings.Tpm2Port : _settings.Port;
        var msg = $"Streaming problem ({_settings.Protocol}) → {_settings.Ip}:{port}\n\n{diag}";
        if (isTpm2)
            msg += "\n\nTPM2 checklist:\n" +
                   "• Receiver IP must be the verpixeld host, NOT the panel (192.168.10.181).\n" +
                   "• In verpixeld, add the \"Network Stream Player\" extension to a canvas so it listens on the port.\n" +
                   "• If it still shows nothing: set that extension's \"Buffer Size\" to ~1500 (MTU-sized packets).";
        try
        {
            BeginInvoke(new Action(() =>
                MessageBox.Show(this, msg, "DeskCast", MessageBoxButtons.OK, MessageBoxIcon.Warning)));
        }
        catch { /* form closing */ }
    }

    private void RebuildSender()
    {
        _ok = false;
        _statusLine = "connecting…";
        _lastShownDiag = null;
        Invalidate(new Rectangle(0, 0, ClientSize.Width, Header));

        var isTpm2 = string.Equals(_settings.Protocol, PanelSenderFactory.ProtocolTpm2, StringComparison.OrdinalIgnoreCase);
        Task.Run(() =>
        {
            IPanelSender? old;
            lock (_sync) { old = _sender; _sender = null; }
            old?.Dispose();

            try
            {
                var created = PanelSenderFactory.Create(_settings);
                lock (_sync) _sender = created;
                _statusLine = isTpm2
                    ? $"TPM2 -> :{_settings.Tpm2Port}  {created.Width}x{created.Height}"
                    : $"PixPlane {created.Width}x{created.Height} {_settings.ColorBits}-bit @ {_settings.TargetMbps:0.#}Mbit/s";
            }
            catch (Exception ex)
            {
                _statusLine = "error: " + ex.Message;
            }
        });
    }

    /// <summary>Push the current seam list to the live sender (PixPlane path only) without a reconnect.</summary>
    public void ApplySeamLive()
    {
        lock (_sync)
        {
            if (_sender is { SupportsSeam: true })
                _sender.UpdateSeam(_settings.Seam ?? []);
        }
        _settings.Save();
    }

    private void OpenSeamEditor()
    {
        if (_seamForm is { IsDisposed: false })
        {
            _seamForm.Activate();
            return;
        }
        _seamForm = new SeamForm(_settings, this);
        _seamForm.FormClosed += (_, _) => _seamForm = null;
        _seamForm.Show(this);
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        dlg.Apply();
        _settings.Save();
        var wall = _settings.WallSize();
        SizeToPanel(wall.Width, wall.Height);
        RebuildSender();
    }

    // ---- window chrome (drag / resize on a borderless form) --------------------------------

    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1, HtCaption = 2, HtLeft = 10, HtRight = 11, HtTop = 12,
        HtTopLeft = 13, HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            var lp = m.LParam.ToInt32();
            var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            var p = PointToClient(screen);
            m.Result = (IntPtr)HitTest(p);
            return;
        }

        base.WndProc(ref m);
    }

    private int HitTest(Point p)
    {
        const int grip = 6;
        var w = ClientSize.Width;
        var h = ClientSize.Height;

        var left = p.X < grip;
        var right = p.X > w - grip;
        var top = p.Y < grip;
        var bottom = p.Y > h - grip;

        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        if (p.Y < Header) return HtCaption; // drag by header
        return HtTransparent;               // click-through hole
    }

    private static Button MakeHeaderButton(string glyph) => new()
    {
        Text = glyph,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(45, 45, 52),
        ForeColor = Color.Gainsboro,
        Font = new Font("Segoe UI", 9f),
        Size = new Size(24, 20),
        TabStop = false
    };

    private void LayoutHeaderButtons()
    {
        if (_btnClose is null || _btnSettings is null || _btnSeam is null || _btn1To1 is null || _btnSource is null) return;

        var top = (Header - _btnClose.Height) / 2;
        _btnClose.Location = new Point(ClientSize.Width - _btnClose.Width - 3, top);
        _btnSettings.Location = new Point(_btnClose.Left - _btnSettings.Width - 4, top);
        _btnSeam.Location = new Point(_btnSettings.Left - _btnSeam.Width - 4, top);
        _btnSource.Location = new Point(_btnSeam.Left - _btnSource.Width - 4, top);
        _btn1To1.Location = new Point(_btnSource.Left - _btn1To1.Width - 4, top);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _running = false;
        _captureThread?.Join(500);
        if (_seamForm is { IsDisposed: false }) _seamForm.Close();
        lock (_sync)
        {
            _sender?.Dispose();
            _sender = null;
        }

        _settings.WinX = Bounds.X;
        _settings.WinY = Bounds.Y;
        _settings.WinW = Bounds.Width;
        _settings.WinH = Bounds.Height;
        _settings.Save();

        base.OnFormClosing(e);
    }
}
