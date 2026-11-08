using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeSwitch
{
    internal sealed class UpdateInfo
    {
        public Version Version;
        public string Tag;
        public string AssetUrl;
        public string Sha256;
    }

    /// <summary>
    /// Keeps ClaudeSwitch up to date from this project's GitHub releases.
    ///
    /// The newest release's ClaudeSwitch.exe is downloaded, checked against the SHA-256 checksum GitHub publishes
    /// for it (and that it really is ClaudeSwitch, in the promised version), then swapped in for the exe on disk.
    /// Windows lets a running program's file be renamed but not overwritten, so the running copy is renamed to
    /// ".old" and removed on the next start. The new version runs the next time ClaudeSwitch opens.
    /// </summary>
    internal static class Updater
    {
        /// <summary>The GitHub repository releases come from. If you fork ClaudeSwitch, change this to yours.</summary>
        public const string Repo = "Bader-foren/ClaudeSwitch";

        const string AssetName = "ClaudeSwitch.exe";
        const string ProgramName = "ClaudeSwitch";
        const string UserAgent = "ClaudeSwitch-Updater";

        public static Version Current
        {
            get
            {
                string v = Env.Get("CLAUDESWITCH_CURRENT_VERSION"); // test hook
                return Normalize(v != null ? new Version(v) : Assembly.GetExecutingAssembly().GetName().Version);
            }
        }

        public static string CurrentText { get { return Display(Current); } }

        public static string Display(Version v) { return v.Revision > 0 ? v.ToString(4) : v.ToString(3); }

        /// <summary>The ClaudeSwitch.exe that is running (or a test copy).</summary>
        public static string ExePath
        {
            get { return Env.Get("CLAUDESWITCH_EXE_PATH") ?? Assembly.GetEntryAssembly().Location; }
        }

        static bool TestFeed { get { return Env.Get("CLAUDESWITCH_UPDATE_FEED") != null; } }

        static string FeedUrl
        {
            get { return Env.Get("CLAUDESWITCH_UPDATE_FEED") ?? "https://api.github.com/repos/" + Repo + "/releases/latest"; }
        }

        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        public static bool TryParseTag(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            Version v;
            if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out v)) return false;
            version = Normalize(v);
            return true;
        }

        public static bool IsNewer(UpdateInfo u) { return u != null && u.Version > Current; }

        /// <summary>Asks GitHub for the newest release. Returns null when it has no usable ClaudeSwitch.exe.</summary>
        public static UpdateInfo Check()
        {
            string json;
            using (var client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = client.DownloadString(FeedUrl);
            }

            var release = JsonText.ParseObject(json);
            if (release == null) return null;
            string tag = JsonText.Str(release, "tag_name");
            Version version;
            if (!TryParseTag(tag, out version)) return null;

            object assets;
            if (!release.TryGetValue("assets", out assets) || assets is string || !(assets is IEnumerable)) return null;
            foreach (object item in (IEnumerable)assets)
            {
                var asset = item as Dictionary<string, object>;
                if (asset == null || !string.Equals(JsonText.Str(asset, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                string digest = JsonText.Str(asset, "digest");
                return new UpdateInfo
                {
                    Version = version,
                    Tag = tag,
                    AssetUrl = JsonText.Str(asset, "browser_download_url"),
                    Sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest.Substring(7).ToLowerInvariant() : null
                };
            }
            return null;
        }

        /// <summary>
        /// Downloads and verifies the update, then puts it in place of <paramref name="exePath"/>.
        /// The running copy keeps running. Throws (and changes nothing) if anything doesn't check out.
        /// </summary>
        public static void Install(UpdateInfo u, string exePath)
        {
            if (u == null || string.IsNullOrEmpty(u.AssetUrl)) throw new UserMessageException("The new release doesn't include ClaudeSwitch.exe.");
            if (string.IsNullOrEmpty(u.Sha256)) throw new UserMessageException("GitHub didn't publish a checksum for the update, so it wasn't installed.");
            var uri = new Uri(u.AssetUrl);
            if (uri.Scheme != Uri.UriSchemeHttps && !(TestFeed && uri.IsFile))
                throw new UserMessageException("The update isn't on a secure (https) address, so it wasn't installed.");

            string download = exePath + ".download";
            try
            {
                if (File.Exists(download)) File.Delete(download);
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                    client.DownloadFile(uri, download);
                }
                if (!string.Equals(Sha256Of(download), u.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UserMessageException("The downloaded update didn't match GitHub's checksum, so it wasn't installed.");

                AssemblyName name;
                try { name = AssemblyName.GetAssemblyName(download); }
                catch (BadImageFormatException) { name = null; }
                if (name == null || name.Name != ProgramName || Normalize(name.Version) != u.Version)
                    throw new UserMessageException("The downloaded file isn't ClaudeSwitch " + Display(u.Version) + ", so it wasn't installed.");
            }
            catch (Exception ex)
            {
                TryDelete(download);
                if (ex is UserMessageException) throw;
                if (ex is WebException) throw new UserMessageException("Couldn't download the update. Check your internet connection and try again.");
                if (ex is UnauthorizedAccessException) throw NotWritable();
                throw;
            }

            // Swap: rename the current exe out of the way (allowed even while it runs), then move the new one in.
            string old = exePath + ".old";
            if (File.Exists(old) && !TryDelete(old)) old = exePath + ".old-" + DateTime.Now.Ticks; // an older copy still runs from it
            try
            {
                File.Move(exePath, old);
            }
            catch (Exception ex)
            {
                TryDelete(download);
                if (ex is UnauthorizedAccessException || ex is IOException) throw NotWritable();
                throw;
            }
            try
            {
                File.Move(download, exePath);
            }
            catch
            {
                File.Move(old, exePath);
                TryDelete(download);
                throw NotWritable();
            }
            Log.Info("Installed update " + Display(u.Version) + " over " + CurrentText);
        }

        static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static UserMessageException NotWritable()
        {
            return new UserMessageException("ClaudeSwitch can't update itself in the folder it's in. Move ClaudeSwitch.exe to a folder you can change, such as your Desktop, and try again.");
        }

        /// <summary>Removes what an earlier update left behind (the previous exe, unfinished downloads).</summary>
        public static void CleanUp(string exePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(exePath);
                string file = Path.GetFileName(exePath);
                var leftovers = Directory.GetFiles(dir, file + ".old*").Concat(Directory.GetFiles(dir, file + ".download"));
                foreach (string f in leftovers)
                {
                    try { File.Delete(f); }
                    catch { } // still in use: try again next time
                }
            }
            catch
            {
            }
        }

        static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
