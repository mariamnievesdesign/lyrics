using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Letras;

/// <summary>
/// Vidrio acrílico nativo de Windows 11 (el mismo de los menús y paneles del sistema),
/// con modo oscuro y esquinas redondeadas.
/// </summary>
static class Glass
{
    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int Left, Right, Top, Bottom; }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    const int DWMWCP_ROUND = 2;
    const int DWMSBT_TRANSIENTWINDOW = 3; // Acrílico

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>Activa el vidrio. Devuelve false si este Windows no lo soporta.</summary>
    public static bool Enable(Window window)
    {
        // El fondo acrílico llegó en Windows 11 22H2 (build 22621).
        if (Environment.OSVersion.Version.Build < 22621) return false;

        var hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        if (hwnd == IntPtr.Zero || source?.CompositionTarget == null) return false;

        try
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

            var dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            var corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            var backdrop = DWMSBT_TRANSIENTWINDOW;
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }
}
