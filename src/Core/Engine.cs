using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeSwitch
{
    internal sealed class LiveState
    {
        public string DesktopUuid;
        public bool? DesktopSignedIn;
        public CodeIdentity Code = new CodeIdentity();

        /// <summary>Claude Desktop is signed in to a known account.</summary>
        public bool DesktopIn { get { return DesktopUuid != null && DesktopSignedIn != false; } }
    }

    internal sealed class SaveResult
    {
        public Account Account;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Saves, switches and restores Claude sign-ins.
    ///
    /// Rule of thumb: before anything replaces the live sign-in, the live sign-in is copied somewhere safe —
    /// into the saved account it belongs to (so refreshed tokens are kept) or into a backup folder.
    /// </summary>
    internal sealed class Engine
    {
        public AppSettings Settings { get; private set; }
        public List<Account> Accounts { get; private set; }
        public DesktopClient Desktop { get; private set; }
        public CodeClient Code { get; private set; }

        /// <summary>Set when Claude Desktop couldn't be reopened after an operation.</summary>
        public string LaunchError { get; set; }

        public Engine() { Reload(); }

        public void Reload()
        {
            Settings = Store.LoadSettings();
            Accounts = Store.LoadAccounts();
            Desktop = new DesktopClient(DesktopClient.Detect(Settings.DesktopDataDir), Settings.DesktopItems);
            Code = new CodeClient();
        }

        public bool DesktopOn { get { return Settings.ManageDesktop && Desktop.Found; } }
        public bool CodeOn { get { return Settings.ManageCode && Code.Found; } }

        string PendingDir { get { return Settings.Pending != null ? Settings.Pending.BackupDir : null; } }

        public void SaveSettings() { Store.SaveSettings(Settings); }

        // ---- reading the current state ----------------------------------------------------------

        public LiveState ReadLive(bool checkCookies)
        {
            var s = new LiveState();
            if (Desktop.Found)
            {
                s.DesktopUuid = Desktop.ReadAccountUuid();
                s.DesktopSignedIn = Desktop.HasAuthTokens() ? true : checkCookies ? Desktop.HasSessionCookie() : (bool?)null;
            }
            if (Code.Found) s.Code = Code.ReadLive();
            return s;
        }

        /// <summary>The saved account that is signed in right now, if any.</summary>
        public Account FindActive(LiveState live)
        {
            if (Settings.Pending != null) return null;
            // When Claude Desktop is signed in, it decides which account is active.
            if (DesktopOn && live.DesktopIn)
                return Accounts.FirstOrDefault(x => x.HasDesktop && x.DesktopUuid == live.DesktopUuid);
            if (CodeOn && live.Code.SignedIn && live.Code.Uuid != null)
                return Accounts.FirstOrDefault(x => x.HasCode && x.CodeUuid == live.Code.Uuid);
            if (live.DesktopUuid == null && live.Code.Uuid == null)
                return Accounts.FirstOrDefault(x => x.Id == Settings.ActiveAccountId);
            return null;
        }

        /// <summary>A saved account that matches what is signed in now (to avoid saving the same account twice).</summary>
        public Account FindSaved(LiveState live)
        {
            // When Claude Desktop is signed in, it decides which saved account this is.
            if (DesktopOn && live.DesktopIn)
                return Accounts.FirstOrDefault(x => x.DesktopUuid == live.DesktopUuid);
            if (CodeOn && live.Code.SignedIn && live.Code.Uuid != null)
                return Accounts.FirstOrDefault(x => x.CodeUuid == live.Code.Uuid || x.DesktopUuid == live.Code.Uuid);
            return null;
        }

        // ---- operations --------------------------------------------------------------------------

        /// <summary>Saves what is signed in now as a new account, or into <paramref name="existing"/>.</summary>
        public SaveResult SaveCurrent(Account existing, string name, bool includeDesktop, bool includeCode, Action<string> progress)
        {
            var result = new SaveResult();
            var pending = Settings.Pending;
            bool isNew = existing == null;
            var acc = existing ?? new Account { Id = Util.NewId(), Color = NextColor(), CreatedAt = Util.Now(), Order = NextOrder() };
            if (!string.IsNullOrWhiteSpace(name)) acc.Name = name.Trim();
            if (string.IsNullOrWhiteSpace(acc.Name)) acc.Name = "Account " + (Accounts.Count + 1);

            bool desktopSaved = false, codeSaved = false, closed = false, reopen = false;
            string image = null;
            try
            {
                if (includeDesktop && Desktop.Found)
                {
                    image = DesktopProcess.RunningImage();
                    // Claude has to be force-closed; give a fresh sign-in time to reach the disk first.
                    if (image != null) Desktop.WaitForFreshSignIn(progress);
                    reopen = DesktopProcess.Close(progress);
                    closed = true;
                    progress("Saving Claude Desktop sign-in…");
                    if (Desktop.IsSignedIn() == false)
                    {
                        result.Notes.Add("Claude Desktop isn't signed in, so its sign-in wasn't saved.");
                    }
                    else
                    {
                        CaptureDesktop(acc);
                        acc.HasDesktop = true;
                        acc.DesktopUuid = Desktop.ReadAccountUuid();
                        desktopSaved = true;
                        if (Desktop.HasSessionCookie() == false)
                            result.Notes.Add("Claude hadn't finished saving this sign-in yet. If Claude asks you to sign in after you switch to “"
                                + acc.Name + "”, sign in once, then choose ⋯ → Update saved sign-in.");
                    }
                }

                if (includeCode && Code.Found)
                {
                    progress("Saving Claude Code sign-in…");
                    var id = Code.ReadLive();
                    // One saved account holds one Claude account: never mix in Claude Code from another account.
                    bool otherAccount = acc.HasDesktop && acc.DesktopUuid != null && id.Uuid != null && id.Uuid != acc.DesktopUuid;
                    if (!id.SignedIn)
                    {
                        result.Notes.Add("Claude Code isn't signed in, so its sign-in wasn't saved.");
                    }
                    else if (otherAccount)
                    {
                        result.Notes.Add("Claude Code is signed in to a different Claude account" + (id.Email != null ? " (" + id.Email + ")" : "")
                            + ", so it wasn't added to “" + acc.Name + "”.");
                    }
                    else if (CaptureCode(acc))
                    {
                        acc.HasCode = true;
                        acc.CodeUuid = id.Uuid;
                        acc.CodeEmail = id.Email;
                        codeSaved = true;
                    }
                }

                if (!desktopSaved && !codeSaved)
                {
                    throw new UserMessageException(pending != null
                        ? "It looks like you haven't signed in yet. Sign in to Claude with the other account, wait a few seconds, then click “Save account” again."
                        : "Claude isn't signed in, so there was nothing to save. Sign in to Claude first.");
                }

                if (pending != null)
                {
                    // Anything that wasn't signed in again during the new sign-in goes back to how it was.
                    if (pending.Desktop && !desktopSaved && Desktop.Found)
                    {
                        if (!closed)
                        {
                            image = DesktopProcess.RunningImage();
                            DesktopProcess.Close(progress);
                            closed = true;
                        }
                        string b = Path.Combine(pending.BackupDir, "desktop");
                        if (Directory.Exists(b))
                        {
                            Desktop.RestoreFrom(b);
                            result.Notes.Add("Claude Desktop was switched back to the account you had before.");
                        }
                        reopen = true;
                    }
                    if (pending.Code && !codeSaved && Code.Found && !Code.ReadLive().SignedIn)
                    {
                        Code.RestoreFrom(Path.Combine(pending.BackupDir, "code"));
                        result.Notes.Add("Claude Code was switched back to the account you had before.");
                    }
                    Settings.Pending = null;
                }

                if (acc.HasCode && (!acc.HasDesktop || acc.DesktopUuid == null || acc.DesktopUuid == acc.CodeUuid))
                    acc.Email = acc.CodeEmail;
                acc.LastUsedAt = Util.Now();
                Store.SaveAccount(acc);
                Settings.ActiveAccountId = acc.Id;
                SaveSettings();
                Accounts = Store.LoadAccounts();
                result.Account = acc;
                Log.Info((isNew ? "Saved new account " : "Updated account ") + acc.Id);
                return result;
            }
            finally
            {
                if (reopen) TryLaunch(image, progress);
            }
        }

        /// <summary>The one-click switch.</summary>
        public void SwitchTo(Account target, Action<string> progress)
        {
            bool doDesktop = DesktopOn && target.HasDesktop && Directory.Exists(DesktopSnap(target));
            bool doCode = CodeOn && target.HasCode && Directory.Exists(CodeSnap(target));
            if (!doDesktop && !doCode)
                throw new UserMessageException("There's no saved sign-in to switch to for “" + target.Name + "”. Use “Update saved sign-in” to save it again.");

            string backup = null;
            Func<string> backupDir = () => backup ?? (backup = Store.NewBackupDir("before-switch", PendingDir));
            string image = null;
            bool wasRunning = false;
            Log.Info("Switching to account " + target.Id);
            try
            {
                if (doDesktop)
                {
                    image = DesktopProcess.RunningImage();
                    wasRunning = DesktopProcess.Close(progress);
                    progress("Saving the current sign-in…");
                    Account owner = Desktop.HasSessionCookie() == true ? DesktopOwner(Desktop.ReadAccountUuid()) : null;
                    string rollback;
                    if (owner != null)
                    {
                        CaptureDesktop(owner);
                        rollback = DesktopSnap(owner);
                    }
                    else
                    {
                        rollback = Path.Combine(backupDir(), "desktop");
                        Desktop.CaptureTo(rollback);
                    }

                    if (owner == null || owner.Id != target.Id)
                    {
                        progress("Signing in as " + target.Name + "…");
                        try
                        {
                            Desktop.RestoreFrom(DesktopSnap(target));
                        }
                        catch
                        {
                            TryRestoreDesktop(rollback);
                            throw;
                        }
                    }
                }

                if (doCode)
                {
                    progress("Switching Claude Code…");
                    var id = Code.ReadLive();
                    Account owner = id.SignedIn ? CodeOwner(id.Uuid) : null;
                    string rollback = null;
                    if (owner != null)
                    {
                        CaptureCode(owner);
                        rollback = CodeSnap(owner);
                    }
                    else if (id.SignedIn || id.Uuid != null)
                    {
                        rollback = Path.Combine(backupDir(), "code");
                        Code.CaptureTo(rollback);
                    }

                    if (owner == null || owner.Id != target.Id)
                    {
                        try
                        {
                            Code.RestoreFrom(CodeSnap(target));
                        }
                        catch
                        {
                            TryRestoreCode(rollback);
                            throw;
                        }
                    }
                }

                target.LastUsedAt = Util.Now();
                Store.SaveAccount(target);
                Settings.ActiveAccountId = target.Id;
                SaveSettings();
            }
            finally
            {
                if (doDesktop && (wasRunning || Settings.ReopenDesktop)) TryLaunch(image, progress);
            }
        }

        /// <summary>Keeps the current sign-in safe, then signs Claude out locally so another account can sign in.</summary>
        public void BeginNewSignIn(bool desktop, bool code, Action<string> progress)
        {
            desktop = desktop && DesktopOn;
            code = code && CodeOn;
            if (!desktop && !code) throw new UserMessageException("Turn on Claude Desktop or Claude Code in Settings first.");
            if (Settings.Pending != null) throw new UserMessageException("You're already signing in to a new account.");

            string backup = Store.NewBackupDir("before-new-sign-in", null);
            string image = null;
            bool desktopOut = false, codeOut = false;
            Log.Info("Starting a new sign-in (backup " + Path.GetFileName(backup) + ")");
            try
            {
                if (desktop)
                {
                    image = DesktopProcess.RunningImage();
                    DesktopProcess.Close(progress);
                    progress("Keeping your current sign-in safe…");
                    Desktop.CaptureTo(Path.Combine(backup, "desktop"));
                    Account owner = Desktop.HasSessionCookie() == true ? DesktopOwner(Desktop.ReadAccountUuid()) : null;
                    if (owner != null) CaptureDesktop(owner);
                    progress("Signing Claude Desktop out…");
                    desktopOut = true;
                    Desktop.SignOut();
                }
                if (code)
                {
                    progress("Signing Claude Code out…");
                    Code.CaptureTo(Path.Combine(backup, "code"));
                    var id = Code.ReadLive();
                    Account owner = id.SignedIn ? CodeOwner(id.Uuid) : null;
                    if (owner != null) CaptureCode(owner);
                    codeOut = true;
                    Code.SignOut();
                }
                Settings.Pending = new PendingSignIn { BackupDir = backup, Desktop = desktop, Code = code, StartedAt = Util.Now() };
                SaveSettings();
            }
            catch
            {
                if (desktopOut) TryRestoreDesktop(Path.Combine(backup, "desktop"));
                if (codeOut) TryRestoreCode(Path.Combine(backup, "code"));
                throw;
            }
            finally
            {
                if (desktop) TryLaunch(image, progress);
            }
        }

        /// <summary>Gives up on the new sign-in and puts back the sign-in from before it started.</summary>
        public void CancelNewSignIn(Action<string> progress)
        {
            var p = Settings.Pending;
            if (p == null) return;
            if (!Directory.Exists(p.BackupDir))
            {
                Settings.Pending = null;
                SaveSettings();
                throw new UserMessageException("The copy of your previous sign-in is missing, so it can't be put back. Please sign in to Claude again.");
            }

            string image = null;
            bool reopen = false;
            try
            {
                if (p.Desktop && Desktop.Found)
                {
                    image = DesktopProcess.RunningImage();
                    reopen = DesktopProcess.Close(progress) || Settings.ReopenDesktop;
                    progress("Putting back your previous sign-in…");
                    Desktop.RestoreFrom(Path.Combine(p.BackupDir, "desktop"));
                }
                if (p.Code && Code.Found) Code.RestoreFrom(Path.Combine(p.BackupDir, "code"));
                Settings.Pending = null;
                SaveSettings();
                Log.Info("New sign-in cancelled; previous sign-in restored");
            }
            finally
            {
                if (reopen) TryLaunch(image, progress);
            }
        }

        public void Rename(Account a, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            a.Name = name.Trim();
            Store.SaveAccount(a);
        }

        /// <summary>Forgets a saved account. Does not sign anyone out.</summary>
        public void Delete(Account a)
        {
            Store.DeleteAccount(a.Id);
            if (Settings.ActiveAccountId == a.Id)
            {
                Settings.ActiveAccountId = null;
                SaveSettings();
            }
            Accounts = Store.LoadAccounts();
            Log.Info("Removed account " + a.Id);
        }

        /// <summary>
        /// Version 1.0.0 could save Claude Desktop and Claude Code from two different Claude accounts into one saved
        /// account. Moves each such Claude Code sign-in to the account it really belongs to. Returns what was fixed.
        /// </summary>
        public List<string> RepairMixedAccounts()
        {
            var notes = new List<string>();
            foreach (var a in Accounts.ToList())
            {
                if (!a.HasDesktop || !a.HasCode || a.DesktopUuid == null || a.CodeUuid == null || a.DesktopUuid == a.CodeUuid) continue;
                string from = CodeSnap(a);
                if (!Directory.Exists(from)) continue;

                string who = a.CodeEmail ?? "another account";
                var owner = Accounts.FirstOrDefault(b => b.Id != a.Id && b.HasDesktop && b.DesktopUuid == a.CodeUuid);
                if (owner != null && !owner.HasCode)
                {
                    MoveDir(from, CodeSnap(owner));
                    owner.HasCode = true;
                    owner.CodeUuid = a.CodeUuid;
                    owner.CodeEmail = a.CodeEmail;
                    owner.Email = a.CodeEmail;
                    Store.SaveAccount(owner);
                    notes.Add("The Claude Code sign-in for " + who + " was saved in “" + a.Name + "”, but it belongs to “" + owner.Name + "”. It's now saved there.");
                }
                else if (owner != null)
                {
                    MoveDir(from, Path.Combine(Store.NewBackupDir("mixed-code", PendingDir), "code"));
                    notes.Add("Removed an extra Claude Code sign-in (" + who + ") from “" + a.Name + "”. “" + owner.Name + "” already has it.");
                }
                else
                {
                    var own = new Account
                    {
                        Id = Util.NewId(),
                        Name = a.CodeEmail ?? "Claude Code account",
                        Color = NextColor(),
                        CreatedAt = Util.Now(),
                        Order = NextOrder(),
                        HasCode = true,
                        CodeUuid = a.CodeUuid,
                        CodeEmail = a.CodeEmail,
                        Email = a.CodeEmail
                    };
                    MoveDir(from, CodeSnap(own));
                    Store.SaveAccount(own);
                    Accounts.Add(own);
                    notes.Add("The Claude Code sign-in for " + who + " was saved in “" + a.Name + "”, but it's a different Claude account. It's now its own account.");
                }

                a.HasCode = false;
                a.CodeUuid = null;
                a.CodeEmail = null;
                Store.SaveAccount(a);
                Log.Info("Repaired mixed account " + a.Id);
            }
            if (notes.Count > 0) Accounts = Store.LoadAccounts();
            return notes;
        }

        // ---- helpers -----------------------------------------------------------------------------

        static void MoveDir(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            FileUtil.Delete(to);
            FileUtil.Retry(() => Directory.Move(from, to));
        }

        static string DesktopSnap(Account a) { return Path.Combine(Store.AccountDir(a.Id), "desktop"); }
        static string CodeSnap(Account a) { return Path.Combine(Store.AccountDir(a.Id), "code"); }

        void CaptureDesktop(Account a)
        {
            string snap = DesktopSnap(a);
            string tmp = snap + ".tmp";
            FileUtil.Delete(tmp);
            Desktop.CaptureTo(tmp);
            FileUtil.SwapIn(tmp, snap);
        }

        bool CaptureCode(Account a)
        {
            string snap = CodeSnap(a);
            string tmp = snap + ".tmp";
            FileUtil.Delete(tmp);
            if (!Code.CaptureTo(tmp))
            {
                FileUtil.Delete(tmp);
                return false;
            }
            FileUtil.SwapIn(tmp, snap);
            return true;
        }

        Account DesktopOwner(string uuid)
        {
            if (uuid != null) return Accounts.FirstOrDefault(a => a.HasDesktop && a.DesktopUuid == uuid);
            // Older Claude versions don't record the account id; trust the account we last switched to.
            return Accounts.FirstOrDefault(a => a.Id == Settings.ActiveAccountId && a.HasDesktop && a.DesktopUuid == null);
        }

        Account CodeOwner(string uuid)
        {
            return uuid == null ? null : Accounts.FirstOrDefault(a => a.HasCode && a.CodeUuid == uuid);
        }

        void TryRestoreDesktop(string dir)
        {
            try { Desktop.RestoreFrom(dir); }
            catch (Exception ex) { Log.Error("Rolling back Claude Desktop failed (backup: " + dir + ")", ex); }
        }

        void TryRestoreCode(string dir)
        {
            try { Code.RestoreFrom(dir); }
            catch (Exception ex) { Log.Error("Rolling back Claude Code failed (backup: " + dir + ")", ex); }
        }

        void TryLaunch(string image, Action<string> progress)
        {
            try
            {
                progress("Opening Claude…");
                string before = Settings.DesktopAppId;
                DesktopProcess.Launch(Settings, image);
                if (Settings.DesktopAppId != before) SaveSettings();
            }
            catch (Exception ex)
            {
                Log.Error("Reopening Claude Desktop failed", ex);
                LaunchError = ex.Message;
            }
        }

        string NextColor()
        {
            foreach (string c in Store.Palette)
                if (!Accounts.Any(a => a.Color == c)) return c;
            return Store.Palette[Accounts.Count % Store.Palette.Length];
        }

        int NextOrder() { return Accounts.Count == 0 ? 0 : Accounts.Max(a => a.Order) + 1; }
    }
}
