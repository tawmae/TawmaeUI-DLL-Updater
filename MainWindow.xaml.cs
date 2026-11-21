using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glyph = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;

namespace TawmaeUIUpdater
{
    // ==============================================================
    public partial class MainWindow : Window
    {
        enum Phase { NoStreamerBot, Checking, Ready, Offline, Installing, Installed, Failed }
        enum Act { None, Install, Retry, Find, Elevate, Start, Close }
        enum Tone { Plain, Accent, Warn, Danger }

        const string Behind = "You are a couple versions behind, buddy!";
        const string CloseFirst = "**Close Streamer.bot to update.** Don't end it in Task Manager, that can lose data.";
        const string WaitForExit = "**Waiting for Streamer.bot to exit.** If it's in the tray, right-click its icon and choose Exit. Don't end it in Task Manager, that can lose data.";

        static readonly Dictionary<string, string> Icons = new Dictionary<string, string>
        {
            ["alert"] = "M21.73 18l-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3M12 9v4M12 17h.01",
            ["code"] = "M16 18l6-6l-6-6M8 6l-6 6l6 6",
            ["download"] = "M12 15V3M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5l5-5",
            ["folder"] = "M6 14l1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2",
            ["loader"] = "M21 12a9 9 0 1 1-6.219-8.56",
            ["play"] = "M5 5a2 2 0 0 1 3.008-1.728l11.997 6.998a2 2 0 0 1 .003 3.458l-12 7A2 2 0 0 1 5 19z",
            ["refresh"] = "M3 12a9 9 0 0 1 9-9a9.75 9.75 0 0 1 6.74 2.74L21 8M21 3v5h-5M21 12a9 9 0 0 1-9 9a9.75 9.75 0 0 1-6.74-2.74L3 16M8 16H3v5",
            ["shield"] = "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z",
        };

        static readonly Brush Danger = Solid("#FF8A80");
        static readonly Brush DangerInk = Solid("#2A0D0A");
        static readonly Brush WarningInk = Solid("#241A05");
        static readonly Brush ChipPlain = Solid("#343434");

        readonly Uri _source;
        readonly bool _elevated = Updater.IsElevated();
        readonly DispatcherTimer _watch = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        readonly Dictionary<int, IDisposable> _exits = new Dictionary<int, IDisposable>();
        readonly Brush _text, _faint, _raised, _accent, _accentInk, _warning;

        string _folder;
        DllFile _local, _remote;
        Version _previous;
        Phase _phase;
        Act _primary, _side;
        string _problem, _detail, _primaryIcon;
        bool _needsAdmin, _running, _visible, _polling, _pollAgain, _closing, _win11;
        double _progress;
        DateTime _since;
        TextBlock _primaryText;
        RotateTransform _spin;
        CancellationTokenSource _cancel;

        // ==============================================================
        public MainWindow(string start, Uri source)
        {
            InitializeComponent();
            _source = source ?? Updater.Source;
            _text = (Brush)Resources["Text"];
            _faint = (Brush)Resources["Faint"];
            _raised = (Brush)Resources["Raised"];
            _accent = (Brush)Resources["Accent"];
            _accentInk = (Brush)Resources["AccentInk"];
            _warning = (Brush)Resources["Warning"];
            _folder = Updater.FindStreamerBot(start) ?? Updater.FindStreamerBot(AppDomain.CurrentDomain.BaseDirectory);

            TitleText.Text = "TawmaeUI DLL Updater v" + Updater.Show(Updater.AppVersion);
            FolderButton.Content = Symbol("folder", 18);
            NoticeIcon.Content = Symbol("alert", 16);
            RefreshButton.Content = Symbol("refresh", 16);
            SourceIcon.Content = Symbol("code", 14);
            Side.Content = "Close";

            _watch.Tick += (s, e) => Poll();
            SourceInitialized += (s, e) => Frame();
            StateChanged += (s, e) => Framed();
            Loaded += (s, e) => Begin();
            Activated += (s, e) => Poll();

            _phase = _folder == null ? Phase.NoStreamerBot : Phase.Checking;
            Render();
        }

