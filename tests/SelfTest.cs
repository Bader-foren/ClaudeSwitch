using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeSwitch.Tests
{
    /// <summary>
    /// Runs the switching engine against fake Claude Desktop / Claude Code folders.
    /// Never touches the real Claude data and never closes or opens any app (CLAUDESWITCH_NO_PROCESS=1).
    /// Usage: build.cmd test [folder]
    /// </summary>
    internal static class SelfTest
    {
        const string Mcp = "{\n  \"mcpServers\": {\n    \"x\": {\"command\": \"node\"}\n  }\n}";

        static int failures, passes;
        static string root, desktop, codeDir, codeCfg;
        static readonly Action<string> Quiet = s => { };

        static int Main(string[] args)
        {
            root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "claudeswitch-selftest");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            desktop = Path.Combine(root, "desktop");
            codeDir = Path.Combine(root, "home", ".claude");
            codeCfg = Path.Combine(root, "home", ".claude.json");
            Directory.CreateDirectory(desktop);
            Directory.CreateDirectory(codeDir);

            Environment.SetEnvironmentVariable("CLAUDESWITCH_DATA", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("CLAUDESWITCH_DESKTOP_DIR", desktop);
            Environment.SetEnvironmentVariable("CLAUDESWITCH_CODE_DIR", codeDir);
            Environment.SetEnvironmentVariable("CLAUDESWITCH_CODE_CONFIG", codeCfg);
            Environment.SetEnvironmentVariable("CLAUDESWITCH_NO_PROCESS", "1");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);

            try
            {
                JsonTests();
                EngineTests();
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("  CRASH " + ex);
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL " + passes + " CHECKS PASSED" : failures + " FAILED, " + passes + " passed");
            return failures == 0 ? 0 : 1;
        }

        static void Check(bool ok, string name)
        {
            if (ok) passes++; else failures++;
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
        }

        static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine(title);
        }

        // ---- JSON ---------------------------------------------------------------------------------

        static void JsonTests()
        {
            Section("JSON editing");
            string pretty = "{\n  \"a\": 1,\n  \"b\": {\"x\": \"}{\\\"\"},\n  \"c\": [1, 2, {\"d\": null}]\n}";
            Check(JsonText.Remove(pretty, "a") == "{\n  \"b\": {\"x\": \"}{\\\"\"},\n  \"c\": [1, 2, {\"d\": null}]\n}", "remove first property");
            Check(JsonText.Remove(pretty, "b") == "{\n  \"a\": 1,\n  \"c\": [1, 2, {\"d\": null}]\n}", "remove middle property");
            Check(JsonText.Remove(pretty, "c") == "{\n  \"a\": 1,\n  \"b\": {\"x\": \"}{\\\"\"}\n}", "remove last property");
            Check(JsonText.Remove("{\"only\":true}", "only") == "{}", "remove the only property");
            Check(JsonText.Remove(pretty, "zzz") == pretty, "removing a missing property changes nothing");
            Check(JsonText.GetRaw(pretty, "b") == "{\"x\": \"}{\\\"\"}", "read an object containing tricky strings");
            Check(JsonText.SetRaw(pretty, "a", "2") == pretty.Replace("\"a\": 1", "\"a\": 2"), "replace a value in place");
            Check(JsonText.SetRaw("{}", "k", "\"v\"") == "{\"k\": \"v\"}", "add to an empty object");
            Check(JsonText.SetRaw(pretty, "new", "true").StartsWith("{\n  \"new\": true,\n  \"a\": 1"), "add keeps the file's indentation");
            Check(JsonText.GetString("{\"s\": \"caf\\u00e9 \\\"q\\\"\"}", "s") == "café \"q\"", "decode escapes");
            Check(JsonText.TopLevelKeys(pretty).SequenceEqual(new[] { "a", "b", "c" }), "list top-level keys only");
            Check(JsonText.GetRaw("{\"outer\": {\"inner\": 1}}", "inner") == null, "nested keys are not mistaken for top-level ones");
        }

        // ---- fake Claude installs -----------------------------------------------------------------

        static void WriteDesktopInstall()
        {
            File.WriteAllText(Path.Combine(desktop, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"KEY1\"}}");
            File.WriteAllText(Path.Combine(desktop, "claude_desktop_config.json"), Mcp);
            File.WriteAllText(Path.Combine(desktop, "config.json"), "{\n  \"locale\": \"en-US\",\n  \"windowSizeWasSignedIn\": true\n}");
            Directory.CreateDirectory(Path.Combine(desktop, "Cache"));
            File.WriteAllText(Path.Combine(desktop, "Cache", "data_0"), "cache");
        }

        static void SignInDesktop(string tag)
        {
            Directory.CreateDirectory(Path.Combine(desktop, "Network"));
            File.WriteAllBytes(Path.Combine(desktop, "Network", "Cookies"),
                Encoding.ASCII.GetBytes("SQLite format 3\0 claude.ai sessionKey v10ENC-" + tag + " lastActiveOrg"));
            Directory.CreateDirectory(Path.Combine(desktop, "Local Storage", "leveldb"));
            File.WriteAllText(Path.Combine(desktop, "Local Storage", "leveldb", "000003.log"), "user-" + tag);
            string idb = Path.Combine(desktop, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb");
            Directory.CreateDirectory(idb);
            File.WriteAllText(Path.Combine(idb, "CURRENT"), "idb-" + tag);
            string cfg = LiveConfig();
            cfg = JsonText.SetRaw(cfg, "lastKnownAccountUuid", JsonText.Quote("uuid-" + tag));
            cfg = JsonText.SetRaw(cfg, "oauth:tokenCacheV2", JsonText.Quote("tok-" + tag));
            File.WriteAllText(Path.Combine(desktop, "config.json"), cfg);
        }

        static void WriteCodeConfig()
        {
            File.WriteAllText(codeCfg,
                "{\n  \"numStartups\": 42,\n  \"projects\": {\n    \"C:\\\\work\\\\{app}\": {\n      \"history\": [\"say \\\"hi\\\" {not a brace}\"],\n      \"cost\": 0.10000000000000001\n    }\n  },\n  \"userID\": \"abc\"\n}");
        }

        static void SignInCode(string tag)
        {
            File.WriteAllText(Path.Combine(codeDir, ".credentials.json"),
                "{\"claudeAiOauth\":{\"accessToken\":\"at-" + tag + "\",\"refreshToken\":\"rt-" + tag + "\"}}");
            string account = "{\n    \"accountUuid\": \"uuid-" + tag + "\",\n    \"emailAddress\": \"" + tag.ToLowerInvariant()
                + "@example.com\",\n    \"displayName\": \"" + tag + "\"\n  }";
            File.WriteAllText(codeCfg, JsonText.SetRaw(LiveCodeCfg(), "oauthAccount", account));
        }

        static string LiveCookies()
        {
            string p = Path.Combine(desktop, "Network", "Cookies");
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }

        static string LiveConfig() { return File.ReadAllText(Path.Combine(desktop, "config.json")); }

        static string LiveCreds()
        {
            string p = Path.Combine(codeDir, ".credentials.json");
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }

        static string LiveCodeCfg() { return File.ReadAllText(codeCfg); }
        static string Projects(string cfg) { return JsonText.GetRaw(cfg, "projects"); }
        static string AccountDir(Account a) { return Path.Combine(root, "data", "accounts", a.Id); }
        static string BackupsDir { get { return Path.Combine(root, "data", "backups"); } }
        static Account Find(Engine e, string name) { return e.Accounts.FirstOrDefault(a => a.Name == name); }

        static string SavedCodeCreds(Account a)
        {
            return Encoding.UTF8.GetString(Secret.Read(Path.Combine(AccountDir(a), "code", "credentials.bin")));
        }

        // ---- engine -------------------------------------------------------------------------------

        static void EngineTests()
        {
            Section("Detect and save the first account");
            WriteDesktopInstall();
            WriteCodeConfig();
            SignInDesktop("A");
            SignInCode("A");
            string projectsBefore = Projects(LiveCodeCfg());

            var e = new Engine();
            Check(e.Desktop.Found && e.Code.Found, "finds Claude Desktop and Claude Code");
            var live = e.ReadLive(true);
            Check(live.DesktopUuid == "uuid-A" && live.DesktopSignedIn == true, "reads Claude Desktop's account and session");
            Check(live.Code.SignedIn && live.Code.Email == "a@example.com", "reads Claude Code's account");

            var alice = e.SaveCurrent(null, "Alice", true, true, Quiet).Account;
            Check(e.Accounts.Count == 1 && alice.HasDesktop && alice.HasCode, "saves Desktop + Code sign-in");
            Check(alice.DesktopUuid == "uuid-A" && alice.CodeUuid == "uuid-A" && alice.Email == "a@example.com", "remembers account ids and email");
            Check(File.Exists(Path.Combine(AccountDir(alice), "desktop", "Network", "Cookies")), "saved copy holds the cookie file");
            byte[] enc = File.ReadAllBytes(Path.Combine(AccountDir(alice), "code", "credentials.bin"));
            Check(!Encoding.ASCII.GetString(enc).Contains("at-A"), "Claude Code tokens are encrypted on disk");
            Check(!Directory.Exists(Path.Combine(AccountDir(alice), "desktop", "Cache"))
                  && !File.Exists(Path.Combine(AccountDir(alice), "desktop", "claude_desktop_config.json")), "caches and MCP config are not copied");
            Check(e.FindActive(e.ReadLive(false)).Id == alice.Id, "Alice shows as active");

            Section("Sign in with another account");
            e.BeginNewSignIn(true, true, Quiet);
            Check(e.Settings.Pending != null && Directory.Exists(e.Settings.Pending.BackupDir), "remembers the pending sign-in and its backup");
            Check(LiveCookies() == null && !Directory.Exists(Path.Combine(desktop, "Local Storage")), "Claude Desktop session removed locally");
            string cfgOut = LiveConfig();
            Check(JsonText.GetRaw(cfgOut, "lastKnownAccountUuid") == null && JsonText.GetRaw(cfgOut, "oauth:tokenCacheV2") == null, "account keys removed from config.json");
            Check(JsonText.GetString(cfgOut, "locale") == "en-US", "app settings in config.json kept");
            Check(File.Exists(Path.Combine(desktop, "Local State")), "Local State kept");
            Check(LiveCreds() == null && JsonText.GetRaw(LiveCodeCfg(), "oauthAccount") == null, "Claude Code signed out locally");
            Check(Projects(LiveCodeCfg()) == projectsBefore, "rest of .claude.json untouched");
            Check(e.FindActive(e.ReadLive(false)) == null, "no account shows as active while signing in");

            SignInDesktop("B");
            SignInCode("B");
            var bobResult = e.SaveCurrent(null, "Bob", true, true, Quiet);
            var bob = bobResult.Account;
            Check(e.Accounts.Count == 2 && e.Settings.Pending == null, "Bob saved, pending sign-in finished");
            Check(bob.DesktopUuid == "uuid-B" && bob.Email == "b@example.com" && bobResult.Notes.Count == 0, "Bob has his own ids and email");

            Section("One-click switch");
            // While Bob is active his tokens refresh, a setting changes, and Claude Code writes unrelated data.
            File.WriteAllText(Path.Combine(codeDir, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"at-B2\",\"refreshToken\":\"rt-B2\"}}");
            File.WriteAllBytes(Path.Combine(desktop, "Network", "Cookies"), Encoding.ASCII.GetBytes("SQLite format 3\0 sessionKey v10ENC-B2"));
            File.WriteAllText(Path.Combine(desktop, "config.json"), JsonText.SetRaw(LiveConfig(), "locale", "\"fr-FR\""));
            File.WriteAllText(codeCfg, JsonText.SetRaw(LiveCodeCfg(), "numStartups", "43"));

            e.SwitchTo(Find(e, "Alice"), Quiet);
            Check(LiveCookies().Contains("ENC-A"), "Claude Desktop cookies are Alice's");
            Check(File.ReadAllText(Path.Combine(desktop, "Local Storage", "leveldb", "000003.log")) == "user-A", "web storage is Alice's");
            Check(JsonText.GetString(LiveConfig(), "lastKnownAccountUuid") == "uuid-A"
                  && JsonText.GetString(LiveConfig(), "oauth:tokenCacheV2") == "tok-A", "config.json account keys are Alice's");
            Check(JsonText.GetString(LiveConfig(), "locale") == "fr-FR", "app settings survive the switch");
            Check(LiveCreds().Contains("at-A"), "Claude Code credentials are Alice's");
            Check(e.Code.ReadLive().Email == "a@example.com", "Claude Code account info is Alice's");
            Check(JsonText.GetRaw(LiveCodeCfg(), "numStartups") == "43" && Projects(LiveCodeCfg()) == projectsBefore, "other .claude.json content preserved exactly");
            Check(File.ReadAllText(Path.Combine(desktop, "claude_desktop_config.json")) == Mcp, "MCP config never touched");
            Check(e.FindActive(e.ReadLive(false)).Id == alice.Id, "Alice shows as active");
            Check(SavedCodeCreds(bob).Contains("rt-B2"), "Bob's refreshed Claude Code token was kept");
            Check(File.ReadAllText(Path.Combine(AccountDir(bob), "desktop", "Network", "Cookies")).Contains("ENC-B2"), "Bob's newest Desktop session was kept");

            e.SwitchTo(Find(e, "Bob"), Quiet);
            Check(LiveCookies().Contains("ENC-B2") && LiveCreds().Contains("rt-B2"), "switching back gives Bob his newest session");
            Check(JsonText.GetString(LiveConfig(), "locale") == "fr-FR", "settings still kept");

            Section("A sign-in that isn't saved gets backed up, not lost");
            SignInDesktop("X");
            SignInCode("X");
            var before = new HashSet<string>(Directory.GetDirectories(BackupsDir));
            e.SwitchTo(Find(e, "Alice"), Quiet);
            var added = Directory.GetDirectories(BackupsDir).Where(d => !before.Contains(d)).ToList();
            Check(added.Count == 1, "one backup folder was created");
            if (added.Count == 1)
            {
                Check(File.ReadAllText(Path.Combine(added[0], "desktop", "Network", "Cookies")).Contains("ENC-X"), "unknown Desktop session is in the backup");
                Check(Encoding.UTF8.GetString(Secret.Read(Path.Combine(added[0], "code", "credentials.bin"))).Contains("at-X"), "unknown Claude Code login is in the backup");
            }
            Check(LiveCookies().Contains("ENC-A") && LiveCreds().Contains("at-A"), "then switched to Alice");
            Check(SavedCodeCreds(bob).Contains("rt-B2"), "Bob's saved sign-in was not overwritten");

            Section("Cancel a new sign-in");
            e.BeginNewSignIn(true, true, Quiet);
            SignInDesktop("C");
            e.CancelNewSignIn(Quiet);
            Check(e.Settings.Pending == null, "pending sign-in cleared");
            Check(LiveCookies().Contains("ENC-A") && LiveCreds().Contains("at-A")
                  && JsonText.GetString(LiveConfig(), "lastKnownAccountUuid") == "uuid-A", "Alice's sign-in is back");

            Section("Saving before signing in is refused");
            e.BeginNewSignIn(true, true, Quiet);
            bool refused = false;
            try { e.SaveCurrent(null, "Nobody", true, true, Quiet); }
            catch (UserMessageException) { refused = true; }
            Check(refused && e.Settings.Pending != null && e.Accounts.Count == 2, "nothing saved, still waiting for the sign-in");

            Section("New account signed in to Claude Desktop only");
            SignInDesktop("C");
            var carolResult = e.SaveCurrent(null, "Carol", true, true, Quiet);
            Check(carolResult.Account.HasDesktop && !carolResult.Account.HasCode, "Carol saved with Desktop only");
            Check(LiveCreds() != null && LiveCreds().Contains("at-A"), "Claude Code put back to the previous account");
            Check(carolResult.Notes.Count > 0, "the user is told what happened");

            Section("Rename and remove");
            e.Rename(Find(e, "Carol"), "  Carol W  ");
            e.Reload();
            Check(Find(e, "Carol W") != null, "rename");
            e.Delete(Find(e, "Carol W"));
            Check(e.Accounts.Count == 2 && Find(e, "Carol W") == null, "remove");

            Section("Files stay valid");
            byte[] cfgBytes = File.ReadAllBytes(codeCfg);
            Check(!(cfgBytes.Length >= 3 && cfgBytes[0] == 0xEF && cfgBytes[1] == 0xBB && cfgBytes[2] == 0xBF), ".claude.json has no BOM");
            Check(JsonText.Serializer().DeserializeObject(LiveCodeCfg()) is Dictionary<string, object>, ".claude.json is valid JSON");
            Check(JsonText.Serializer().DeserializeObject(LiveConfig()) is Dictionary<string, object>, "config.json is valid JSON");
            Check(new Engine().Accounts.Count == 2, "accounts load again from disk");
        }
    }
}
