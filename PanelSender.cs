using PixPlane;
using Tpm2Net;

namespace DesktopStreamer;

/// <summary>
///     Protocol-agnostic frame sink. DeskCast can stream over the direct PixPlane bit-plane
///     protocol (RP2350 + W6300) or the legacy TPM2.NET protocol (verpixeld NetworkStreamPlayer). Both
///     take a top-down BGRA frame so the capture path stays identical.
/// </summary>
public interface IPanelSender : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Per-column seam correction only means something for the direct panel protocol.</summary>
    bool SupportsSeam { get; }

    /// <summary>Optional transport diagnostic to surface in the UI (null = fine).</summary>
    string? Diagnostics { get; }

    void SendFrameBgra(ReadOnlySpan<byte> bgra, int strideBytes);
    void UpdateColor(PanelColorSettings color);
    void UpdateSeam(IReadOnlyList<SeamColumn> seam);
}

/// <summary>Direct RP2350 + W6300 bit-plane streamer (PixPlane).</summary>
public sealed class PixPlaneSender : IPanelSender
{
    private readonly PanelStreamer _inner;

    public PixPlaneSender(PanelStreamOptions opt) => _inner = new PanelStreamer(opt);

    public int Width => _inner.Width;
    public int Height => _inner.Height;
    public bool SupportsSeam => true;
    public string? Diagnostics => null;

    public void SendFrameBgra(ReadOnlySpan<byte> bgra, int strideBytes) => _inner.SendFrameBgra(bgra, strideBytes);
    public void UpdateColor(PanelColorSettings color) => _inner.UpdateColor(color);
    public void UpdateSeam(IReadOnlyList<SeamColumn> seam) => _inner.UpdateSeam(seam);
    public void Dispose() => _inner.Dispose();
}

/// <summary>
///     One PixPlane streamer per physical panel. The capture is the virtual wall; each panel
///     receives its own 256×128 crop. Same origin on every panel clones the top-left tile.
/// </summary>
public sealed class PixPlaneWallSender : IPanelSender
{
    private const int PanelW = StreamSettings.PanelWidth;
    private const int PanelH = StreamSettings.PanelHeight;
    private readonly Slot[] _slots;

    public PixPlaneWallSender(StreamSettings s)
    {
        var (w, h) = s.WallSize();
        Width = w;
        Height = h;
        var color = s.ToColor();
        var seam = s.Seam ?? [];
        _slots = s.ActivePanels().Select(p => new Slot(
            new PanelStreamer(new PanelStreamOptions
            {
                Host = p.Host,
                Port = p.Port > 0 ? p.Port : s.Port,
                TargetMbps = s.TargetMbps,
                ColorBits = s.ColorBits,
                WallWidth = PanelW,
                WallHeight = PanelH,
                Color = color,
                Seam = seam
            }), p.X, p.Y)).ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public bool SupportsSeam => true;
    public string? Diagnostics => null;

    public void SendFrameBgra(ReadOnlySpan<byte> bgra, int strideBytes)
    {
        if (_slots.Length == 1 && _slots[0].X == 0 && _slots[0].Y == 0
            && Width == PanelW && Height == PanelH)
        {
            _slots[0].Streamer.SendFrameBgra(bgra, strideBytes);
            return;
        }

        for (var i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            Crop(bgra, strideBytes, Width, Height, slot.X, slot.Y, slot.Crop);
            slot.Streamer.SendFrameBgra(slot.Crop, PanelW * 4);
        }
    }

    public void UpdateColor(PanelColorSettings color)
    {
        foreach (var slot in _slots) slot.Streamer.UpdateColor(color);
    }

    public void UpdateSeam(IReadOnlyList<SeamColumn> seam)
    {
        foreach (var slot in _slots) slot.Streamer.UpdateSeam(seam);
    }

    public void Dispose()
    {
        foreach (var slot in _slots) slot.Streamer.Dispose();
    }

    private static void Crop(ReadOnlySpan<byte> src, int stride, int frameW, int frameH,
        int originX, int originY, byte[] dst)
    {
        dst.AsSpan().Clear();
        var rowBytes = PanelW * 4;
        for (var y = 0; y < PanelH; y++)
        {
            var sy = originY + y;
            if ((uint)sy >= (uint)frameH) continue;
            var sx = originX;
            var dx = 0;
            var copy = PanelW;
            if (sx < 0)
            {
                dx = -sx;
                copy -= dx;
                sx = 0;
            }

            if (sx >= frameW || copy <= 0) continue;
            if (sx + copy > frameW) copy = frameW - sx;
            src.Slice(sy * stride + sx * 4, copy * 4).CopyTo(dst.AsSpan(y * rowBytes + dx * 4, copy * 4));
        }
    }

    private sealed class Slot(PanelStreamer streamer, int x, int y)
    {
        public PanelStreamer Streamer { get; } = streamer;
        public int X { get; } = x;
        public int Y { get; } = y;
        public byte[] Crop { get; } = new byte[PanelW * PanelH * 4];
    }
}

/// <summary>Legacy TPM2.NET streamer (verpixeld NetworkStreamPlayer / any TPM2 receiver).</summary>
public sealed class Tpm2PanelSender : IPanelSender
{
    private readonly Tpm2Sender _inner;

    public Tpm2PanelSender(Tpm2StreamOptions opt) => _inner = new Tpm2Sender(opt);

    public int Width => _inner.Width;
    public int Height => _inner.Height;
    public bool SupportsSeam => false;

    // Autoconfig is the reliable reachability signal: UDP sends to a dead host usually DON'T throw, so
    // "frames sent, no error" is misleading. If the receiver never answered the config query, it isn't there.
    public string? Diagnostics => _inner.LastError
        ?? (!_inner.AutoConfigured
            ? "TPM2 receiver did not answer (autoconfig). Check the IP/port and that the 'Network Stream Player' extension is added & running."
            : null);

    public void SendFrameBgra(ReadOnlySpan<byte> bgra, int strideBytes) => _inner.SendFrameBgra(bgra, strideBytes);
    public void UpdateColor(PanelColorSettings color) => _inner.UpdateColor(PanelSenderFactory.ToTpm2(color));
    public void UpdateSeam(IReadOnlyList<SeamColumn> seam) { /* TPM2 has no per-column seam concept */ }
    public void Dispose() => _inner.Dispose();
}

/// <summary>Builds the sender selected in the settings.</summary>
public static class PanelSenderFactory
{
    public const string ProtocolRgbPanel = "rgbpanel";
    public const string ProtocolTpm2 = "tpm2";

    public static IPanelSender Create(StreamSettings s)
    {
        if (string.Equals(s.Protocol, ProtocolTpm2, StringComparison.OrdinalIgnoreCase))
        {
            return new Tpm2PanelSender(new Tpm2StreamOptions
            {
                Host = s.Ip,
                Port = s.Tpm2Port,
                WallWidth = StreamSettings.PanelWidth,
                WallHeight = StreamSettings.PanelHeight,
                SendBufferSize = s.Tpm2BufferSize,
                AutoConfigure = true,
                Color = ToTpm2(s.ToColor())
            });
        }

        return new PixPlaneWallSender(s);
    }

    internal static Tpm2ColorSettings ToTpm2(PanelColorSettings c) => new()
    {
        Curve = c.Curve,
        Gamma = c.Gamma,
        Contrast = c.Contrast,
        Brightness = c.Brightness,
        GainR = c.GainR,
        GainG = c.GainG,
        GainB = c.GainB,
        SwapRedBlue = c.SwapRedBlue
    };
}
