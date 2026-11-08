using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClaudeSwitch
{
    /// <summary>
    /// Claude Desktop (the Windows app). Its sign-in lives in Chromium-style files in its data folder:
    /// the cookie database (claude.ai session), "Local State" (the key those cookies are encrypted with),
    /// web storage, and a few account keys inside config.json.
    /// </summary>
    internal sealed class DesktopClient
    {
        public static readonly string[] DefaultItems =
        {
            @"Network\Cookies", @"Network\Cookies-journal", "Cookies", "Cookies-journal",
            "Local State", "Local Storage", "Session Storage", "IndexedDB", "WebStorage", "config.json"
        };

        const string ConfigFile = "config.json";
        const string LocalStateFile = "Local State";

        public readonly string DataDir;
        public readonly List<string> Items;

        public DesktopClient(string dataDir, IEnumerable<string> items)
        {
            DataDir = dataDir;
            Items = (items ?? DefaultItems).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            if (Items.Count == 0) Items = DefaultItems.ToList();
        }

        public bool Found { get { return DataDir != null; } }

        /// <summary>Finds Claude Desktop's data folder (classic install, Microsoft Store install, or relocated data).</summary>
        public static string Detect(string overrideDir)
        {
            string env = Env.Get("CLAUDESWITCH_DESKTOP_DIR");
            if (env != null) return Directory.Exists(env) ? env : null;
            if (!string.IsNullOrWhiteSpace(overrideDir)) return Directory.Exists(overrideDir) ? overrideDir : null;

            var candidates = new List<string>();
            try
            {
                string relocated = Path.Combine(AppPaths.LocalAppData, "Claude-Data");
                if (Directory.Exists(relocated)) candidates.AddRange(Directory.GetDirectories(relocated));
            }
            catch { }
            try
            {
                string packages = Path.Combine(AppPaths.LocalAppData, "Packages");
                if (Directory.Exists(packages))
                    foreach (string p in Directory.GetDirectories(packages, "Claude_*"))
                        candidates.Add(Path.Combine(p, @"LocalCache\Roaming\Claude"));
            }
            catch { }
            candidates.Add(Path.Combine(AppPaths.RoamingAppData, "Claude"));

            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string c in candidates)
            {
                string ls = Path.Combine(c, LocalStateFile);
                if (!File.Exists(ls)) continue;
                DateTime t = File.GetLastWriteTimeUtc(ls);
                foreach (string extra in new[] { ConfigFile, @"Network\Cookies", "Cookies" })
                {
                    string p = Path.Combine(c, extra);
                    if (File.Exists(p) && File.GetLastWriteTimeUtc(p) > t) t = File.GetLastWriteTimeUtc(p);
                }
                if (t > bestTime) { best = c; bestTime = t; }
            }
            return best;
        }

        // ---- identity ---------------------------------------------------------------------------

        /// <summary>The Anthropic account id Claude Desktop last signed in with (from config.json).</summary>
        public string ReadAccountUuid() { return Found ? ReadUuidFrom(DataDir) : null; }

        public static string ReadUuidFrom(string dir)
        {
            try
            {
                string p = Path.Combine(dir, ConfigFile);
                return File.Exists(p) ? JsonText.GetString(FileUtil.ReadText(p), "lastKnownAccountUuid") : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// True when the cookie database holds a claude.ai session, false when it doesn't,
        /// null when it can't be read (Claude Desktop is running and has it locked).
        /// Only the cookie *name* is looked for; cookie values are encrypted and never read.
        /// </summary>
        public bool? HasSessionCookie() { return Found ? CookieScan(DataDir) : false; }

        public static bool? CookieScan(string dir)
        {
            byte[] needle = Encoding.ASCII.GetBytes("sessionKey");
            foreach (string rel in new[] { @"Network\Cookies", "Cookies" })
            {
                string p = Path.Combine(dir, rel);
                if (!File.Exists(p)) continue;
                try
                {
                    byte[] data;
                    using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        data = new byte[fs.Length];
                        int read = 0;
                        while (read < data.Length)
                        {
                            int n = fs.Read(data, read, data.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                    }
                    return IndexOf(data, needle) >= 0;
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
            return false;
        }

        static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i <= hay.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        // ---- snapshots (Claude Desktop must be closed) -------------------------------------------

        public void CaptureTo(string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string item in Items)
            {
                string src = Path.Combine(DataDir, item);
                if (FileUtil.Exists(src)) FileUtil.Copy(src, Path.Combine(destDir, item));
            }
        }

        public void RestoreFrom(string srcDir)
        {
            foreach (string item in Items)
            {
                if (item == ConfigFile) { RestoreConfig(srcDir); continue; }
                string live = Path.Combine(DataDir, item);
                FileUtil.Delete(live);
                string snap = Path.Combine(srcDir, item);
                if (FileUtil.Exists(snap)) FileUtil.Copy(snap, live);
            }
        }

        /// <summary>Removes the local sign-in so Claude Desktop opens on its login screen. Settings are kept.</summary>
        public void SignOut()
        {
            foreach (string item in Items)
            {
                if (item == LocalStateFile) continue;
                if (item == ConfigFile)
                {
                    string p = Path.Combine(DataDir, ConfigFile);
                    if (!File.Exists(p)) continue;
                    string text = FileUtil.ReadText(p);
                    string stripped = StripAccountKeys(text);
                    if (stripped != text) FileUtil.WriteTextAtomic(p, stripped);
                    continue;
                }
                FileUtil.Delete(Path.Combine(DataDir, item));
            }
        }

        static bool IsAccountKey(string key)
        {
            return key.StartsWith("oauth:", StringComparison.Ordinal) || key == "lastKnownAccountUuid";
        }

        static string StripAccountKeys(string text)
        {
            foreach (string k in JsonText.TopLevelKeys(text))
                if (IsAccountKey(k)) text = JsonText.Remove(text, k);
            return text;
        }

        /// <summary>
        /// config.json mixes app settings with account tokens. Only the account keys are swapped,
        /// so settings like language stay the same for every account.
        /// </summary>
        void RestoreConfig(string srcDir)
        {
            string live = Path.Combine(DataDir, ConfigFile);
            string snap = Path.Combine(srcDir, ConfigFile);
            string snapText = File.Exists(snap) ? FileUtil.ReadText(snap) : null;
            if (!File.Exists(live))
            {
                if (snapText != null) FileUtil.Copy(snap, live);
                return;
            }

            string text = FileUtil.ReadText(live);
            string merged;
            try
            {
                merged = StripAccountKeys(text);
                if (snapText != null)
                    foreach (string key in JsonText.TopLevelKeys(snapText))
                        if (IsAccountKey(key)) merged = JsonText.SetRaw(merged, key, JsonText.GetRaw(snapText, key));
            }
            catch (FormatException ex)
            {
                Log.Error("config.json could not be merged; using the saved copy", ex);
                if (snapText != null) FileUtil.Copy(snap, live);
                return;
            }
            if (merged != text) FileUtil.WriteTextAtomic(live, merged);
        }
    }

    /// <summary>Finds, closes and opens the Claude Desktop app. Never touches Claude Code (CLI) processes.</summary>
    internal static class DesktopProcess
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        const uint WmClose = 0x0010;

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>
        /// Claude Desktop runs from WindowsApps\Claude_* (Store) or %LOCALAPPDATA%\AnthropicClaude (classic).
        /// Claude Code is also called claude.exe but lives elsewhere, so it is never matched.
        /// </summary>
        static bool IsDesktopImage(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.ToLowerInvariant();
            return p.Contains(@"\windowsapps\claude_") || p.Contains(@"\anthropicclaude\");
        }

        static List<Process> Find()
        {
            var list = new List<Process>();
            foreach (Process p in Process.GetProcessesByName("claude"))
            {
                if (IsDesktopImage(ImagePath(p.Id))) list.Add(p);
                else p.Dispose();
            }
            return list;
        }

        public static bool IsRunning()
        {
            if (Env.NoProcess) return false;
            var procs = Find();
            bool running = procs.Count > 0;
            foreach (var p in procs) p.Dispose();
            return running;
        }

        public static string RunningImage()
        {
            if (Env.NoProcess) return null;
            string path = null;
            foreach (var p in Find())
            {
                if (path == null) path = ImagePath(p.Id);
                p.Dispose();
            }
            return path;
        }

        /// <summary>Asks Claude Desktop to close, then makes sure it is gone. Returns true if it was running.</summary>
        public static bool Close(Action<string> progress)
        {
            if (Env.NoProcess) return false;
            var procs = Find();
            if (procs.Count == 0) return false;

            progress("Closing Claude…");
            var pids = new HashSet<int>(procs.Select(p => p.Id));
            foreach (var p in procs) p.Dispose();
            EnumWindows((hwnd, l) =>
            {
                int pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pids.Contains(pid) && IsWindowVisible(hwnd)) PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);

            if (!WaitGone(5000))
            {
                Log.Info("Claude Desktop still running after close request (probably in the tray); ending it");
                foreach (var p in Find())
                {
                    try { p.Kill(); }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) { }
                    p.Dispose();
                }
                if (!WaitGone(10000))
                    throw new UserMessageException("Claude Desktop didn't close. Quit it from its tray icon, then try again.");
            }
            Thread.Sleep(500); // let Windows release the file handles
            return true;
        }

        static bool WaitGone(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                var procs = Find();
                bool any = procs.Count > 0;
                foreach (var p in procs) p.Dispose();
                if (!any) return true;
                Thread.Sleep(200);
            }
            return false;
        }

        public static void Launch(AppSettings settings, string lastImage)
        {
            if (Env.NoProcess) return;

            if (!string.IsNullOrEmpty(lastImage) && File.Exists(lastImage) &&
                lastImage.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Process.Start(new ProcessStartInfo(lastImage) { UseShellExecute = true });
                return;
            }

            if (string.IsNullOrEmpty(settings.DesktopAppId)) settings.DesktopAppId = DetectAppId();
            if (!string.IsNullOrEmpty(settings.DesktopAppId))
            {
                Process.Start("explorer.exe", "shell:AppsFolder\\" + settings.DesktopAppId);
                return;
            }

            string classic = Path.Combine(AppPaths.LocalAppData, "AnthropicClaude", "claude.exe");
            if (File.Exists(classic))
            {
                Process.Start(new ProcessStartInfo(classic) { UseShellExecute = true });
                return;
            }
            throw new UserMessageException("Couldn't find Claude Desktop to open it. Please open it yourself.");
        }

        /// <summary>Looks up Claude's Start-menu app id, e.g. "Claude_pzs8sxrjxfjjc!Claude".</summary>
        static string DetectAppId()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"Get-StartApps | Where-Object { $_.Name -eq 'Claude' } | Select-Object -First 1 -ExpandProperty AppID\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(10000);
                    return output.Length > 0 ? output : null;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Looking up Claude's app id failed", ex);
                return null;
            }
        }
    }
}
