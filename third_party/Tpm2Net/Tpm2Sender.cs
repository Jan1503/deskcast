using System.Net;
using System.Net.Sockets;

namespace Tpm2Net;

/// <summary>
///     Streams frames to a TPM2.NET / TPM2MAX.NET receiver over UDP. This is the "legacy" path the old
///     DesktopStreamer and the verpixeld <c>NetworkStreamPlayer</c> extension used. Feed a top-down BGRA
///     frame; the sender packs it to 8-bit RGB, fragments it into TPM2 datagrams and paces the send.
///
///     Wire format (per fragment):
///       TPM2.NET    (&lt;=255 packets): [0]=0x9C [1]=0xDA [2..3]=frameLen BE [4]=pkt [5]=nPkts [payload] [0x36]
///       TPM2MAX.NET (&gt;255 packets):  [0]=0x9D [1]=0xDA [2..3]=frameLen BE [4..5]=pkt BE [6..7]=nPkts BE [payload] [0x36]
///     Config query : [0]=0x9C [1]=0xC0 [8]=0x40 [9]=0x10 [10]=0x36  -&gt; reply carries frameSize + W + H.
/// </summary>
public sealed class Tpm2Sender : IDisposable
{
    private const byte TPM2_START = 0x9C;
    private const byte TPM2MAX_START = 0x9D;
    private const byte DATA_FRAME = 0xDA;
    private const byte COMMAND_FRAME = 0xC0;
    private const byte END_MARKER = 0x36;

    public int Width { get; }
    public int Height { get; }
    public int FrameLength { get; }
    public int PacketCount { get; }
    public bool IsTpm2Max { get; }

    /// <summary>Number of frames actually pushed onto the wire (skipped/failed frames don't count).</summary>
    public long FramesSent { get; private set; }

    /// <summary>Last transport error (e.g. "connection reset - no receiver"); null once a send succeeds.</summary>
    public string? LastError { get; private set; }

    /// <summary>True if the receiver answered the auto-config query (i.e. something is actually listening).</summary>
    public bool AutoConfigured { get; private set; }

    private readonly Socket _sock;
    private readonly int _commandLength;
    private readonly int _packetSize;

    private readonly byte[] _rgb;         // W*H*3 source RGB
    private readonly byte[] _send;        // all packets, contiguous
    private readonly byte[] _previous;    // last sent buffer (skip identical frames)
    private bool _havePrevious;

    private byte[] _lutR = new byte[256];
    private byte[] _lutG = new byte[256];
    private byte[] _lutB = new byte[256];
    private volatile bool _swapRedBlue;

    public Tpm2Sender(Tpm2StreamOptions opt)
    {
        ArgumentNullException.ThrowIfNull(opt);
        var wallW = opt.WallWidth > 0 ? opt.WallWidth : 256;
        var wallH = opt.WallHeight > 0 ? opt.WallHeight : 128;

        var host = string.IsNullOrWhiteSpace(opt.Host) ? "127.0.0.1" : opt.Host;
        var port = opt.Port > 0 ? opt.Port : 65506;
        if (!IPAddress.TryParse(host, out var ip))
            ip = Dns.GetHostAddresses(host).FirstOrDefault()
                 ?? throw new ArgumentException($"cannot resolve host '{host}'", nameof(opt));
        var endpoint = new IPEndPoint(ip, port);

        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            SendBufferSize = int.MaxValue
        };
        _sock.Connect(endpoint);

        // Ask the receiver for its frame size AND canvas dimensions. The verpixeld canvas can be any size,
        // so we ADOPT the reported width/height instead of assuming a fixed panel geometry.
        var frameSize = 0;
        var qw = 0;
        var qh = 0;
        if (opt.AutoConfigure)
        {
            (frameSize, qw, qh) = TryQueryConfig(opt.AutoConfigureTimeoutMs);
            AutoConfigured = frameSize > 0;
        }

        Width = qw > 0 ? qw : wallW;
        Height = qh > 0 ? qh : wallH;
        var pixelBytes = Width * Height * 3;

        // Prefer the receiver's advertised frame size (keeps packet counts in lock-step); otherwise derive
        // the largest divisor of the pixel budget that fits our datagram window.
        if (frameSize <= 0 || pixelBytes % frameSize != 0)
            frameSize = MaxFrameLength(pixelBytes, Math.Max(64, opt.SendBufferSize) - 9);
        if (frameSize <= 0) frameSize = pixelBytes; // single packet fallback

        FrameLength = frameSize;
        PacketCount = pixelBytes / frameSize;
        IsTpm2Max = PacketCount > 255;
        _commandLength = IsTpm2Max ? 9 : 7;
        _packetSize = FrameLength + _commandLength;

        _rgb = new byte[pixelBytes];
        _send = new byte[_packetSize * PacketCount];
        _previous = new byte[_send.Length];

