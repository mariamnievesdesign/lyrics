using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Letras;

/// <summary>
/// Vidrio acrílico (desenfoque de lo que hay detrás + tinte oscuro) que se mantiene
/// aunque la ventana no esté activa — importante porque normalmente estarás usando otra app.
/// </summary>
static class Glass
{
    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // Formato AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    const int DWMWCP_ROUND = 2;
    const int DWMSBT_NONE = 1;

    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    const int ACCENT_FLAG_USE_TINT = 2;
    /// <summary>Niveles de tinte (opacidad del oscurecido), de más transparente a más oscuro.</summary>
    public static readonly byte[] Levels = { 0x12, 0x30, 0x58, 0x88 };

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>Activa el vidrio. Devuelve false si este Windows no lo soporta.</summary>
    public static bool Enable(Window window, int level)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        if (hwnd == IntPtr.Zero || source?.CompositionTarget == null) return false;

        try
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

            // Opcionales (solo Windows 11): modo oscuro, esquinas redondeadas y sin fondo de sistema.
            var dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            var corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            var none = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));

            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            return SetTint(window, level);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Cambia qué tan oscuro es el vidrio. level: índice en Levels.</summary>
    public static bool SetTint(Window window, int level)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var alpha = (uint)Levels[Math.Clamp(level, 0, Levels.Length - 1)];
        var accent = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = ACCENT_FLAG_USE_TINT,
            GradientColor = (alpha << 24) | 0x001E1618, // AABBGGRR: gris violáceo muy oscuro
        };
        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
