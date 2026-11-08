using System;
using System.IO;

namespace ClaudeSwitch
{
    internal sealed class CodeIdentity
    {
        public bool SignedIn;
        public string Uuid;
        public string Email;
        public string Name;
    }

    /// <summary>
    /// Claude Code (the CLI). On Windows its sign-in is ~/.claude/.credentials.json plus the
    /// "oauthAccount" entry in ~/.claude.json (or both under CLAUDE_CONFIG_DIR when that is set).
    /// </summary>
    internal sealed class CodeClient
    {
        const string CredsBin = "credentials.bin";
        const string AccountBin = "oauthAccount.bin";
        const string AccountKey = "oauthAccount";

        public readonly string ConfigDir;
        public readonly string GlobalConfigPath;

        public CodeClient()
        {
            string custom = Env.Get("CLAUDE_CONFIG_DIR");
            ConfigDir = Env.Get("CLAUDESWITCH_CODE_DIR") ?? custom ?? Path.Combine(AppPaths.Home, ".claude");
            GlobalConfigPath = Env.Get("CLAUDESWITCH_CODE_CONFIG")
                ?? (custom != null ? Path.Combine(custom, ".claude.json") : Path.Combine(AppPaths.Home, ".claude.json"));
        }

        public string CredentialsPath { get { return Path.Combine(ConfigDir, ".credentials.json"); } }

        public bool Found { get { return Directory.Exists(ConfigDir) || File.Exists(GlobalConfigPath); } }

        public CodeIdentity ReadLive()
        {
            var id = new CodeIdentity();
            try
            {
                if (File.Exists(GlobalConfigPath)) Fill(id, JsonText.GetRaw(FileUtil.ReadText(GlobalConfigPath), AccountKey));
            }
            catch (Exception ex)
            {
                Log.Error("Reading Claude Code's account info failed", ex);
            }
            id.SignedIn = File.Exists(CredentialsPath);
            return id;
        }

        public static CodeIdentity ReadSnapshot(string codeDir)
        {
            var id = new CodeIdentity();
            try
            {
                byte[] raw = Secret.Read(Path.Combine(codeDir, AccountBin));
                if (raw != null) Fill(id, FileUtil.Utf8.GetString(raw));
                id.SignedIn = File.Exists(Path.Combine(codeDir, CredsBin));
            }
            catch (Exception ex)
            {
                Log.Error("Reading a saved Claude Code sign-in failed", ex);
            }
            return id;
        }

        static void Fill(CodeIdentity id, string rawAccount)
        {
            var d = JsonText.ParseObject(rawAccount);
            id.Uuid = JsonText.Str(d, "accountUuid");
            id.Email = JsonText.Str(d, "emailAddress");
            id.Name = JsonText.Str(d, "displayName");
        }

        /// <summary>Saves the current sign-in (encrypted) into destDir. Returns false if there is nothing to save.</summary>
        public bool CaptureTo(string destDir)
        {
            byte[] creds = File.Exists(CredentialsPath) ? FileUtil.ReadBytes(CredentialsPath) : null;
            string account = File.Exists(GlobalConfigPath) ? JsonText.GetRaw(FileUtil.ReadText(GlobalConfigPath), AccountKey) : null;
            if (creds == null && account == null) return false;

            Directory.CreateDirectory(destDir);
            if (creds != null) Secret.Write(Path.Combine(destDir, CredsBin), creds);
            if (account != null) Secret.Write(Path.Combine(destDir, AccountBin), FileUtil.Utf8.GetBytes(account));
            return true;
        }

        /// <summary>Puts a saved sign-in back. A missing folder means "signed out".</summary>
        public void RestoreFrom(string srcDir)
        {
            byte[] creds = Directory.Exists(srcDir) ? Secret.Read(Path.Combine(srcDir, CredsBin)) : null;
            byte[] account = Directory.Exists(srcDir) ? Secret.Read(Path.Combine(srcDir, AccountBin)) : null;

            if (creds != null) FileUtil.WriteBytesAtomic(CredentialsPath, creds);
            else FileUtil.Delete(CredentialsPath);
            SetAccount(account != null ? FileUtil.Utf8.GetString(account) : null);
        }

        /// <summary>Removes the local sign-in only (does not call Anthropic, so saved sessions keep working).</summary>
        public void SignOut()
        {
            FileUtil.Delete(CredentialsPath);
            SetAccount(null);
        }

        void SetAccount(string raw)
        {
            string text = File.Exists(GlobalConfigPath) ? FileUtil.ReadText(GlobalConfigPath) : null;
            if (text == null)
            {
                if (raw == null) return;
                text = "{}";
            }
            string updated = raw == null ? JsonText.Remove(text, AccountKey) : JsonText.SetRaw(text, AccountKey, raw);
            if (updated != text) FileUtil.WriteTextAtomic(GlobalConfigPath, updated);
        }
    }
}
