using System.Windows.Forms;

namespace DesktopStreamer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // DPI-unaware keeps screen-capture coordinates identical to the form's
        // bounds on scaled displays. We downscale to the panel anyway, so the
        // (minor) blurriness of virtualization is irrelevant here.
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settings = StreamSettings.Load(args);
        Application.Run(new OverlayForm(settings));
    }
}
