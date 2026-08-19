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

        return new PixPlaneSender(new PanelStreamOptions
        {
            Host = s.Ip,
            Port = s.Port,
            TargetMbps = s.TargetMbps,
            ColorBits = s.ColorBits,
            WallWidth = StreamSettings.PanelWidth,
            WallHeight = StreamSettings.PanelHeight,
            Color = s.ToColor(),
            Seam = s.Seam ?? []
        });
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
