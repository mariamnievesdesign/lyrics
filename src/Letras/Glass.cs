using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Letras;

/// <summary>Desenfoque real de lo que hay detrás de la ventana (efecto vidrio) y esquinas redondeadas.</summary>
static class Glass
{
    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_ENABLE_BLURBEHIND = 3;

    [DllImport("user32.dll")]
    static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    public static void Enable(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch { /* Si Windows no lo soporta, queda el vidrio translúcido sin desenfoque. */ }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>Recorta la ventana con esquinas redondeadas para que el desenfoque no se vea cuadrado.</summary>
    public static void UpdateRegion(Window window, double radius)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget == null) return;

        var m = source.CompositionTarget.TransformToDevice;
        var w = (int)Math.Ceiling(window.ActualWidth * m.M11);
        var h = (int)Math.Ceiling(window.ActualHeight * m.M22);
        if (w < 2 || h < 2) return;
        var d = (int)Math.Round(radius * 2 * m.M11);

        var rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
        if (SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn); // Si funciona, Windows se queda con la región.
    }
}
