using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ClaudeSwitch
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [STAThread]
        static int Main()
        {
            // One copy at a time: starting it again just brings the open window to the front.
            string mutexName = "Local\\ClaudeSwitch.SingleInstance" + (Env.Get("CLAUDESWITCH_DATA") != null ? ".custom" : "");
            bool created;
            using (var mutex = new Mutex(true, mutexName, out created))
            {
                if (!created)
                {
                    ActivateExisting();
                    return 0;
                }

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log.Error("Unhandled error", e.Exception);
                    MessageBox.Show(e.Exception.Message, "ClaudeSwitch", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    Log.Error("Unobserved task error", e.Exception);
                    e.SetObserved();
                };

                try
                {
                    var ui = new MainWindowController();
                    return app.Run(ui.Window);
                }
                catch (Exception ex)
                {
                    Log.Error("Startup failed", ex);
                    MessageBox.Show("ClaudeSwitch couldn't start:\n\n" + ex.Message, "ClaudeSwitch", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
            }
        }

        static void ActivateExisting()
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                IntPtr h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                ShowWindow(h, 9); // SW_RESTORE
                SetForegroundWindow(h);
            }
        }
    }
}
