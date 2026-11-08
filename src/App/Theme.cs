using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClaudeSwitch
{
    /// <summary>Light and dark colour sets. Every control uses these through DynamicResource, so switching is instant.</summary>
    internal static class Theme
    {
        static readonly Dictionary<string, string> Light = new Dictionary<string, string>
        {
            { "Bg", "#F5F5F3" }, { "Surface", "#FFFFFF" }, { "SurfaceHover", "#F0F0ED" }, { "SurfaceAlt", "#E9E9E5" },
            { "Border", "#E2E1DC" }, { "Text", "#1D1D1F" }, { "TextMuted", "#6B6B70" },
            { "Accent", "#4F5BD5" }, { "AccentFg", "#4450C8" }, { "AccentText", "#FFFFFF" }, { "AccentSoft", "#ECEEFC" },
            { "Danger", "#C83434" }, { "DangerFill", "#D33A3A" }, { "DangerSoft", "#FCEBEB" },
            { "Success", "#2E9E57" }, { "Off", "#B9B9BE" },
            { "Overlay", "#59000000" }, { "ToastBg", "#1D1D1F" }, { "ToastText", "#FFFFFF" }
        };

        static readonly Dictionary<string, string> Dark = new Dictionary<string, string>
        {
            { "Bg", "#161618" }, { "Surface", "#1F1F22" }, { "SurfaceHover", "#29292D" }, { "SurfaceAlt", "#2A2A2E" },
            { "Border", "#313136" }, { "Text", "#EDEDEF" }, { "TextMuted", "#9A9AA2" },
            { "Accent", "#5E68E6" }, { "AccentFg", "#A3AAFF" }, { "AccentText", "#FFFFFF" }, { "AccentSoft", "#252846" },
            { "Danger", "#FF7B7B" }, { "DangerFill", "#C93C3C" }, { "DangerSoft", "#3A2224" },
            { "Success", "#3FBF6E" }, { "Off", "#55555C" },
            { "Overlay", "#8C000000" }, { "ToastBg", "#EDEDEF" }, { "ToastText", "#161618" }
        };

        public static bool IsDark { get; private set; }

        /// <summary>mode: "system", "light" or "dark".</summary>
        public static void Apply(string mode)
        {
            bool dark = mode == "dark" || (mode != "light" && SystemPrefersDark());
            IsDark = dark;
            var res = Application.Current.Resources;
            foreach (var kv in dark ? Dark : Light)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
                brush.Freeze();
                res[kv.Key] = brush;
            }
        }

        public static bool SystemPrefersDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Matches the window frame (border, rounded corners) to the theme on Windows 10/11.</summary>
        public static void ApplyToWindow(Window w)
        {
            IntPtr h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            try
            {
                int dark = IsDark ? 1 : 0;
                DwmSetWindowAttribute(h, 20, ref dark, 4);          // DWMWA_USE_IMMERSIVE_DARK_MODE
                int round = 2;
                DwmSetWindowAttribute(h, 33, ref round, 4);         // DWMWA_WINDOW_CORNER_PREFERENCE = round
                var c = (Color)ColorConverter.ConvertFromString((IsDark ? Dark : Light)["Border"]);
                int colorRef = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(h, 34, ref colorRef, 4);      // DWMWA_BORDER_COLOR
            }
            catch
            {
                // Older Windows: keep the default frame.
            }
        }
    }
}