        // ==============================================================
        void Frame()
        {
            _win11 = Native.IsWindows11();
            if (_win11)
                Native.DarkFrame(new WindowInteropHelper(this).Handle, 0x2E2E2E);
            Framed();
        }

        // ==============================================================
        void Framed()
        {
            bool max = WindowState == WindowState.Maximized;
            Root.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
            Root.BorderThickness = new Thickness(_win11 || max ? 0 : 1);
            MaxButton.Content = max ? "" : "";
            AutomationProperties.SetName(MaxButton, max ? "Restore" : "Maximize");
        }

        // ==============================================================
        void Begin()
        {
            if (_folder == null)
            {
                _phase = Phase.NoStreamerBot;
                Render();
                return;
            }
            Updater.Cleanup(_folder);
            _local = Updater.ReadLocal(Updater.DllPath(_folder));
            Apply(Updater.Inspect(_folder));
            _watch.Start();
            Check();
        }

        // ==============================================================
        async void Check()
        {
            _cancel?.Cancel();
            var cancel = _cancel = new CancellationTokenSource();
            _phase = Phase.Checking;
            _remote = null;
            _progress = 0;
            _since = DateTime.UtcNow;
            Render();
            try
            {
                var remote = await Updater.DownloadAsync(_source, new Progress<double>(Progressed), cancel.Token);
                if (cancel.IsCancellationRequested)
                    return;
                _remote = remote;
                _phase = Phase.Ready;
            }
            catch (Exception ex)
            {
                if (cancel.IsCancellationRequested)
                    return;
                _phase = Phase.Offline;
                (_problem, _detail) = DownloadProblem(ex);
            }
            Render();
        }

        // ==============================================================
        void Progressed(double value)
        {
            _progress = value;
            if (_phase == Phase.Checking)
                Render();
        }

        // ==============================================================
        async void Poll()
        {
            string folder = _folder;
            if (folder == null || _closing || !IsLoaded)
                return;
            if (_polling)
            {
                _pollAgain = true;
                return;
            }
            _polling = true;
            RunState state = null;
            try
            {
                state = await Task.Run(() => Updater.Inspect(folder));
            }
            catch (Exception)
            {
            }
            _polling = false;
            if (state != null && !_closing && folder == _folder)
            {
                bool changed = state.Running != _running || state.Visible != _visible;
                Apply(state);
                if (changed)
                    Render();
            }
            if (_pollAgain)
            {
                _pollAgain = false;
                Poll();
            }
        }

        // ==============================================================
        void Apply(RunState state)
        {
            _running = state.Running;
            _visible = state.Visible;
            foreach (int id in state.Ids)
            {
                if (_exits.ContainsKey(id))
                    continue;
                var exit = Native.WatchExit(id, () => Dispatcher.BeginInvoke(new Action(Poll)));
                if (exit != null)
                    _exits[id] = exit;
            }
            foreach (int id in _exits.Keys.Where(id => !state.Ids.Contains(id)).ToList())
            {
                _exits[id].Dispose();
                _exits.Remove(id);
            }
        }

        // ==============================================================
        async void Install()
        {
            string folder = _folder;
            var file = _remote;
            if (folder == null || file == null)
                return;
            Apply(Updater.Inspect(folder));
            if (_running)
            {
                Render();
                return;
            }

            var previous = _local?.Version;
            _phase = Phase.Installing;
            Render();
            try
            {
                await Task.WhenAll(Task.Run(() => Updater.Install(folder, file)), Task.Delay(300));
                _previous = previous;
                _phase = Phase.Installed;
            }
            catch (Exception ex)
            {
                _phase = Phase.Failed;
                (_problem, _detail, _needsAdmin) = InstallProblem(ex);
            }
            _local = Updater.ReadLocal(Updater.DllPath(folder));
            Render();
        }

