using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudeSwitch
{
    /// <summary>One row in the account list.</summary>
    public sealed class AccountVm
    {
        public Account Model { get; set; }
        public string Name { get; set; }
        public string Subtitle { get; set; }
        public string Initial { get; set; }
        public Brush AvatarBrush { get; set; }
        public bool IsActive { get; set; }
        public Visibility ActiveVisibility { get { return IsActive ? Visibility.Visible : Visibility.Collapsed; } }
        public string Tip { get; set; }
    }

    internal enum BtnKind { Primary, Secondary, Danger }

    internal sealed class DlgBtn
    {
        public readonly string Id;
        public readonly string Label;
        public readonly BtnKind Kind;

        public DlgBtn(string id, string label, BtnKind kind)
        {
            Id = id;
            Label = label;
            Kind = kind;
        }
    }

    internal sealed class Win32Owner : System.Windows.Forms.IWin32Window
    {
        readonly IntPtr handle;
        public Win32Owner(IntPtr handle) { this.handle = handle; }
        public IntPtr Handle { get { return handle; } }
    }

    internal sealed class MainWindowController
    {
        readonly Engine engine;
        readonly Window w;
        readonly DispatcherTimer toastTimer, pendingTimer, updateTimer;
        readonly bool afterUpdate;
        bool busy, loadingSettings, checkingUpdate;
        string lastListKey;
        string updateStatus = "";
        UpdateInfo installedUpdate;
        Account menuAccount;
        TaskCompletionSource<string> dialogTcs;
        string dialogPrimaryId;

        public Window Window { get { return w; } }

        public MainWindowController(bool afterUpdate)
        {
            this.afterUpdate = afterUpdate;
            engine = new Engine();
            Theme.Apply(engine.Settings.Theme);
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeSwitch.MainWindow.xaml"))
                w = (Window)XamlReader.Load(s);

            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            toastTimer.Tick += (s, e) =>
            {
                toastTimer.Stop();
                El<Border>("Toast").Visibility = Visibility.Collapsed;
            };
            // While adding an account, keep the "are you signed in yet?" line current.
            pendingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            pendingTimer.Tick += (s, e) => { if (!busy && dialogTcs == null) UpdatePendingStatus(); };
            updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            updateTimer.Tick += async (s, e) => await CheckForUpdates(false);

            string version = Updater.CurrentText;
            El<TextBlock>("VersionText").Text = "v" + version;
            El<TextBlock>("AboutText").Text = "ClaudeSwitch " + version + " · free and open source (MIT)\nNot made by or affiliated with Anthropic.";
            Wire();
            UpdateThemeButton();
            Refresh();
            w.Loaded += async (s, e) => await OnLoaded();
        }

        async Task OnLoaded()
        {
            if (afterUpdate) Toast("Updated to ClaudeSwitch " + Updater.CurrentText);
            await RunRepair();
            await Task.Delay(TimeSpan.FromSeconds(3));
            await CheckForUpdates(false);
            updateTimer.Start();
        }

        T El<T>(string name) where T : class
        {
            var el = w.FindName(name) as T;
            if (el == null) throw new InvalidOperationException("UI element missing: " + name);
            return el;
        }

        Style Res(string key) { return (Style)w.FindResource(key); }

        Account Fresh(Account a) { return a == null ? null : engine.Accounts.FirstOrDefault(x => x.Id == a.Id); }

        void SaveSettingsQuiet()
        {
            try { engine.SaveSettings(); }
            catch (Exception ex) { Log.Error("Saving settings failed", ex); }
        }

        // ---- wiring -------------------------------------------------------------------------------

        void Wire()
        {
            El<Button>("CloseBtn").Click += (s, e) => w.Close();
            El<Button>("MinBtn").Click += (s, e) => w.WindowState = WindowState.Minimized;
            El<Button>("ThemeBtn").Click += (s, e) =>
            {
                engine.Settings.Theme = Theme.IsDark ? "light" : "dark";
                SaveSettingsQuiet();
                ApplyTheme();
            };
            El<Button>("SettingsBtn").Click += (s, e) => ShowSettings(true);
            El<Button>("SettingsBackBtn").Click += (s, e) => ShowSettings(false);
            El<Button>("AddBtn").Click += async (s, e) => await AddAccountFlow();
            El<Button>("PendingSaveBtn").Click += async (s, e) => await SaveFlow(null);
            El<Button>("PendingCancelBtn").Click += async (s, e) => await CancelPendingFlow();
            El<Button>("PendingTerminalBtn").Click += (s, e) => OpenCodeLogin();
            El<ItemsControl>("AccountList").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnListClick));

            El<Button>("MenuRename").Click += async (s, e) => { CloseMenu(); await RenameFlow(menuAccount); };
            El<Button>("MenuUpdate").Click += async (s, e) => { CloseMenu(); await SaveFlow(menuAccount); };
            El<Button>("MenuDelete").Click += async (s, e) => { CloseMenu(); await DeleteFlow(menuAccount); };

            ThemeRadio("ThemeSystem", "system");
            ThemeRadio("ThemeLight", "light");
            ThemeRadio("ThemeDark", "dark");
            Toggle("ManageDesktopToggle", v => engine.Settings.ManageDesktop = v);
            Toggle("ReopenToggle", v => engine.Settings.ReopenDesktop = v);
            Toggle("ManageCodeToggle", v => engine.Settings.ManageCode = v);
            Toggle("AutoUpdateToggle", v => engine.Settings.AutoUpdate = v);
            El<Button>("CheckUpdatesBtn").Click += async (s, e) => await CheckForUpdates(true);
            El<Button>("UpdateRestartBtn").Click += (s, e) => RestartIntoUpdate();
            El<Button>("DesktopFolderBtn").Click += async (s, e) => await PickDesktopFolder();
            El<Button>("DesktopAutoBtn").Click += (s, e) =>
            {
                engine.Settings.DesktopDataDir = null;
                SaveSettingsQuiet();
                Refresh();
                LoadSettingsUi();
            };
            El<Button>("OpenDataBtn").Click += (s, e) => OpenFolder(AppPaths.DataDir);

            w.SourceInitialized += (s, e) => Theme.ApplyToWindow(w);
            w.Activated += (s, e) => { if (!busy && dialogTcs == null) Refresh(); };
            w.StateChanged += (s, e) =>
                El<Grid>("Root").Margin = w.WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            w.PreviewKeyDown += OnKey;
            w.Closing += (s, e) => { if (busy) e.Cancel = true; };
            SystemEvents.UserPreferenceChanged += OnSystemPrefs;
            w.Closed += (s, e) => SystemEvents.UserPreferenceChanged -= OnSystemPrefs;
        }

        void ThemeRadio(string name, string mode)
        {
            El<RadioButton>(name).Checked += (s, e) =>
            {
                if (loadingSettings) return;
                engine.Settings.Theme = mode;
                SaveSettingsQuiet();
                ApplyTheme();
            };
        }

        void Toggle(string name, Action<bool> set)
        {
            var cb = El<CheckBox>(name);
            RoutedEventHandler handler = (s, e) =>
            {
                if (loadingSettings) return;
                set(cb.IsChecked == true);
                SaveSettingsQuiet();
                Refresh();
                LoadSettingsUi();
            };
            cb.Checked += handler;
            cb.Unchecked += handler;
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (El<Grid>("DialogLayer").Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    CloseDialog(null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && dialogPrimaryId != null && !(Keyboard.FocusedElement is Button))
                {
                    CloseDialog(dialogPrimaryId);
                    e.Handled = true;
                }
                return;
            }
            if (busy) return;
            if (e.Key == Key.Escape && El<Grid>("SettingsLayer").Visibility == Visibility.Visible)
            {
                ShowSettings(false);
                e.Handled = true;
            }
        }

        // ---- theme --------------------------------------------------------------------------------

        void ApplyTheme()
        {
            Theme.Apply(engine.Settings.Theme);
            Theme.ApplyToWindow(w);
            UpdateThemeButton();
            if (El<Grid>("SettingsLayer").Visibility == Visibility.Visible) LoadSettingsUi();
        }

        void UpdateThemeButton()
        {
            var b = El<Button>("ThemeBtn");
            b.Content = Theme.IsDark ? "" : "";
            b.ToolTip = Theme.IsDark ? "Switch to light mode" : "Switch to dark mode";
        }

        void OnSystemPrefs(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General) return;
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (engine.Settings.Theme != "light" && engine.Settings.Theme != "dark") ApplyTheme();
            }));
        }

        // ---- main view ----------------------------------------------------------------------------

        void Refresh()
        {
            try { engine.Reload(); }
            catch (Exception ex) { Log.Error("Reload failed", ex); }

            Account active = null;
            try { active = engine.FindActive(engine.ReadLive(false)); }
            catch (Exception ex) { Log.Error("Reading the current sign-in failed", ex); }

            var items = engine.Accounts.Select(a => MakeVm(a, active != null && active.Id == a.Id)).ToList();
            // Only rebuild the list when something changed, so a click that activates the window isn't lost.
            string key = string.Join("|", items.Select(v => v.Model.Id + "/" + v.Name + "/" + v.Subtitle + "/" + v.IsActive + "/" + v.Model.Color));
            if (key != lastListKey)
            {
                El<ItemsControl>("AccountList").ItemsSource = items;
                lastListKey = key;
            }
            El<Border>("EmptyState").Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            El<TextBlock>("CountText").Text = items.Count == 0 ? "" : items.Count == 1 ? "1 account" : items.Count + " accounts";

            var p = engine.Settings.Pending;
            El<Border>("PendingBanner").Visibility = p != null ? Visibility.Visible : Visibility.Collapsed;
            El<Button>("AddBtn").Visibility = p != null ? Visibility.Collapsed : Visibility.Visible;
            if (p != null)
            {
                El<TextBlock>("PendingText").Text = PendingSteps(p);
                El<Button>("PendingTerminalBtn").Visibility = p.Code ? Visibility.Visible : Visibility.Collapsed;
                UpdatePendingStatus();
                if (!pendingTimer.IsEnabled) pendingTimer.Start();
            }
            else if (pendingTimer.IsEnabled)
            {
                pendingTimer.Stop();
            }

            SetStatus("Desktop", "Claude Desktop", engine.Desktop.Found, engine.Settings.ManageDesktop, engine.Desktop.DataDir);
            SetStatus("Code", "Claude Code", engine.Code.Found, engine.Settings.ManageCode, engine.Code.ConfigDir);
        }

        static string PendingSteps(PendingSignIn p)
        {
            var steps = new List<string>();
            if (p.Desktop) steps.Add("Sign in to Claude Desktop with the other account.");
            if (p.Code) steps.Add("For Claude Code, click “Claude Code login” (or run claude in a terminal) and sign in.");
            steps.Add("Come back here and click “Save account”.");
            return string.Join("\n", steps.Select((t, i) => (i + 1) + ". " + t));
        }

        /// <summary>While adding an account, shows whether Claude Desktop is signed in to a new account yet.</summary>
        void UpdatePendingStatus()
        {
            var status = El<TextBlock>("PendingStatus");
            var p = engine.Settings.Pending;
            if (p == null || !p.Desktop || !engine.Desktop.Found)
            {
                status.Visibility = Visibility.Collapsed;
                return;
            }
            string uuid = engine.Desktop.ReadAccountUuid();
            bool tokens = engine.Desktop.HasAuthTokens();
            Account saved = uuid == null ? null : engine.Accounts.FirstOrDefault(a => a.HasDesktop && a.DesktopUuid == uuid);
            string before = DesktopClient.ReadUuidFrom(System.IO.Path.Combine(p.BackupDir, "desktop"));
            string text, color;
            if (uuid == null && !tokens)
            {
                text = "Waiting for you to sign in to Claude Desktop…";
                color = "TextMuted";
            }
            else if (saved != null)
            {
                text = "Claude Desktop is signed in to “" + saved.Name + "”, which is already saved. Sign in with your other account.";
                color = "Danger";
            }
            else if (uuid != null && uuid == before)
            {
                text = "Claude Desktop is still signed in to your previous account. Sign in with your other account.";
                color = "Danger";
            }
            else
            {
                text = "✓ Signed in to a new account. Click “Save account”.";
                color = "Success";
            }
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, color);
            status.Visibility = Visibility.Visible;
        }

        void SetStatus(string prefix, string name, bool found, bool on, string path)
        {
            El<Ellipse>(prefix + "Dot").SetResourceReference(Shape.FillProperty, found && on ? "Success" : "Off");
            El<TextBlock>(prefix + "Label").Text = name + (!found ? " · not found" : on ? "" : " · off");
            El<StackPanel>(prefix + "Status").ToolTip = !found ? name + " wasn't found on this PC."
                : !on ? name + " switching is turned off in Settings."
                : name + ": " + path;
        }

        static AccountVm MakeVm(Account a, bool active)
        {
            string parts = a.HasDesktop && a.HasCode ? "Desktop + Code" : a.HasDesktop ? "Desktop" : a.HasCode ? "Code" : "nothing saved";
            string subtitle = string.IsNullOrEmpty(a.Email) ? parts : a.Email + "  ·  " + parts;
            Brush brush;
            try
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(a.Color ?? "#7C8796"));
                brush.Freeze();
            }
            catch
            {
                brush = Brushes.SlateGray;
            }
            string initial = "?";
            foreach (char ch in a.Name ?? "")
            {
                if (!char.IsLetterOrDigit(ch)) continue;
                initial = char.ToUpperInvariant(ch).ToString();
                break;
            }
            return new AccountVm
            {
                Model = a,
                Name = a.Name,
                Subtitle = subtitle,
                Initial = initial,
                AvatarBrush = brush,
                IsActive = active,
                Tip = active ? "You're using this account" : "Click to switch to this account"
            };
        }

        async void OnListClick(object sender, RoutedEventArgs e)
        {
            var btn = e.OriginalSource as Button;
            var vm = btn == null ? null : btn.DataContext as AccountVm;
            if (vm == null) return;
            e.Handled = true;
            if ((btn.Tag as string) == "menu") OpenMenu(btn, vm.Model);
            else await SwitchFlow(vm.Model);
        }

        void OpenMenu(Button anchor, Account a)
        {
            menuAccount = a;
            var menu = El<Popup>("CardMenu");
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = anchor.ActualWidth - 210; // right-align the 200px menu (inside a 10px shadow margin)
            menu.IsOpen = true;
        }

        void CloseMenu() { El<Popup>("CardMenu").IsOpen = false; }

        void Toast(string message)
        {
            El<TextBlock>("ToastText").Text = message;
            var t = El<Border>("Toast");
            t.Visibility = Visibility.Visible;
            t.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            toastTimer.Stop();
            toastTimer.Start();
        }

        // ---- flows --------------------------------------------------------------------------------

        async Task SwitchFlow(Account a)
        {
            if (busy) return;
            a = Fresh(a);
            if (a == null)
            {
                Refresh();
                return;
            }
            if (engine.Settings.Pending != null)
            {
                await Alert("Finish adding first", "You're in the middle of adding an account. Click “Save account” or “Cancel” first.");
                return;
            }
            var active = engine.FindActive(engine.ReadLive(false));
            if (active != null && active.Id == a.Id)
            {
                Toast("You're already using " + a.Name);
                return;
            }
            if (engine.DesktopOn && a.HasDesktop && !engine.Settings.SeenSwitchNotice)
            {
                string answer = await OpenDialog("Switch to " + a.Name + "?", body =>
                {
                    body.Children.Add(Para("Claude Desktop will close and open again, signed in as “" + a.Name + "”. It takes a few seconds.", false));
                    body.Children.Add(Para("You'll only see this message once.", true));
                }, new DlgBtn("cancel", "Cancel", BtnKind.Secondary), new DlgBtn("ok", "Switch", BtnKind.Primary));
                if (answer != "ok") return;
                engine.Settings.SeenSwitchNotice = true;
                SaveSettingsQuiet();
            }

            bool ok = await RunBusy("Switching to " + a.Name + "…", progress => engine.SwitchTo(a, progress));
            if (!ok) return;
            CodeIdentity codeNow = engine.CodeOn ? engine.ReadLive(false).Code : null;
            if (codeNow != null && codeNow.SignedIn && !a.HasCode)
                Toast("Now using " + a.Name + ". Claude Code stays on " + (codeNow.Email ?? "its own account")
                    + ", because no Claude Code sign-in is saved for " + a.Name + ".");
            else
                Toast("Now using " + a.Name + (engine.CodeOn && a.HasCode ? ". Restart any open Claude Code sessions." : "."));
        }

        async Task AddAccountFlow()
        {
            if (busy) return;
            if (!engine.DesktopOn && !engine.CodeOn)
            {
                await Alert("Claude wasn't found", "ClaudeSwitch couldn't find Claude Desktop or Claude Code on this PC, or both are turned off in Settings.");
                return;
            }
            var live = engine.ReadLive(false);
            string choice = await OpenDialog("Add account", body =>
            {
                body.Children.Add(Option("", "Save the account I'm using now", DescribeLive(live), "current"));
                body.Children.Add(Option("", "Sign in with another account", "Claude opens signed out so you can log in with a different account.", "new"));
                body.Children.Add(Note("Tip: don't use “Log out” inside Claude for this. Logging out ends that session, so its saved copy would stop working.", false));
            }, new DlgBtn("cancel", "Cancel", BtnKind.Secondary));

            if (choice == "current") await SaveFlow(null);
            else if (choice == "new") await NewSignInFlow();
        }

        /// <summary>What Claude Desktop and Claude Code are each signed in to right now, one line each.</summary>
        string DescribeLive(LiveState live)
        {
            var lines = new List<string>();
            if (engine.DesktopOn) lines.Add("Claude Desktop: " + DesktopWho(live));
            if (engine.CodeOn) lines.Add("Claude Code: " + CodeWho(live));
            return lines.Count == 0 ? "Nothing to save." : string.Join("\n", lines);
        }

        string DesktopWho(LiveState live)
        {
            if (!live.DesktopIn) return "not signed in";
            Account saved = engine.Accounts.FirstOrDefault(a => a.HasDesktop && a.DesktopUuid == live.DesktopUuid);
            if (saved != null) return "“" + saved.Name + "” (already saved)";
            if (live.Code.Uuid == live.DesktopUuid && live.Code.Email != null) return live.Code.Email + " (not saved yet)";
            return "an account that isn't saved yet";
        }

        string CodeWho(LiveState live)
        {
            if (!live.Code.SignedIn) return "not signed in";
            string who = live.Code.Email ?? "signed in";
            if (!live.DesktopIn || live.Code.Uuid == null || live.Code.Uuid == live.DesktopUuid) return who;
            Account saved = engine.Accounts.FirstOrDefault(a => (a.HasCode && a.CodeUuid == live.Code.Uuid) || (a.HasDesktop && a.DesktopUuid == live.Code.Uuid));
            return who + (saved != null ? " (“" + saved.Name + "”, a different account)" : " (a different account)");
        }

        string SuggestName(LiveState live)
        {
            if (live.Code.SignedIn && live.Code.Email != null && (!live.DesktopIn || live.DesktopUuid == live.Code.Uuid))
                return live.Code.Email;
            return "Account " + (engine.Accounts.Count + 1);
        }

        static bool IsDifferentAccount(Account a, LiveState live)
        {
            bool desktopDiffers = a.DesktopUuid != null && live.DesktopUuid != null && a.DesktopUuid != live.DesktopUuid;
            bool codeDiffers = a.CodeUuid != null && live.Code.SignedIn && live.Code.Uuid != null && a.CodeUuid != live.Code.Uuid;
            return desktopDiffers || codeDiffers;
        }

        /// <summary>Saves what is signed in now: as a new account, or into an existing one.</summary>
        async Task SaveFlow(Account existing)
        {
            if (busy) return;
            existing = Fresh(existing);
            var live = engine.ReadLive(false);
            Account match = existing ?? engine.FindSaved(live);
            bool canDesktop = engine.DesktopOn;
            bool canCode = engine.CodeOn && live.Code.SignedIn;
            // One saved account holds one Claude account: Claude Code signed in to another account isn't mixed in.
            bool codeIsOther = canDesktop && live.DesktopIn && canCode && live.Code.Uuid != null && live.Code.Uuid != live.DesktopUuid;
            string suggested = match != null ? match.Name : SuggestName(live);

            TextBox nameBox = null;
            CheckBox desktopBox = null, codeBox = null;
            string answer = await OpenDialog(existing != null ? "Update saved sign-in" : "Save account", body =>
            {
                body.Children.Add(FieldLabel("Name"));
                nameBox = Input(suggested);
                body.Children.Add(nameBox);
                body.Children.Add(FieldLabel("Save the sign-in for"));
                desktopBox = Tick(!engine.Desktop.Found ? "Claude Desktop (not found)"
                    : live.DesktopIn ? "Claude Desktop · " + DesktopWho(live) : "Claude Desktop (not signed in)", canDesktop, canDesktop);
                string codeLabel = !engine.CodeOn ? "Claude Code (not found)"
                    : !live.Code.SignedIn ? "Claude Code (not signed in)"
                    : "Claude Code · " + CodeWho(live);
                codeBox = Tick(codeLabel, canCode && !codeIsOther, canCode && !codeIsOther);
                body.Children.Add(desktopBox);
                body.Children.Add(codeBox);

                if (codeIsOther)
                {
                    body.Children.Add(Note("Claude Code is signed in to a different Claude account, so it's left out: each saved account holds one Claude account. To save Claude Code on its own, untick Claude Desktop.", false));
                    var code = codeBox;
                    desktopBox.Checked += (s, e) => { code.IsChecked = false; code.IsEnabled = false; };
                    desktopBox.Unchecked += (s, e) => { code.IsEnabled = true; };
                }
                if (existing != null && IsDifferentAccount(existing, live))
                    body.Children.Add(Note("Claude is signed in to a different account right now. This will replace the saved sign-in of “" + existing.Name + "”.", true));
                else if (existing == null && match != null)
                    body.Children.Add(Note("This account is already saved as “" + match.Name + "”. Saving will update it.", false));
                if (canDesktop && DesktopProcess.IsRunning())
                    body.Children.Add(Para("Claude Desktop will close for a moment while its sign-in is copied.", true));
            }, new DlgBtn("cancel", "Cancel", BtnKind.Secondary),
               new DlgBtn("save", match != null ? "Update" : "Save", BtnKind.Primary));
            if (answer != "save") return;

            bool d = desktopBox.IsChecked == true, c = codeBox.IsChecked == true;
            if (!d && !c)
            {
                await Alert("Nothing to save", "Tick Claude Desktop, Claude Code, or both.");
                return;
            }
            string name = nameBox.Text.Trim();
            if (name.Length == 0) name = suggested;

            // Saving Claude Code on its own goes to the account that Claude Code is signed in to.
            Account target = existing ?? (d ? match : engine.FindSaved(new LiveState { Code = live.Code }));
            if (target != match && nameBox.Text.Trim() == suggested)
                name = target != null ? target.Name : (live.Code.Email ?? suggested);

            SaveResult saved = null;
            bool ok = await RunBusy("Saving…", progress => { saved = engine.SaveCurrent(target, name, d, c, progress); });
            if (!ok || saved == null) return;
            if (saved.Notes.Count > 0) await Alert("Saved “" + saved.Account.Name + "”", string.Join("\n\n", saved.Notes));
            else Toast("Saved “" + saved.Account.Name + "”");
        }

        async Task NewSignInFlow()
        {
            var live = engine.ReadLive(false);
            bool signedIn = (engine.DesktopOn && live.DesktopUuid != null) || (engine.CodeOn && live.Code.SignedIn);
            if (signedIn && engine.FindSaved(live) == null)
            {
                string first = await OpenDialog("Save your current account first?",
                    body => body.Children.Add(Para("You're signed in to an account that isn't saved yet. Save it now so you can switch back to it later.", false)),
                    new DlgBtn("skip", "Skip", BtnKind.Secondary), new DlgBtn("save", "Save it first", BtnKind.Primary));
                if (first == null) return;
                if (first == "save")
                {
                    await SaveFlow(null);
                    live = engine.ReadLive(false);
                    if (engine.FindSaved(live) == null) return;
                }
            }

            CheckBox desktopBox = null, codeBox = null;
            string answer = await OpenDialog("Sign in with another account", body =>
            {
                body.Children.Add(Para("Your current sign-in is kept safe first. Then:", false));
                desktopBox = Tick("Claude Desktop opens signed out", engine.DesktopOn, engine.DesktopOn);
                codeBox = Tick("Claude Code is signed out too", engine.CodeOn && live.Code.SignedIn, engine.CodeOn);
                body.Children.Add(desktopBox);
                body.Children.Add(codeBox);
                body.Children.Add(Para("Sign in with the other account, then come back and click “Save account”.", true));
            }, new DlgBtn("cancel", "Cancel", BtnKind.Secondary), new DlgBtn("go", "Continue", BtnKind.Primary));
            if (answer != "go") return;

            bool d = desktopBox.IsChecked == true, c = codeBox.IsChecked == true;
            if (!d && !c) return;
            await RunBusy("Getting ready…", progress => engine.BeginNewSignIn(d, c, progress));
        }

        async Task CancelPendingFlow()
        {
            if (busy) return;
            string answer = await OpenDialog("Stop adding the account?",
                body => body.Children.Add(Para("ClaudeSwitch will put back the sign-in you had before.", false)),
                new DlgBtn("no", "Keep going", BtnKind.Secondary), new DlgBtn("yes", "Put it back", BtnKind.Primary));
            if (answer != "yes") return;
            if (await RunBusy("Putting back your sign-in…", progress => engine.CancelNewSignIn(progress)))
                Toast("Your previous sign-in is back");
        }

        async Task RenameFlow(Account a)
        {
            a = Fresh(a);
            if (a == null || busy) return;
            TextBox box = null;
            string answer = await OpenDialog("Rename", body =>
            {
                box = Input(a.Name);
                body.Children.Add(box);
            }, new DlgBtn("cancel", "Cancel", BtnKind.Secondary), new DlgBtn("ok", "Rename", BtnKind.Primary));
            if (answer != "ok" || box.Text.Trim().Length == 0) return;

            string error = null;
            try { engine.Rename(a, box.Text); }
            catch (Exception ex)
            {
                Log.Error("Rename failed", ex);
                error = ex.Message;
            }
            Refresh();
            if (error != null) await Alert("Couldn't rename", error);
        }

        async Task DeleteFlow(Account a)
        {
            a = Fresh(a);
            if (a == null || busy) return;
            string answer = await OpenDialog("Remove “" + a.Name + "”?",
                body => body.Children.Add(Para("This deletes the saved sign-in from ClaudeSwitch. It doesn't sign you out of Claude or change your Claude account.", false)),
                new DlgBtn("cancel", "Cancel", BtnKind.Secondary), new DlgBtn("remove", "Remove", BtnKind.Danger));
            if (answer != "remove") return;

            string error = null;
            try { engine.Delete(a); }
            catch (Exception ex)
            {
                Log.Error("Remove failed", ex);
                error = ex.Message;
            }
            Refresh();
            if (error != null) await Alert("Couldn't remove", error);
            else Toast("Removed “" + a.Name + "”");
        }

        void OpenCodeLogin()
        {
            if (Env.NoProcess)
            {
                Toast("Test mode: not opening a terminal.");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/k claude") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("Opening a terminal failed", ex);
                Toast("Couldn't open a terminal. Run “claude” yourself and sign in.");
            }
        }

        async Task<bool> RunBusy(string message, Action<Action<string>> work)
        {
            busy = true;
            CloseMenu();
            El<TextBlock>("BusyText").Text = message;
            El<Grid>("BusyLayer").Visibility = Visibility.Visible;
            Action<string> progress = text => w.Dispatcher.BeginInvoke(new Action(() => El<TextBlock>("BusyText").Text = text));

            Exception error = null;
            try { await Task.Run(() => work(progress)); }
            catch (Exception ex) { error = ex; }

            busy = false;
            El<Grid>("BusyLayer").Visibility = Visibility.Collapsed;
            string launchError = engine.LaunchError;
            engine.LaunchError = null;
            Refresh();

            if (error != null)
            {
                if (error is UserMessageException)
                {
                    await Alert("Couldn't finish", error.Message);
                }
                else
                {
                    Log.Error("Operation failed", error);
                    await Alert("Something went wrong", error.Message + "\n\nDetails are in log.txt (Settings → Open data folder).");
                }
                return false;
            }
            if (launchError != null) Toast("Couldn't reopen Claude. Please open it yourself.");
            return true;
        }

        // ---- repair and updates -------------------------------------------------------------------

        /// <summary>Fixes saved accounts that mix two Claude accounts (possible in version 1.0.0).</summary>
        async Task RunRepair()
        {
            List<string> notes;
            try
            {
                notes = engine.RepairMixedAccounts();
            }
            catch (Exception ex)
            {
                Log.Error("Repairing saved accounts failed", ex);
                return;
            }
            if (notes.Count == 0) return;
            Refresh();
            notes.Add("Each saved account now holds one Claude account. To switch Claude Code along with an account, sign in to Claude Code with that same account, then choose ⋯ → Update saved sign-in.");
            await Alert("Fixed a mix-up in your saved accounts", string.Join("\n\n", notes));
        }

        async Task CheckForUpdates(bool manual)
        {
            if (checkingUpdate || installedUpdate != null) return;
            if (!manual && !engine.Settings.AutoUpdate) return;
            // Test mode never goes online unless a test feed is given.
            if (Env.NoProcess && Env.Get("CLAUDESWITCH_UPDATE_FEED") == null) return;

            checkingUpdate = true;
            SetUpdateStatus("Checking for updates…");
            UpdateInfo found = null;
            bool installed = false;
            string error = null;
            try
            {
                await Task.Run(() =>
                {
                    found = Updater.Check();
                    if (!Updater.IsNewer(found)) return;
                    w.Dispatcher.BeginInvoke(new Action(() => SetUpdateStatus("Downloading version " + Updater.Display(found.Version) + "…")));
                    Updater.Install(found, Updater.ExePath);
                    installed = true;
                });
            }
            catch (Exception ex)
            {
                Log.Error("Update failed", ex);
                error = ex is System.Net.WebException ? "Couldn't reach GitHub to check for updates." : ex.Message;
            }
            checkingUpdate = false;

            if (installed)
            {
                installedUpdate = found;
                SetUpdateStatus("Version " + Updater.Display(found.Version) + " is installed. Restart ClaudeSwitch to use it.");
                El<TextBlock>("UpdateBannerTitle").Text = "ClaudeSwitch " + Updater.Display(found.Version) + " is ready";
                El<Border>("UpdateBanner").Visibility = Visibility.Visible;
            }
            else if (error != null)
            {
                SetUpdateStatus(error);
                if (manual) Toast(error);
            }
            else
            {
                SetUpdateStatus("You have the latest version.");
                if (manual) Toast("You have the latest version.");
            }
        }

        void SetUpdateStatus(string text)
        {
            updateStatus = text;
            El<TextBlock>("UpdateStatusText").Text = "Version " + Updater.CurrentText + ". " + text;
        }

        /// <summary>Starts the newly installed version and closes this one.</summary>
        void RestartIntoUpdate()
        {
            if (busy || installedUpdate == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(Updater.ExePath, Program.AfterUpdateArg) { UseShellExecute = false });
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("Restarting into the update failed", ex);
                Toast("Couldn't restart. Close ClaudeSwitch and open it again to finish updating.");
            }
        }

        // ---- settings -----------------------------------------------------------------------------

        void ShowSettings(bool show)
        {
            if (show) LoadSettingsUi();
            CloseMenu();
            El<Grid>("SettingsLayer").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        void LoadSettingsUi()
        {
            loadingSettings = true;
            try
            {
                string t = engine.Settings.Theme ?? "system";
                El<RadioButton>("ThemeSystem").IsChecked = t != "light" && t != "dark";
                El<RadioButton>("ThemeLight").IsChecked = t == "light";
                El<RadioButton>("ThemeDark").IsChecked = t == "dark";
                El<CheckBox>("ManageDesktopToggle").IsChecked = engine.Settings.ManageDesktop;
                El<CheckBox>("ReopenToggle").IsChecked = engine.Settings.ReopenDesktop;
                El<CheckBox>("ManageCodeToggle").IsChecked = engine.Settings.ManageCode;
                El<CheckBox>("AutoUpdateToggle").IsChecked = engine.Settings.AutoUpdate;
                El<TextBlock>("UpdateStatusText").Text = "Version " + Updater.CurrentText + ". "
                    + (updateStatus.Length > 0 ? updateStatus : "New versions come from this project's GitHub releases.");
                El<TextBlock>("DesktopPathText").Text = engine.Desktop.Found ? engine.Desktop.DataDir : "Not found. Click “Change…” to pick it.";
                El<TextBlock>("CodePathText").Text = engine.Code.Found ? engine.Code.CredentialsPath : "Not found (Claude Code isn't installed here).";
                El<Button>("DesktopAutoBtn").Visibility = string.IsNullOrEmpty(engine.Settings.DesktopDataDir) ? Visibility.Collapsed : Visibility.Visible;
            }
            finally
            {
                loadingSettings = false;
            }
        }

        async Task PickDesktopFolder()
        {
            string chosen = null;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Pick Claude Desktop's data folder (the one with a file called “Local State”).";
                dlg.ShowNewFolderButton = false;
                if (engine.Desktop.Found) dlg.SelectedPath = engine.Desktop.DataDir;
                if (dlg.ShowDialog(new Win32Owner(new WindowInteropHelper(w).Handle)) == System.Windows.Forms.DialogResult.OK)
                    chosen = dlg.SelectedPath;
            }
            if (chosen == null) return;
            if (!File.Exists(System.IO.Path.Combine(chosen, "Local State")))
            {
                await Alert("That's not the right folder", "This folder doesn't contain Claude Desktop's “Local State” file.");
                return;
            }
            engine.Settings.DesktopDataDir = chosen;
            SaveSettingsQuiet();
            Refresh();
            LoadSettingsUi();
        }

        static void OpenFolder(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch (Exception ex) { Log.Error("Opening folder failed", ex); }
        }

        // ---- dialogs ------------------------------------------------------------------------------

        Task<string> Alert(string title, string message)
        {
            return OpenDialog(title, body => body.Children.Add(Para(message, false)), new DlgBtn("ok", "OK", BtnKind.Primary));
        }

        Task<string> OpenDialog(string title, Action<StackPanel> build, params DlgBtn[] buttons)
        {
            var previous = dialogTcs;
            dialogTcs = null;
            if (previous != null) previous.TrySetResult(null);
            CloseMenu();

            El<TextBlock>("DialogTitle").Text = title;
            var body = El<StackPanel>("DialogBody");
            body.Children.Clear();
            if (build != null) build(body);
            body.Visibility = body.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var bar = El<StackPanel>("DialogButtons");
            bar.Children.Clear();
            dialogPrimaryId = null;
            Button focusTarget = null;
            foreach (var b in buttons)
            {
                var btn = new Button
                {
                    Content = b.Label,
                    MinWidth = 84,
                    Margin = new Thickness(8, 0, 0, 0),
                    Style = Res(b.Kind == BtnKind.Primary ? "PrimaryButton" : b.Kind == BtnKind.Danger ? "DangerButton" : "SecondaryButton")
                };
                string id = b.Id;
                btn.Click += (s, e) => CloseDialog(id);
                bar.Children.Add(btn);
                if (b.Kind == BtnKind.Primary)
                {
                    dialogPrimaryId = id;
                    focusTarget = btn;
                }
                else if (b.Kind == BtnKind.Secondary && focusTarget == null)
                {
                    focusTarget = btn; // destructive buttons never get the default focus
                }
            }
            bar.Visibility = buttons.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            var layer = El<Grid>("DialogLayer");
            layer.Visibility = Visibility.Visible;
            layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));

            var tcs = new TaskCompletionSource<string>();
            dialogTcs = tcs;
            w.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                var box = body.Children.OfType<TextBox>().FirstOrDefault();
                if (box != null)
                {
                    box.Focus();
                    box.SelectAll();
                }
                else if (focusTarget != null)
                {
                    focusTarget.Focus();
                }
            }));
            return tcs.Task;
        }

        void CloseDialog(string id)
        {
            var layer = El<Grid>("DialogLayer");
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            layer.Visibility = Visibility.Collapsed;
            var t = dialogTcs;
            dialogTcs = null;
            if (t != null) t.TrySetResult(id);
        }

        TextBlock Para(string text, bool muted)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6), LineHeight = 19 };
            t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "TextMuted" : "Text");
            return t;
        }

        TextBlock FieldLabel(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            return t;
        }

        TextBox Input(string value)
        {
            return new TextBox { Text = value ?? "", Style = Res("Input") };
        }

        CheckBox Tick(string label, bool isChecked, bool enabled)
        {
            return new CheckBox
            {
                IsChecked = isChecked,
                IsEnabled = enabled,
                Style = Res("Check"),
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }
            };
        }

        Border Note(string text, bool warning)
        {
            var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 2) };
            b.SetResourceReference(Border.BackgroundProperty, warning ? "DangerSoft" : "AccentSoft");
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 17 };
            t.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Danger" : "Text");
            b.Child = t;
            return b;
        }

        Button Option(string glyph, string title, string subtitle, string id)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top };
            icon.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            var g = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)w.FindResource("IconFont"),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            g.SetResourceReference(TextBlock.ForegroundProperty, "AccentFg");
            icon.Child = g;
            grid.Children.Add(icon);

            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
            var sub = new TextBlock { Text = subtitle, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), LineHeight = 17 };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            text.Children.Add(sub);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var btn = new Button { Content = grid, Style = Res("CardButton"), Margin = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetName(btn, title);
            btn.Click += (s, e) => CloseDialog(id);
            return btn;
        }
    }
}
