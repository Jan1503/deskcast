using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopStreamer;

/// <summary>A capturable top-level window (for the picker).</summary>
public sealed record WindowInfo(IntPtr Handle, string Title, string Process)
{
    public string Display => string.IsNullOrEmpty(Process) ? Title : $"{Title}   —   {Process}";
}

/// <summary>
///     Win32 window enumeration + off-screen window capture. Uses PrintWindow with
///     PW_RENDERFULLCONTENT (2) so it grabs the window's pixels even when it is occluded or in the
///     background (works for most apps; some GPU/DRM-protected windows may render black). The capture
///     is cropped to the client area so title bars / borders are dropped.
/// </summary>
public static class WindowCapture
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private const uint PW_RENDERFULLCONTENT = 2;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;

    /// <summary>List the visible, alt-tab-like top-level windows (excluding <paramref name="exclude"/>).</summary>
    public static List<WindowInfo> Enumerate(IntPtr exclude)
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (h == exclude) return true;
            if (!IsWindowVisible(h) || IsIconic(h)) return true;

            if (GetWindowTextLength(h) == 0) return true;

            // Skip tool windows (palettes, tooltips).
            var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;

            // Skip cloaked windows (UWP "ghosts" that are technically visible but not shown).
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            if (!GetWindowRect(h, out var r)) return true;
            if (r.R - r.L < 40 || r.B - r.T < 40) return true;

            var sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            var proc = "";
            try { GetWindowThreadProcessId(h, out var pid); proc = Process.GetProcessById((int)pid).ProcessName; }
            catch { /* process gone / access denied */ }

            list.Add(new WindowInfo(h, title, proc));
            return true;
        }, IntPtr.Zero);

        return list.OrderBy(w => w.Process, StringComparer.OrdinalIgnoreCase)
                   .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>Find a still-open window by exact title (best effort, for restoring the saved selection).</summary>
    public static IntPtr FindByTitle(string title, IntPtr exclude)
    {
        if (string.IsNullOrWhiteSpace(title)) return IntPtr.Zero;
        foreach (var w in Enumerate(exclude))
            if (string.Equals(w.Title, title, StringComparison.Ordinal))
                return w.Handle;
        return IntPtr.Zero;
    }

    public static bool IsAlive(IntPtr h) => h != IntPtr.Zero && IsWindow(h) && IsWindowVisible(h) && !IsIconic(h);

    /// <summary>
    ///     Capture a window's pixels off-screen, cropped to its client area. Returns null if it could not
    ///     be captured (e.g. window gone / minimised / size invalid). The caller owns/Disposes the bitmap.
    /// </summary>
    public static Bitmap? Capture(IntPtr hWnd)
    {
        if (!IsAlive(hWnd) || !GetWindowRect(hWnd, out var wr)) return null;
        int fw = wr.R - wr.L, fh = wr.B - wr.T;
        if (fw < 2 || fh < 2 || fw > 16384 || fh > 16384) return null;

        var full = new Bitmap(fw, fh, PixelFormat.Format32bppArgb);
        try
        {
            using (var g = Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }

            // Crop to the client area (drop the title bar + borders) when we can locate it.
            if (GetClientRect(hWnd, out var cr) && cr.R >= 2 && cr.B >= 2)
            {
                var origin = new POINT();
                if (ClientToScreen(hWnd, ref origin))
                {
                    int offX = origin.X - wr.L, offY = origin.Y - wr.T, cw = cr.R, ch = cr.B;
                    if (offX >= 0 && offY >= 0 && offX + cw <= fw && offY + ch <= fh)
                    {
                        var client = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
                        using (var g2 = Graphics.FromImage(client))
                            g2.DrawImage(full, new Rectangle(0, 0, cw, ch),
                                new Rectangle(offX, offY, cw, ch), GraphicsUnit.Pixel);
                        full.Dispose();
                        return client;
                    }
                }
            }

            return full;
        }
        catch
        {
            full.Dispose();
            return null;
        }
    }
}
