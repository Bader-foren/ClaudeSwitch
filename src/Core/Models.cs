using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace ClaudeSwitch
{
    public class AppSettings
    {
        public AppSettings()
        {
            Theme = "system";
            ManageDesktop = true;
            ManageCode = true;
            ReopenDesktop = true;
        }

        /// <summary>"system", "light" or "dark".</summary>
        public string Theme { get; set; }
        public bool ManageDesktop { get; set; }
        public bool ManageCode { get; set; }
        public bool ReopenDesktop { get; set; }
        public bool SeenSwitchNotice { get; set; }

        /// <summary>Claude Desktop data folder. Null = detect automatically.</summary>
        public string DesktopDataDir { get; set; }

        /// <summary>Start-menu app id used to reopen Claude Desktop (found automatically).</summary>
        public string DesktopAppId { get; set; }

        /// <summary>Advanced: which Claude Desktop files hold the sign-in. Null = built-in list.</summary>
        public List<string> DesktopItems { get; set; }

        public string ActiveAccountId { get; set; }

        /// <summary>Set while the user is signing in to a new account.</summary>
        public PendingSignIn Pending { get; set; }
    }

    public class PendingSignIn
    {
        public string BackupDir { get; set; }
        public bool Desktop { get; set; }
        public bool Code { get; set; }
        public string StartedAt { get; set; }
    }

    public class Account
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Color { get; set; }
        public int Order { get; set; }
        public string CreatedAt { get; set; }
        public string LastUsedAt { get; set; }

        public bool HasDesktop { get; set; }
        public string DesktopUuid { get; set; }

        public bool HasCode { get; set; }
        public string CodeUuid { get; set; }
        public string CodeEmail { get; set; }
    }

    internal static class Store
    {
        public static readonly string[] Palette =
        {
            "#E8735A", "#4E8DF5", "#3DB37A", "#E5A23A", "#9B6BF2", "#2DB3B3", "#E05F9A", "#7C8796"
        };

        public static AppSettings LoadSettings()
        {
            string f = AppPaths.SettingsFile;
            if (!File.Exists(f)) return new AppSettings();
            try
            {
                return JsonText.Serializer().Deserialize<AppSettings>(FileUtil.ReadText(f)) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Log.Error("settings.json could not be read; using defaults", ex);
                try { File.Copy(f, f + ".bad", true); } catch { }
                return new AppSettings();
            }
        }

        public static void SaveSettings(AppSettings s)
        {
            FileUtil.WriteTextAtomic(AppPaths.SettingsFile, JsonText.Pretty(JsonText.Serializer().Serialize(s)));
        }

        public static string AccountDir(string id) { return Path.Combine(AppPaths.AccountsDir, id); }

        public static List<Account> LoadAccounts()
        {
            var list = new List<Account>();
            if (!Directory.Exists(AppPaths.AccountsDir)) return list;
            var json = JsonText.Serializer();
            foreach (string dir in Directory.GetDirectories(AppPaths.AccountsDir))
            {
                string f = Path.Combine(dir, "account.json");
                if (!File.Exists(f)) continue;
                try
                {
                    var a = json.Deserialize<Account>(FileUtil.ReadText(f));
                    if (a == null) continue;
                    a.Id = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(a.Name)) a.Name = "Account";
                    list.Add(a);
                }
                catch (Exception ex)
                {
                    Log.Error("Skipping unreadable account " + dir, ex);
                }
            }
            return list.OrderBy(a => a.Order).ThenBy(a => a.CreatedAt, StringComparer.Ordinal).ToList();
        }

        public static void SaveAccount(Account a)
        {
            string f = Path.Combine(AccountDir(a.Id), "account.json");
            FileUtil.WriteTextAtomic(f, JsonText.Pretty(JsonText.Serializer().Serialize(a)));
        }

        public static void DeleteAccount(string id) { FileUtil.Delete(AccountDir(id)); }

        /// <summary>Creates a new, empty backup folder and keeps only the newest ones.</summary>
        public static string NewBackupDir(string reason, string keepPath)
        {
            Directory.CreateDirectory(AppPaths.BackupsDir);
            string baseName = Path.Combine(AppPaths.BackupsDir, Util.Stamp() + "-" + reason);
            string d = baseName;
            for (int n = 2; Directory.Exists(d); n++) d = baseName + "-" + n;
            Directory.CreateDirectory(d);
            PruneBackups(12, keepPath, d);
            return d;
        }

        static void PruneBackups(int keep, string keepPath, string newest)
        {
            try
            {
                var dirs = Directory.GetDirectories(AppPaths.BackupsDir)
                    .OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal)
                    .ToList();
                for (int i = keep; i < dirs.Count; i++)
                {
                    if (PathEq(dirs[i], keepPath) || PathEq(dirs[i], newest)) continue;
                    FileUtil.Delete(dirs[i]);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Pruning backups failed", ex);
            }
        }

        static bool PathEq(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