        // ==============================================================
        void Render()
        {
            string installed = _local?.Version == null ? null : "v" + Updater.Show(_local.Version);
            string latest = _remote == null ? null : "v" + Updater.Show(_remote.Version);
            bool writes = false;
            bool refresh = false;
            _side = Act.None;

            switch (_phase)
            {
                case Phase.NoStreamerBot:
                    SetChip("Streamer.bot not found", Tone.Danger);
                    SetVersion(null);
                    SetFacts("Put this file in your Streamer.bot folder, next to **Streamer.bot.exe**, and run it again.");
                    SetPrimary(Act.Find, "Find Streamer.bot.exe", "folder", false);
                    break;

                case Phase.Checking:
                    SetChip("Checking", Tone.Plain);
                    SetVersion(installed, true);
                    SetFacts("Asking tawmae.xyz for the latest version");
                    SetPrimary(Act.None, "Checking..." + Percent(), "loader", false, false, true);
                    refresh = true;
                    break;

                case Phase.Offline:
                    SetChip("Offline", Tone.Danger);
                    SetVersion(installed);
                    SetFacts(_problem, _detail);
                    SetPrimary(Act.Retry, "Try again", "refresh", false);
                    break;

                case Phase.Ready:
                    writes = Ready(installed, latest);
                    refresh = true;
                    break;

                case Phase.Installing:
                    SetChip(_local == null ? "Installing" : "Updating", Tone.Plain);
                    SetVersion(installed, true);
                    SetFacts(_local == null ? "Copying TawmaeUI.dll into Streamer.bot" : "Replacing TawmaeUI.dll");
                    SetPrimary(Act.None, _local == null ? "Installing..." : "Updating...", "loader", false, false, true);
                    refresh = true;
                    break;

                case Phase.Installed:
                    SetChip(_previous == null ? "Installed" : "Updated", Tone.Accent);
                    SetVersion(installed);
                    SetFacts(_previous == null ? "TawmaeUI.dll is in place" : "Was **v" + Updater.Show(_previous) + "**");
                    if (_running || _elevated)
                    {
                        SetPrimary(Act.Close, "Close", null, false);
                    }
                    else
                    {
                        SetPrimary(Act.Start, "Start Streamer.bot", "play", true);
                        _side = Act.Close;
                    }
                    break;

                case Phase.Failed:
                    SetChip("Not updated", Tone.Danger);
                    SetVersion(installed);
                    SetFacts(_problem, _detail);
                    if (_needsAdmin && !_elevated)
                        SetPrimary(Act.Elevate, "Run as administrator", "shield", false);
                    else
                        SetPrimary(Act.Install, "Try again", "refresh", false, !_running && _remote != null);
                    writes = true;
                    break;
            }

            Side.Visibility = _side == Act.None ? Visibility.Collapsed : Visibility.Visible;
            RefreshButton.Visibility = refresh ? Visibility.Visible : Visibility.Collapsed;
            RefreshButton.IsEnabled = _phase == Phase.Ready;

            bool blocked = writes && _running;
            Notice.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
            if (blocked)
                Rich(NoticeText, _visible ? CloseFirst : WaitForExit);

            Tools.Visibility = _folder == null ? Visibility.Collapsed : Visibility.Visible;
            if (_folder != null)
            {
                string path = Updater.DllPath(_folder);
                FolderText.Text = path.Replace("\\", "\\​");
                AutomationProperties.SetName(FolderText, path);
            }
        }

        // ==============================================================
        bool Ready(string installed, string latest)
        {
            if (_local == null)
            {
                SetChip("Not installed", Tone.Plain);
                SetVersion(null);
                SetFacts("TawmaeUI.dll isn't in this Streamer.bot yet");
                SetPrimary(Act.Install, "Install " + latest, "download", true, !_running);
                return true;
            }
            if (_local.Version == null)
            {
                SetChip("Damaged", Tone.Danger);
                SetVersion(null);
                SetFacts("Your TawmaeUI.dll can't be read");
                SetPrimary(Act.Install, "Replace with " + latest, "download", true, !_running);
                return true;
            }

            int order = _local.Version.CompareTo(_remote.Version);
            bool otherBuild = order == 0 && _local.Sha256 != null && _local.Sha256 != _remote.Sha256;
            if (order < 0 || otherBuild)
            {
                SetChip("Update available", Tone.Warn);
                SetVersion(installed);
                SetFacts(otherBuild ? "tawmae.xyz has a different build of **" + latest + "**" : Behind);
                SetPrimary(Act.Install, otherBuild ? "Update" : "Update to " + latest, "download", true, !_running);
                return true;
            }

            SetChip("Up to date", Tone.Accent);
            SetVersion(installed);
            SetFacts(order == 0 ? "You're on the latest version, yippie!" : "Newer than tawmae.xyz (**" + latest + "**)");
            SetPrimary(Act.Close, "Close", null, false);
            return false;
        }

