using System.Text.Json;
using PixPlane;

namespace DesktopStreamer;

/// <summary>Persisted (and CLI-overridable) sender configuration for DeskCast.</summary>
public sealed class StreamSettings
{
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "desktopstreamer.json");

    // Panel geometry is fixed by the firmware protocol.
    public const int PanelWidth = 256;
    public const int PanelHeight = 128;

    /// <summary>Streaming path: "rgbpanel" (direct RP2350 bit-plane) or "tpm2" (legacy verpixeld extension).</summary>
    public string Protocol { get; set; } = PanelSenderFactory.ProtocolRgbPanel;

    public string Ip { get; set; } = "192.168.10.181";
    public int Port { get; set; } = 7777;

    /// <summary>
    ///     PixPlane receivers. Each one is a 256×128 crop at <see cref="StreamPanel.X"/>/<see cref="StreamPanel.Y"/>.
    ///     Empty list falls back to <see cref="Ip"/> at the origin. Same origin on every panel = clone.
    /// </summary>
    public List<StreamPanel> Panels { get; set; } = [];

    /// <summary>UDP port for the legacy TPM2.NET path (verpixeld NetworkStreamPlayer default).</summary>
    public int Tpm2Port { get; set; } = 65506;

    /// <summary>UDP payload budget for the TPM2 path (derives RGB bytes per datagram).</summary>
    public int Tpm2BufferSize { get; set; } = 48000;

    public int Fps { get; set; } = 30;

    /// <summary>Send pacing target in Mbit/s (kept below the MCU drain rate to avoid RX overflow).</summary>
    public double TargetMbps { get; set; } = 19.0;

    /// <summary>Greyscale bit-planes (8/10/13/14). Must match the firmware's HUB_NPLANES / gclk bits.</summary>
    public int ColorBits { get; set; } = 14;

    /// <summary>Send pixels as B,G,R instead of R,G,B (panel colour-order wiring).</summary>
    public bool SwapRedBlue { get; set; } = true;

    /// <summary>Gamma applied per channel (1.0 = off). ~2.2 brightens mid-tones.</summary>
    public double Gamma { get; set; } = 1.0;

    /// <summary>Contrast around mid-grey (1.0 = off, &gt;1 steeper).</summary>
    public double Contrast { get; set; } = 1.0;

    /// <summary>Master brightness multiplier (0..4).</summary>
    public double Brightness { get; set; } = 1.0;

    /// <summary>
    ///     Per-column boundary/seam correction (host-side, in PixPlane). Defaults to the four
    ///     scan-home boundary columns 63/127/191/255 with the tuned gain/lift. Optional Knots
    ///     (9-point 8-bit remap) invert the nonlinear first-line; edit in the Seam window.
    /// </summary>
    public List<SeamColumn> Seam { get; set; } = BuildDefaultSeam();

    /// <summary>Build the live colour pipeline shared by both streaming protocols.</summary>
    public PanelColorSettings ToColor()
    {
        var useGamma = Gamma > 0 && Math.Abs(Gamma - 1.0) > 1e-6;
        return new PanelColorSettings
        {
            Curve = useGamma ? "gamma" : "none",
            Gamma = Gamma,
            Contrast = Contrast,
            Brightness = Brightness,
            SwapRedBlue = SwapRedBlue
        };
    }

    private static List<SeamColumn> BuildDefaultSeam()
    {
        var list = new List<SeamColumn>();
        foreach (var x in new[] { 63, 127, 191, 255 })
            list.Add(new SeamColumn
            {
                X = x, GainR = 0.85, GainG = 0.85, GainB = 0.85,
                LiftR = 0.004, LiftG = 0.004, LiftB = 0.004
            });
        return list;
    }

    /// <summary>Capture source: "region" (the overlay hole grabs the screen area) or "window" (a chosen app window).</summary>
    public string CaptureMode { get; set; } = "region";

    /// <summary>Title of the window to stream in "window" mode (best-effort match to re-find it on launch).</summary>
    public string TargetWindowTitle { get; set; } = "";

    /// <summary>Last overlay window placement, so it reopens where you left it.</summary>
    public int WinX { get; set; } = 200;
    public int WinY { get; set; } = 200;
    public int WinW { get; set; } = 520;
    public int WinH { get; set; } = 300;

    public static StreamSettings Load(string[] args)
    {
        StreamSettings s;
        try
        {
            s = File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<StreamSettings>(File.ReadAllText(ConfigPath)) ?? new StreamSettings()
                : new StreamSettings();
        }
        catch
        {
            s = new StreamSettings();
        }

        // CLI overrides: --ip x --port n --fps n --mbps f --bits n --gamma f
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--protocol" when i + 1 < args.Length: s.Protocol = args[++i].Trim().ToLowerInvariant(); break;
                case "--ip" when i + 1 < args.Length: s.Ip = args[++i]; break;
                case "--tpm2port" when i + 1 < args.Length: s.Tpm2Port = ParseInt(args[++i], s.Tpm2Port); break;
                case "--port" when i + 1 < args.Length: s.Port = ParseInt(args[++i], s.Port); break;
                case "--fps" when i + 1 < args.Length: s.Fps = ParseInt(args[++i], s.Fps); break;
                case "--mbps" when i + 1 < args.Length: s.TargetMbps = ParseDouble(args[++i], s.TargetMbps); break;
                case "--bits" when i + 1 < args.Length: s.ColorBits = ParseInt(args[++i], s.ColorBits); break;
                case "--gamma" when i + 1 < args.Length: s.Gamma = ParseDouble(args[++i], s.Gamma); break;
            }
        }

        s.EnsurePanels();
        return s;
    }

    /// <summary>Make sure at least the legacy single IP exists as panel 1, and mirror it back into <see cref="Ip"/>.</summary>
    public void EnsurePanels()
    {
        Panels ??= [];
        Panels.RemoveAll(p => string.IsNullOrWhiteSpace(p.Host));
        if (Panels.Count == 0)
            Panels.Add(new StreamPanel { Host = Ip, Port = Port > 0 ? Port : 7777, X = 0, Y = 0, Enabled = true });

        foreach (var p in Panels)
        {
            if (p.Port <= 0) p.Port = Port > 0 ? Port : 7777;
            p.X = Math.Max(0, p.X);
            p.Y = Math.Max(0, p.Y);
        }

        var first = Panels.FirstOrDefault(p => p.Enabled) ?? Panels[0];
        Ip = first.Host.Trim();
        Port = first.Port;
    }

    public IReadOnlyList<StreamPanel> ActivePanels()
    {
        EnsurePanels();
        var active = Panels.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Host)).ToList();
        return active.Count > 0 ? active : Panels;
    }

    /// <summary>Virtual wall in pixels. PixPlane = bounding box of the placed panels. TPM2 stays one 256×128 receiver.</summary>
    public (int Width, int Height) WallSize()
    {
        if (string.Equals(Protocol, PanelSenderFactory.ProtocolTpm2, StringComparison.OrdinalIgnoreCase))
            return (PanelWidth, PanelHeight);

        var panels = ActivePanels();
        var w = panels.Max(p => p.X + PanelWidth);
        var h = panels.Max(p => p.Y + PanelHeight);
        return (Math.Clamp(w, PanelWidth, 2048), Math.Clamp(h, PanelHeight, 1024));
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal.
        }
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, out var n) ? n : fallback;

    private static double ParseDouble(string value, double fallback) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;
}

/// <summary>One physical 256×128 panel on the virtual wall.</summary>
public sealed class StreamPanel
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 7777;
    public int X { get; set; }
    public int Y { get; set; }
    public bool Enabled { get; set; } = true;
}