        UpdateColor(opt.Color ?? new Tpm2ColorSettings());
    }

    /// <summary>Rebuild the 8-bit colour LUTs from live settings (gamma / contrast / brightness / white balance).</summary>
    public void UpdateColor(Tpm2ColorSettings c)
    {
        ArgumentNullException.ThrowIfNull(c);
        _swapRedBlue = c.SwapRedBlue;
        _lutR = BuildLut(c, c.GainR);
        _lutG = BuildLut(c, c.GainG);
        _lutB = BuildLut(c, c.GainB);
        _havePrevious = false; // colour changed -> force a resend
    }

    private static byte[] BuildLut(Tpm2ColorSettings c, double gain)
    {
        var curve = (c.Curve ?? "none").Trim().ToLowerInvariant();
        var gamma = c.Gamma <= 0 ? 2.2 : c.Gamma;
        var contrast = c.Contrast <= 0 ? 1.0 : c.Contrast;
        var scale = gain * Math.Clamp(c.Brightness <= 0 ? 1.0 : c.Brightness, 0.0, 4.0);
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var y = curve == "gamma" ? Math.Pow(i / 255.0, gamma) : i / 255.0;
            y = (y - 0.5) * contrast + 0.5;
            if (y < 0) y = 0; else if (y > 1) y = 1;
            lut[i] = (byte)Math.Clamp(Math.Round(y * scale * 255.0), 0, 255);
        }
        return lut;
    }

    /// <summary>Pack a top-down BGRA frame (Width x Height, 4 bytes/pixel) and stream it as TPM2 datagrams.</summary>
    public void SendFrameBgra(ReadOnlySpan<byte> bgra, int strideBytes)
    {
        if (strideBytes < Width * 4) throw new ArgumentException("stride too small", nameof(strideBytes));
        if (bgra.Length < strideBytes * (Height - 1) + Width * 4)
            throw new ArgumentException("frame buffer too small", nameof(bgra));

        var lutR = _lutR; var lutG = _lutG; var lutB = _lutB;
        var swap = _swapRedBlue;
        var rgb = _rgb;
        var di = 0;
        for (var y = 0; y < Height; y++)
        {
            var row = y * strideBytes;
            for (var x = 0; x < Width; x++)
            {
                var o = row + x * 4;
                int b = bgra[o];
                int g = bgra[o + 1];
                int r = bgra[o + 2];
                if (swap) (r, b) = (b, r);
                rgb[di++] = lutR[r];
                rgb[di++] = lutG[g];
                rgb[di++] = lutB[b];
            }
        }

        BuildPackets(rgb);
        if (_havePrevious && _send.AsSpan().SequenceEqual(_previous))
            return;

        for (var packet = 0; packet < PacketCount; packet++)
        {
            try { _sock.Send(_send, packet * _packetSize, _packetSize, SocketFlags.None); }
            catch (SocketException ex)
            {
                _havePrevious = false;
                LastError = ex.SocketErrorCode == SocketError.ConnectionReset
                    ? "connection reset (no TPM2 receiver at this IP:port?)"
                    : $"socket: {ex.SocketErrorCode}";
                return;
            }
            if (packet < PacketCount - 1)
                Thread.Sleep(0); // yield without a full context switch to avoid RX drops
        }

        Buffer.BlockCopy(_send, 0, _previous, 0, _send.Length);
        _havePrevious = true;
        LastError = null;
        FramesSent++;
    }

    private void BuildPackets(byte[] rgb)
    {
        var start = IsTpm2Max ? TPM2MAX_START : TPM2_START;
        var flHi = (byte)(FrameLength >> 8);
        var flLo = (byte)FrameLength;
        var pcHi = (byte)(PacketCount >> 8);
        var pcLo = (byte)PacketCount;

        for (var packet = 1; packet <= PacketCount; packet++)
        {
            var pi = (packet - 1) * _packetSize;
            var src = (packet - 1) * FrameLength;

            _send[pi] = start;
            _send[pi + 1] = DATA_FRAME;
            _send[pi + 2] = flHi;
            _send[pi + 3] = flLo;
            if (IsTpm2Max)
            {
                _send[pi + 4] = (byte)(packet >> 8);
                _send[pi + 5] = (byte)packet;
                _send[pi + 6] = pcHi;
                _send[pi + 7] = pcLo;
            }
            else
            {
                _send[pi + 4] = (byte)packet;
                _send[pi + 5] = (byte)PacketCount;
            }

            Buffer.BlockCopy(rgb, src, _send, pi + _commandLength - 1, FrameLength);
            _send[pi + _packetSize - 1] = END_MARKER;
        }
    }

    // Returns (frameSize, width, height) from the receiver's config reply, or (0,0,0) if nobody answered.
    private (int frameSize, int width, int height) TryQueryConfig(int timeoutMs)
    {
        var query = new byte[11];
        query[0] = TPM2_START;
        query[1] = COMMAND_FRAME;
        query[8] = 0x40;
        query[9] = 0x10;
        query[10] = END_MARKER;

        var perTry = Math.Max(150, timeoutMs / 3);
        var prev = _sock.ReceiveTimeout;
        _sock.ReceiveTimeout = perTry;
        try
        {
            // The query datagram itself can be lost; retry a few times before giving up.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    _sock.Send(query);
                    var reply = new byte[16];
                    var n = _sock.Receive(reply);
                    if (n >= 12 && reply[n - 1] == END_MARKER)
                    {
                        var fs = (reply[2] << 8) | reply[3];
                        var w = (reply[7] << 8) | reply[8];
                        var h = (reply[9] << 8) | reply[10];
                        return (fs, w, h);
                    }
                }
                catch (SocketException) { /* timeout / reset -> retry or fall back */ }
            }
        }
        finally { _sock.ReceiveTimeout = prev; }
        return (0, 0, 0);
    }

    private static int MaxFrameLength(int pixelCount, int maxFrameLength)
    {
        for (var i = Math.Min(maxFrameLength, pixelCount); i >= 1; i--)
            if (pixelCount % i == 0)
                return i;
        return 0;
    }

    public void Dispose() => _sock.Dispose();
}
