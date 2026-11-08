using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ClaudeSwitch
{
    /// <summary>An error whose message is meant to be shown to the user as-is.</summary>
    internal sealed class UserMessageException : Exception
    {
        public UserMessageException(string message) : base(message) { }
    }

    internal static class Env
    {
        public static string Get(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        /// <summary>
        /// Test hook: when CLAUDESWITCH_NO_PROCESS=1, ClaudeSwitch never closes or starts Claude Desktop.
        /// </summary>
        public static bool NoProcess { get { return Get("CLAUDESWITCH_NO_PROCESS") == "1"; } }
    }

    internal static class AppPaths
    {
        static string dataDir;

        /// <summary>
        /// Where ClaudeSwitch keeps its settings and saved accounts:
        /// %APPDATA%\ClaudeSwitch, or a "data" folder next to the exe when portable.txt exists there.
        /// </summary>
        public static string DataDir
        {
            get
            {
                if (dataDir != null) return dataDir;
                string d = Env.Get("CLAUDESWITCH_DATA");
                if (d == null)
                {
                    string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    d = File.Exists(Path.Combine(exeDir, "portable.txt"))
                        ? Path.Combine(exeDir, "data")
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeSwitch");
                }
                Directory.CreateDirectory(d);
                dataDir = d;
                return d;
            }
        }

        public static string AccountsDir { get { return Path.Combine(DataDir, "accounts"); } }
        public static string BackupsDir { get { return Path.Combine(DataDir, "backups"); } }
        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "log.txt"); } }
        public static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        public static string LocalAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        public static string RoamingAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
    }

    /// <summary>Plain-text log in the data folder. Never log secrets: only paths, ids and errors.</summary>
    internal static class Log
    {
        static readonly object Gate = new object();

        public static void Info(string msg) { Write("INFO ", msg); }

        public static void Error(string msg, Exception ex)
        {
            Write("ERROR", ex == null ? msg : msg + ": " + ex);
        }

        static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    string f = AppPaths.LogFile;
                    if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024)
                    {
                        string old = f + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(f, old);
                    }
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level + " " + msg + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }

    internal static class Util
    {
        public static string Now() { return DateTime.UtcNow.ToString("o"); }
        public static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 12); }
        public static string Stamp() { return DateTime.Now.ToString("yyyyMMdd-HHmmss"); }
    }

    internal static class FileUtil
    {
        public static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static bool Exists(string path) { return File.Exists(path) || Directory.Exists(path); }

        /// <summary>Retries file operations for a few seconds, for files another program is still letting go of.</summary>
        public static void Retry(Action action)
        {
            Retry<object>(() => { action(); return null; });
        }

        public static T Retry<T>(Func<T> action)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return action();
                }
                catch (IOException)
                {
                    if (attempt >= 24) throw;
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt >= 24) throw;
                }
                Thread.Sleep(250);
            }
        }

        public static string ReadText(string path) { return Retry(() => File.ReadAllText(path)); }
        public static byte[] ReadBytes(string path) { return Retry(() => File.ReadAllBytes(path)); }

        public static void Delete(string path)
        {
            if (File.Exists(path))
            {
                Retry(() =>
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                });
            }
            else if (Directory.Exists(path))
            {
                Retry(() =>
                {
                    foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(path, true);
                });
            }
        }

        /// <summary>Copies a file, or a folder with everything in it.</summary>
        public static void Copy(string src, string dst)
        {
            if (File.Exists(src))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst)));
                Retry(() => File.Copy(src, dst, true));
            }
            else if (Directory.Exists(src))
            {
                Directory.CreateDirectory(dst);
                foreach (string d in Directory.GetDirectories(src)) Copy(d, Path.Combine(dst, Path.GetFileName(d)));
                foreach (string f in Directory.GetFiles(src)) Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            }
        }

        public static void WriteTextAtomic(string path, string text) { WriteBytesAtomic(path, Utf8.GetBytes(text)); }

        /// <summary>Writes to a temp file first and then swaps it in, so a crash never leaves half a file.</summary>
        public static void WriteBytesAtomic(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string tmp = path + ".cs-tmp";
            File.WriteAllBytes(tmp, data);
            Retry(() =>
            {
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
            });
        }

        /// <summary>Moves a freshly written folder into place, keeping the old one as "final.prev".</summary>
        public static void SwapIn(string tmp, string final)
        {
            string prev = final + ".prev";
            Delete(prev);
            if (Directory.Exists(final)) Retry(() => Directory.Move(final, prev));
            Retry(() => Directory.Move(tmp, final));
        }
    }

    /// <summary>Encrypts saved tokens with Windows DPAPI so only this Windows user can read them.</summary>
    internal static class Secret
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaudeSwitch/v1");

        public static void Write(string path, byte[] plain)
        {
            FileUtil.WriteBytesAtomic(path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }

        public static byte[] Read(string path)
        {
            if (!File.Exists(path)) return null;
            return ProtectedData.Unprotect(FileUtil.ReadBytes(path), Entropy, DataProtectionScope.CurrentUser);
        }
    }
}
