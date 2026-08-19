namespace Tpm2Net;

/// <summary>
///     Live-adjustable colour pipeline for the TPM2 sender. Applied per pixel as an 8-bit-in / 8-bit-out
///     lookup table (the TPM2 wire format is 8-bit RGB), so it can be rebuilt at runtime.
/// </summary>
public sealed class Tpm2ColorSettings
{
    /// <summary>Tone curve: "none" (linear) or "gamma" (power law).</summary>
    public string Curve { get; set; } = "none";

    /// <summary>Gamma exponent for Curve == "gamma".</summary>
    public double Gamma { get; set; } = 2.2;

    /// <summary>Contrast around mid-grey: 1.0 = unchanged, &gt;1 steeper, &lt;1 flatter.</summary>
    public double Contrast { get; set; } = 1.0;

    /// <summary>Master brightness multiplier (0..4).</summary>
    public double Brightness { get; set; } = 1.0;

    /// <summary>Per-channel white-balance gain.</summary>
    public double GainR { get; set; } = 1.0;
    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;

    /// <summary>Swap R and B (panel colour-order wiring).</summary>
    public bool SwapRedBlue { get; set; }

    public Tpm2ColorSettings Clone() => (Tpm2ColorSettings)MemberwiseClone();
}

/// <summary>Everything needed to open a TPM2.NET stream. Colour can also be changed live afterwards.</summary>
public sealed class Tpm2StreamOptions
{
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Default TPM2.NET port used by the verpixeld NetworkStreamPlayer extension.</summary>
    public int Port { get; set; } = 65506;

    /// <summary>Frame geometry sent on the wire (must match the receiving canvas).</summary>
    public int WallWidth { get; set; } = 256;
    public int WallHeight { get; set; } = 128;

    /// <summary>
    ///     UDP payload budget used to derive the RGB bytes per datagram (frameSize). The receiver uses the
    ///     same idea (BufferSize - header); the largest divisor of W*H*3 that fits is chosen.
    /// </summary>
    public int SendBufferSize { get; set; } = 48000;

    /// <summary>
    ///     Query the receiver (0xC0 command) for its frame size / dimensions before streaming. Falls back to
    ///     the local geometry + <see cref="SendBufferSize"/> if the receiver does not answer in time.
    /// </summary>
    public bool AutoConfigure { get; set; } = true;

    public int AutoConfigureTimeoutMs { get; set; } = 800;

    public Tpm2ColorSettings Color { get; set; } = new();
}