        // ==============================================================
        void SetChip(string text, Tone tone)
        {
            ChipText.Text = text;
            switch (tone)
            {
                case Tone.Accent:
                    Chip.Background = _accent;
                    ChipText.Foreground = _accentInk;
                    break;
                case Tone.Warn:
                    Chip.Background = _warning;
                    ChipText.Foreground = WarningInk;
                    break;
                case Tone.Danger:
                    Chip.Background = Danger;
                    ChipText.Foreground = DangerInk;
                    break;
                default:
                    Chip.Background = ChipPlain;
                    ChipText.Foreground = _text;
                    break;
            }
        }

        // ==============================================================
        void SetVersion(string text, bool idle = false)
        {
            Value.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            Value.Text = text ?? "";
            Value.Foreground = idle ? _faint : _text;
        }

        // ==============================================================
        void SetFacts(string text, string detail = null)
        {
            Rich(Facts, text);
            Facts.ToolTip = string.IsNullOrEmpty(detail) ? null : detail;
        }

        // ==============================================================
        void SetPrimary(Act act, string text, string icon, bool strong, bool enabled = true, bool spin = false)
        {
            _primary = act;
            Primary.Background = strong ? _accent : _raised;
            Primary.Foreground = strong ? _accentInk : _text;
            Primary.IsEnabled = enabled && act != Act.None;
            AutomationProperties.SetName(Primary, text);

            string key = (icon ?? "") + (spin ? ":spin" : "");
            if (_primaryText != null && key == _primaryIcon)
            {
                _primaryText.Text = text;
                return;
            }
            _spin?.BeginAnimation(RotateTransform.AngleProperty, null);
            _spin = null;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon != null)
            {
                var glyph = Symbol(icon, 15);
                glyph.Margin = new Thickness(0, 0, 7, 0);
                if (spin)
                {
                    _spin = new RotateTransform();
                    glyph.RenderTransformOrigin = new Point(0.5, 0.5);
                    glyph.RenderTransform = _spin;
                    _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8)) { RepeatBehavior = RepeatBehavior.Forever });
                }
                panel.Children.Add(glyph);
            }
            _primaryText = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(_primaryText);
            _primaryIcon = key;
            Primary.Content = panel;
        }

        // ==============================================================
        string Percent()
        {
            bool slow = (DateTime.UtcNow - _since).TotalMilliseconds > 700;
            return slow && _progress > 0 && _progress < 1 ? " " + (int)(_progress * 100) + "%" : "";
        }

        // ==============================================================
        void Rich(TextBlock block, string text)
        {
            block.Inlines.Clear();
            string[] parts = (text ?? "").Split(new[] { "**" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0)
                    continue;
                var run = new Run(parts[i]);
                if (i % 2 == 1)
                {
                    run.Foreground = _text;
                    run.FontWeight = FontWeights.SemiBold;
                }
                block.Inlines.Add(run);
            }
        }

        // ==============================================================
        static FrameworkElement Symbol(string name, double size)
        {
            var path = new Glyph
            {
                Data = Geometry.Parse(Icons[name]),
                Width = 24,
                Height = 24,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            var color = new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self };
            path.SetBinding(Shape.StrokeProperty, color);
            if (name == "play")
                path.SetBinding(Shape.FillProperty, color);
            return new Viewbox { Width = size, Height = size, Child = path, VerticalAlignment = VerticalAlignment.Center };
        }

        // ==============================================================
        static Brush Solid(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        // ==============================================================
        static (string, string) DownloadProblem(Exception ex)
        {
            switch (ex)
            {
                case TimeoutException _:
                    return ("tawmae.xyz took too long to answer. Try again in a moment.", null);
                case InvalidDataException _:
                    return (ex.Message, null);
                case HttpRequestException _ when ex.InnerException == null:
                    return (ex.Message, null);
                case HttpRequestException _:
                    return ("Couldn't reach tawmae.xyz. Check your internet connection.", Innermost(ex).Message);
                case IOException _:
                    return ("The download stopped early. Try again.", ex.Message);
                default:
                    return ("Couldn't check tawmae.xyz. Try again.", ex.Message);
            }
        }

        // ==============================================================
        (string, string, bool) InstallProblem(Exception ex)
        {
            int code = ex.HResult & 0xFFFF;
            if (ex is IOException && (code == 32 || code == 33 || code == 1224))
                return ("TawmaeUI.dll is in use. Close Streamer.bot and try again.", ex.Message, false);
            if (ex is UnauthorizedAccessException)
                return _elevated
                    ? ("Windows blocked changes to this folder, even for an administrator.", ex.Message, false)
                    : ("Windows blocked changes to this folder. Run the updater as administrator.", ex.Message, true);
            if (ex is InvalidDataException)
                return (ex.Message, null, false);
            return ("Couldn't replace TawmaeUI.dll. Try again.", ex.Message, false);
        }

        // ==============================================================
        static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null)
                ex = ex.InnerException;
            return ex;
        }

        // ==============================================================
        public void Crash(Exception ex)
        {
            _problem = "Something went wrong. Try again.";
            _detail = ex.Message;
            _needsAdmin = false;
            _phase = _remote == null ? Phase.Offline : Phase.Failed;
            Render();
        }

        // ==============================================================
        void Do(Act act)
        {
            switch (act)
            {
                case Act.Install:
                    Install();
                    break;
                case Act.Retry:
                    Check();
                    break;
                case Act.Find:
                    Find();
                    break;
                case Act.Elevate:
                    Elevate();
                    break;
                case Act.Start:
                    StartStreamerBot();
                    break;
                case Act.Close:
                    Close();
                    break;
            }
        }

        // ==============================================================
        void Find()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Find Streamer.bot.exe",
                Filter = "Streamer.bot|Streamer.bot.exe",
                FileName = Updater.StreamerBotExe,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) != true)
                return;
            string folder = Updater.FindStreamerBot(dialog.FileName);
            if (folder == null)
                return;
            _folder = folder;
            Begin();
        }

        // ==============================================================
        void Elevate()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string target = Path.Combine(_folder, Updater.StreamerBotExe);
                Process.Start(new ProcessStartInfo(exe, "\"" + target + "\"") { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (Exception)
            {
            }
        }

        // ==============================================================
        void StartStreamerBot()
        {
            try
            {
                Updater.StartStreamerBot(_folder);
                Close();
            }
            catch (Exception ex)
            {
                SetFacts("Couldn't start Streamer.bot. Start it the usual way.", ex.Message);
            }
        }

        // ==============================================================
        void Open(string url)
        {
            try
            {
                Updater.OpenLink(url);
            }
            catch (Exception)
            {
            }
        }

        // ==============================================================
        void OnPrimary(object sender, RoutedEventArgs e) => Do(_primary);

        void OnSide(object sender, RoutedEventArgs e) => Do(_side);

        void OnRefresh(object sender, RoutedEventArgs e) => Begin();

        void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        void OnMaximize(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
        }

        void OnClose(object sender, RoutedEventArgs e) => Close();

        void OnSource(object sender, RoutedEventArgs e) => Open(Updater.RepoUrl);

        void OnBrand(object sender, RoutedEventArgs e) => Open(Updater.SiteUrl);

        void OnFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_folder != null)
                    Updater.ShowInExplorer(_folder);
            }
            catch (Exception)
            {
            }
        }

        // ==============================================================
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_phase == Phase.Installing)
            {
                e.Cancel = true;
                return;
            }
            _closing = true;
            _watch.Stop();
            _cancel?.Cancel();
            foreach (var exit in _exits.Values)
                exit.Dispose();
            _exits.Clear();
            base.OnClosing(e);
        }
    }
}
