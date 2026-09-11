using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Novawake.Helpers;

namespace Novawake
{
    public sealed partial class MainWindow : Window
    {

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_ASYNC = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;
        private const uint SND_FILENAME = 0x00020000;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

        private const uint MSGFLT_ALLOW = 1;

        private static uint WM_SHOW_AND_START_TIMER;
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DEFAULT_DPI = 96;
        private const int WINDOW_WIDTH = 520;
        private const int WINDOW_HEIGHT = 600;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private AppWindow? _appWindow;

        // Timer state fields
        private readonly DispatcherTimer _timer;
        private TimeSpan _remainingTime;
        private double _totalDurationSeconds;
        private bool _isInfinite;
        private bool _isActive;
        private bool _isExiting;
        private readonly Stopwatch _elapsedTimer = new();
        private TimeSpan _timerDuration;
        private bool _isDialogShowing;
        private readonly SolidColorBrush _alertPromptBrush = new(Windows.UI.Color.FromArgb(255, 245, 158, 11));
        private readonly SolidColorBrush _alertErrorBrush = new(Windows.UI.Color.FromArgb(255, 239, 68, 68));
        private bool _hasEnteredNewKeys = false;

        public System.Windows.Input.ICommand RestoreWindowCommand { get; }
        public System.Windows.Input.ICommand ExitCommand { get; }
        public System.Windows.Input.ICommand ToggleWakeCommand { get; }

        public MainWindow()
        {
            App.Log("MainWindow constructor started.");
            try
            {
                this.InitializeComponent();
                App.Log("InitializeComponent finished.");
            }
            catch (Exception ex)
            {
                App.Log($"InitializeComponent EXCEPTION: {ex}");
                throw;
            }

            WM_SHOW_AND_START_TIMER = RegisterWindowMessage("Novawake_ShowAndStartTimer");
            try
            {
                ChangeWindowMessageFilter(WM_SHOW_AND_START_TIMER, MSGFLT_ALLOW);
            }
            catch { }

            RestoreWindowCommand = new RelayCommand(RestoreWindow);
            ExitCommand = new RelayCommand(ExitApplication);
            ToggleWakeCommand = new RelayCommand(ToggleWakeFromTray);

            TrayIcon.DoubleClickCommand = RestoreWindowCommand;
            TrayIcon.LeftClickCommand = RestoreWindowCommand; // #13: Single-click tray restore

            // Set tray icon source programmatically using absolute path for unpackaged execution
            try
            {
                string trayIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo.ico");
                if (File.Exists(trayIconPath))
                {
                    TrayIcon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(trayIconPath));
                }
            }
            catch { }

            // Bind Menu Items programmatically
            MenuRestore.Command = RestoreWindowCommand;
            MenuToggleWake.Command = ToggleWakeCommand;
            MenuExit.Command = ExitCommand;

            // Set app to Dark Theme (applied to the main container Grid)
            if (Content is Grid rootGrid)
            {
                rootGrid.RequestedTheme = ElementTheme.Dark;
            }

            // Set up Mica backdrop
            this.SystemBackdrop = new MicaBackdrop();

            // Handle window size and decorations
            App.Log("Calling ConfigureWindow...");
            ConfigureWindow();
            App.Log("ConfigureWindow finished.");

            // Initialize Timer
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;

            // Register window close event
            this.Closed += Window_Closed;

            // Load settings and register shortcuts immediately on startup
            App.Log("Calling LoadSettings...");
            LoadSettings();
            App.Log("LoadSettings finished.");

            // WinUI 3: Set version and initial nav selection on root element Loaded
            if (this.Content is FrameworkElement rootElement)
            {
                rootElement.Loaded += (_, _) =>
                {
                    App.Log("MainWindow.rootElement.Loaded fired!");
                    try
                    {
                        // Set version from assembly metadata in all About-related labels
                        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                        string versionStr = version != null
                            ? $"v{version.Major}.{version.Minor}.{version.Build}"
                            : "v1.1.0";
                        AboutVersionText.Text = versionStr;
                        AboutCardVersionText.Text = versionStr;

                        // #1: Default selected navigation item
                        RootNavView.SelectedItem = NavTimer;
                    }
                    catch (Exception ex)
                    {
                        App.Log($"rootElement.Loaded error: {ex}");
                    }
                };
            }

            // Check if we need to auto-start the timer (only when launched via keyboard shortcut/hotkey)
            if (ShortcutManager.IsLaunchedViaHotkey())
            {
                StartDefaultTimer();
            }
        }

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc callback, IntPtr id, IntPtr refData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        private const uint WM_GETMINMAXINFO = 0x0024;

        private SubclassProc? _subclassProc;

        private IntPtr WindowSubclass(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            try
            {
                if (uMsg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    var minimumSize = GetDpiAwareWindowSize(hWnd);
                    mmi.ptMinTrackSize.X = minimumSize.Width;
                    mmi.ptMinTrackSize.Y = minimumSize.Height;
                    Marshal.StructureToPtr(mmi, lParam, false);
                }
                else if (uMsg == WM_SHOW_AND_START_TIMER)
                {
                    App.Log("WindowSubclass received WM_SHOW_AND_START_TIMER");
                    bool isHotkey = (wParam == (IntPtr)1);
                    this.DispatcherQueue?.TryEnqueue(() =>
                    {
                        RestoreWindow();
                        if (isHotkey)
                        {
                            StartDefaultTimer();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                App.Log($"WindowSubclass exception: {ex}");
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        private static Windows.Graphics.SizeInt32 GetDpiAwareWindowSize(IntPtr hwnd)
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi == 0)
            {
                dpi = DEFAULT_DPI;
            }

            int width = (int)Math.Round(WINDOW_WIDTH * dpi / (double)DEFAULT_DPI);
            int height = (int)Math.Round(WINDOW_HEIGHT * dpi / (double)DEFAULT_DPI);

            // Never force a minimum larger than the monitor's usable area.
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO
                {
                    cbSize = Marshal.SizeOf<MONITORINFO>()
                };

                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    width = Math.Min(width, monitorInfo.rcWork.Right - monitorInfo.rcWork.Left);
                    height = Math.Min(height, monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top);
                }
            }

            return new Windows.Graphics.SizeInt32(width, height);
        }



        private void ConfigureWindow()
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            App.Log($"ConfigureWindow called with HWND={hwnd}");

            // Save HWND to local file for reliable single-instance redirection
            try
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Novawake");
                Directory.CreateDirectory(appData);
                File.WriteAllText(Path.Combine(appData, "hwnd.txt"), hwnd.ToString());
            }
            catch (Exception ex)
            {
                App.Log($"ConfigureWindow save hwnd error: {ex.Message}");
            }
            
            // Enable Immersive Dark Mode for title bar
            int useDark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            // Resize and allow resizing/maximizing
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            var initialSize = GetDpiAwareWindowSize(hwnd);
            
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                int x = displayArea.WorkArea.X + Math.Max(0, (displayArea.WorkArea.Width - initialSize.Width) / 2);
                int y = displayArea.WorkArea.Y + Math.Max(0, (displayArea.WorkArea.Height - initialSize.Height) / 2);
                _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, initialSize.Width, initialSize.Height));
            }
            else
            {
                _appWindow.Resize(initialSize);
            }

            _appWindow.Show(true);

            // Set window icon programmatically
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo.ico");
                if (File.Exists(iconPath))
                {
                    _appWindow.SetIcon(iconPath);
                }
            }
            catch { }
            
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
            }

            // Hook window subclass to restrict minimum size
            _subclassProc = new SubclassProc(WindowSubclass);
            SetWindowSubclass(hwnd, _subclassProc, IntPtr.Zero, IntPtr.Zero);

            // Hook the title-bar Close button so it performs a clean, complete shutdown.
            _appWindow.Closing += AppWindow_Closing;
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            App.Log($"AppWindow_Closing called. _isExiting={_isExiting}");
            if (_isExiting)
            {
                return;
            }

            // Finish the native closing event before calling Close() from ExitApplication.
            args.Cancel = true;
            if (DispatcherQueue == null || !DispatcherQueue.TryEnqueue(ExitApplication))
            {
                ExitApplication();
            }
        }

        private void LoadSettings()
        {
            // Check startup shortcut (detach handler to avoid redundant write loop on startup)
            StartupToggle.Toggled -= StartupToggle_Toggled;
            StartupToggle.IsOn = ShortcutManager.StartupShortcutExists();
            StartupToggle.Toggled += StartupToggle_Toggled;

            // Load hotkey setting (default to Ctrl+Alt+N)
            var dict = ReadSettingsDict();
            string currentHotkey = "Ctrl+Alt+N";
            if (dict.TryGetValue("Hotkey", out string? savedHotkey) && !string.IsNullOrWhiteSpace(savedHotkey))
            {
                currentHotkey = savedHotkey;
            }
            CurrentHotkeyText.Text = currentHotkey;

            // Register the app path, but do not rewrite existing shell shortcuts on every launch.
            // Recreating them makes Explorer drop their registered hotkeys until its next restart.
            try
            {
                ShortcutManager.RegisterAppPaths();
                if (!ShortcutManager.DesktopShortcutExists() || !ShortcutManager.HotkeyShortcutMatches(currentHotkey))
                {
                    ShortcutManager.CreateDesktopShortcut(currentHotkey);
                }
            }
            catch { }

            // Load default timer settings and select correct ComboBoxItem (detach handler to avoid redundant write)
            DefaultTimerComboBox.SelectionChanged -= DefaultTimerComboBox_SelectionChanged;
            int defaultTimer = LoadDefaultTimerSetting();
            if (defaultTimer == -1)
            {
                DefaultTimerComboBox.SelectedIndex = 6; // Infinite
            }
            else if (defaultTimer == 30)
            {
                DefaultTimerComboBox.SelectedIndex = 0; // 30m
            }
            else if (defaultTimer == 60)
            {
                DefaultTimerComboBox.SelectedIndex = 1; // 1h
            }
            else if (defaultTimer == 120)
            {
                DefaultTimerComboBox.SelectedIndex = 2; // 2h
            }
            else if (defaultTimer == 240)
            {
                DefaultTimerComboBox.SelectedIndex = 3; // 4h
            }
            else if (defaultTimer == 360)
            {
                DefaultTimerComboBox.SelectedIndex = 4; // 6h
            }
            else if (defaultTimer == 720)
            {
                DefaultTimerComboBox.SelectedIndex = 5; // 12h
            }
            else
            {
                DefaultTimerComboBox.SelectedIndex = 1; // 1h (default fallback)
            }
            DefaultTimerComboBox.SelectionChanged += DefaultTimerComboBox_SelectionChanged;

            // Load persisted DisplaySleepToggle state (detach handler to avoid save + refresh side effects)
            DisplaySleepToggle.Toggled -= DisplaySleepToggle_Toggled;
            DisplaySleepToggle.IsOn = LoadDisplaySleepSetting();
            DisplaySleepToggle.Toggled += DisplaySleepToggle_Toggled;

            // #14: Load persisted SoundAlertToggle state
            SoundAlertToggle.Toggled -= SoundAlertToggle_Toggled;
            SoundAlertToggle.IsOn = LoadSoundAlertSetting();
            SoundAlertToggle.Toggled += SoundAlertToggle_Toggled;
        }

        // ── Settings persistence (key=value format) ─────────────────────────────

        private string GetSettingsFilePath()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Novawake");
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "settings.txt");
        }

        /// <summary>Reads all settings into a dictionary (key=value, one per line).</summary>
        private Dictionary<string, string> ReadSettingsDict()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = GetSettingsFilePath();
                if (File.Exists(path))
                {
                    // Support legacy single-value format for backward compat
                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length == 1 && !lines[0].Contains('='))
                    {
                        // Old format: just the timer value
                        dict["DefaultTimer"] = lines[0].Trim();
                    }
                    else
                    {
                        foreach (string line in lines)
                        {
                            int idx = line.IndexOf('=');
                            if (idx > 0)
                            {
                                dict[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                            }
                        }
                    }
                }
            }
            catch { }
            return dict;
        }

        /// <summary>Writes a single key to settings, preserving other keys.</summary>
        private void WriteSetting(string key, string value)
        {
            try
            {
                var dict = ReadSettingsDict();
                dict[key] = value;
                var lines = new System.Collections.Generic.List<string>();
                foreach (var kv in dict)
                    lines.Add($"{kv.Key}={kv.Value}");
                File.WriteAllLines(GetSettingsFilePath(), lines);
            }
            catch { }
        }

        private int LoadDefaultTimerSetting()
        {
            var dict = ReadSettingsDict();
            if (dict.TryGetValue("DefaultTimer", out string? val))
            {
                if (val == "Infinite") return -1;
                if (int.TryParse(val, out int minutes)) return minutes;
            }
            return 60; // Default to 1 hour
        }

        private bool LoadDisplaySleepSetting()
        {
            var dict = ReadSettingsDict();
            if (dict.TryGetValue("PreventDisplay", out string? val))
                return !string.Equals(val, "False", StringComparison.OrdinalIgnoreCase);
            return true; // Default on
        }

        private bool LoadSoundAlertSetting()
        {
            var dict = ReadSettingsDict();
            if (dict.TryGetValue("PlayChimeSound", out string? val))
                return !string.Equals(val, "False", StringComparison.OrdinalIgnoreCase);
            return true; // Default on
        }

        private void SaveDefaultTimerSetting(string value) => WriteSetting("DefaultTimer", value);
        private void SaveDisplaySleepSetting(bool value) => WriteSetting("PreventDisplay", value.ToString());
        private void SaveSoundAlertSetting(bool value) => WriteSetting("PlayChimeSound", value.ToString());

        public void StartDefaultTimer()
        {
            if (_isActive)
            {
                DeactivateWakeState();
            }

            int defaultTimer = LoadDefaultTimerSetting();
            if (defaultTimer == -1)
            {
                ActivateWakeState(0, true);
            }
            else
            {
                ActivateWakeState(defaultTimer, false);
            }
        }

        private void DefaultTimerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DefaultTimerComboBox != null && DefaultTimerComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                SaveDefaultTimerSetting(tag);
            }
        }

        private void RootNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                NavigateToPage(tag);
            }
        }

        private void NavigateToPage(string tag)
        {
            TimerGrid.Visibility = tag == "Timer" ? Visibility.Visible : Visibility.Collapsed;
            SettingsGrid.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            AboutGrid.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnGoToAbout_Click(object sender, RoutedEventArgs e)
        {
            RootNavView.SelectedItem = NavAbout;
        }

        private void SoundAlertToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SaveSoundAlertSetting(SoundAlertToggle.IsOn);
        }

        private async void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string preset)
            {
                if (preset == "Custom")
                {
                    if (_isDialogShowing) return;
                    _isDialogShowing = true;

                    // Create title element dynamically inside stackPanel to center it
                    var titleTextBlock = new TextBlock
                    {
                        Text = "Custom Duration",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        FontSize = 20,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Margin = new Thickness(0, 4, 0, 12)
                    };

                    var textBlock = new TextBlock
                    {
                        Text = "Enter duration to keep system awake",
                        FontSize = 13,
                        Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 16)
                    };

                    var textBox = new TextBox
                    {
                        Width = 160,
                        Height = 54,
                        FontSize = 26,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        PlaceholderText = "--:--",
                        MaxLength = 5,
                        InputScope = new Microsoft.UI.Xaml.Input.InputScope
                        {
                            Names = { new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.Number } }
                        }
                    };

                    var errorTextBlock = new TextBlock
                    {
                        Text = "Enter a valid time (e.g. 02:30)",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        Visibility = Visibility.Collapsed
                    };

                    var stackPanel = new StackPanel
                    {
                        Spacing = 12,
                        Width = 300,
                        Margin = new Thickness(0, 8, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    stackPanel.Children.Add(titleTextBlock);
                    stackPanel.Children.Add(textBlock);
                    stackPanel.Children.Add(textBox);
                    stackPanel.Children.Add(errorTextBlock);

                    var dialog = new ContentDialog
                    {
                        Title = null,
                        PrimaryButtonText = "Start",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        Content = stackPanel,
                        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                        XamlRoot = btn.XamlRoot,
                        IsPrimaryButtonEnabled = false
                    };

                    // Format input text (HH:MM) and validate
                    void TextBox_TextChanged(object? s, TextChangedEventArgs ev)
                    {
                        textBox.TextChanged -= TextBox_TextChanged;

                        string text = textBox.Text;
                        string digits = new(text.Where(char.IsDigit).ToArray());

                        if (digits.Length > 4)
                        {
                            digits = digits[..4];
                        }

                        string formatted;
                        if (digits.Length >= 3)
                        {
                            formatted = digits.Insert(2, ":");
                        }
                        else if (digits.Length == 2 && text.Contains(':'))
                        {
                            formatted = digits + ":";
                        }
                        else
                        {
                            formatted = digits;
                        }

                        int selectionStart = textBox.SelectionStart;
                        bool isCursorAtEnd = selectionStart >= text.Length;

                        textBox.Text = formatted;

                        if (isCursorAtEnd)
                        {
                            textBox.SelectionStart = formatted.Length;
                        }
                        else
                        {
                            textBox.SelectionStart = Math.Min(selectionStart, formatted.Length);
                        }

                        textBox.TextChanged += TextBox_TextChanged;

                        // Validate time (HH:MM)
                        bool isValid = false;
                        if (digits.Length == 4)
                        {
                            int hours = int.Parse(digits[..2]);
                            int minutes = int.Parse(digits[2..]);
                            if (minutes < 60 && (hours > 0 || minutes > 0))
                            {
                                isValid = true;
                            }
                        }

                        dialog.IsPrimaryButtonEnabled = isValid;
                        errorTextBlock.Visibility = (digits.Length == 4 && !isValid) ? Visibility.Visible : Visibility.Collapsed;
                    }

                    textBox.TextChanged += TextBox_TextChanged;
                    dialog.Opened += (s, ev) => textBox.Focus(FocusState.Programmatic);

                    try
                    {
                        var result = await dialog.ShowAsync();
                        if (result == ContentDialogResult.Primary)
                        {
                            string digits = new(textBox.Text.Where(char.IsDigit).ToArray());
                            if (digits.Length == 4)
                            {
                                int hours = int.Parse(digits[..2]);
                                int minutes = int.Parse(digits[2..]);
                                int totalMinutes = (hours * 60) + minutes;

                                // Keep the existing session alive until the replacement
                                // duration has been confirmed by the user.
                                if (_isActive)
                                {
                                    DeactivateWakeState();
                                }

                                SetActivePreset(btn);
                                ActivateWakeState(totalMinutes, false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Dialog error: {ex.Message}");
                    }
                    finally
                    {
                        _isDialogShowing = false;
                    }
                    return;
                }

                // Any timed/infinite preset starts immediately — no button needed
                if (_isActive)
                {
                    DeactivateWakeState();
                }

                SetActivePreset(btn);

                if (preset == "Infinite")
                {
                    ActivateWakeState(0, true);
                }
                else if (int.TryParse(preset, out int minutes))
                {
                    ActivateWakeState(minutes, false);
                }
            }
        }

        /// <summary>Highlights the selected preset button with AccentButtonStyle; resets all others to default.</summary>
        private void SetActivePreset(Button? active)
        {
            Button[] allPresets =
            [
                BtnPresetInfinite, BtnPreset30m, BtnPreset1h, BtnPreset2h,
                BtnPreset4h, BtnPreset6h, BtnPreset12h, BtnPresetCustom
            ];

            // Resolve accent brush dynamically (yellow fallback)
            var accentBrush = new SolidColorBrush(Microsoft.UI.Colors.Yellow);
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object? colorObj) && colorObj is Windows.UI.Color color)
            {
                accentBrush = new SolidColorBrush(color);
            }

            foreach (var btn in allPresets)
            {
                if (btn == BtnPresetCustom)
                {
                    if (active == BtnPresetCustom)
                    {
                        BorderPresetCustom.BorderBrush = accentBrush;
                        BtnPresetCustom.Foreground = accentBrush;
                        btn.Style = null; // keep transparent background
                    }
                    else
                    {
                        BorderPresetCustom.ClearValue(Border.BorderBrushProperty);
                        BtnPresetCustom.ClearValue(Button.ForegroundProperty);
                        btn.Style = null;
                    }
                }
                else
                {
                    // In WinUI 3, Style = null reverts to the implicit default Button style
                    btn.Style = btn == active
                        ? (Style)Application.Current.Resources["AccentButtonStyle"]
                        : null;
                }
            }
        }

        private void BtnStartWake_Click(object sender, RoutedEventArgs e)
        {
            if (_isActive)
            {
                DeactivateWakeState();
            }
        }

        private bool ActivateWakeState(int minutes, bool isInfinite)
        {
            // Prevent display sleep based on setting
            bool preventDisplay = DisplaySleepToggle.IsOn;
            if (!SleepPreventer.Start(preventDisplay))
            {
                NotifyWakeStateFailure("Windows could not enable keep-awake. The timer was not started.");
                return false;
            }

            _isActive = true;
            _isInfinite = isInfinite;

            // External activations (hotkey/tray) can occur while Settings or About
            // is selected. Always expose the active timer and its Stop control.
            RootNavView.SelectedItem = NavTimer;
            NavigateToPage("Timer");

            // Hide configuration and show the active countdown panel
            TimerConfigPanel.Visibility = Visibility.Collapsed;
            ActiveStatePanel.Visibility = Visibility.Visible;
            RootNavView.IsPaneVisible = false; // #1: Hide sidebar in active mode

            if (_isInfinite)
            {
                CountdownText.Text = "∞";
                StatusText.Text = "SYSTEM KEPT AWAKE";
                StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                
                // #5: Static filled ring + glow pulse instead of spinning indeterminate loader
                WakeProgressRing.IsIndeterminate = false;
                WakeProgressRing.Maximum = 100;
                WakeProgressRing.Value = 100;
                
                MenuToggleWake.Text = "Disable Keep-Awake";

                // Start timer to periodically call SleepPreventer.Refresh() in the background
                _timer.Start();
            }
            else
            {
                _remainingTime = TimeSpan.FromMinutes(minutes);
                _timerDuration = _remainingTime;
                _totalDurationSeconds = _remainingTime.TotalSeconds;
                _elapsedTimer.Restart();
                WakeProgressRing.IsIndeterminate = false;
                WakeProgressRing.Maximum = _totalDurationSeconds;
                WakeProgressRing.Value = _totalDurationSeconds;

                CountdownText.Text = FormatDuration(_remainingTime);
                StatusText.Text = "SYSTEM KEPT AWAKE";
                StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                MenuToggleWake.Text = "Disable Keep-Awake";

                _timer.Start();
            }

            BtnStartWake.Content = "Stop Wake";
            BtnStartWake.Background = new SolidColorBrush(Microsoft.UI.Colors.DarkRed);
            BtnStartWake.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            BtnStartWake.Visibility = Visibility.Visible; // always show Stop when active

            // #7: Show status pill in header
            if (StatusPill != null) StatusPill.Visibility = Visibility.Visible;

            // #4: Start glow pulse animation
            try { GlowPulse.Begin(); } catch { }

            UpdateTrayTooltip();
            return true;
        }

        private void DeactivateWakeState()
        {
            _timer.Stop();
            _elapsedTimer.Reset();
            _isActive = false;
            _isInfinite = false;
            SleepPreventer.Stop();

            // Hide countdown panel and restore the duration picker
            ActiveStatePanel.Visibility = Visibility.Collapsed;
            TimerConfigPanel.Visibility = Visibility.Visible;
            RootNavView.IsPaneVisible = true; // #1: Restore sidebar

            CountdownText.Text = "--:--:--";
            StatusText.Text = "INACTIVE";
            StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            
            WakeProgressRing.IsIndeterminate = false;
            WakeProgressRing.Value = 0;
            
            MenuToggleWake.Text = "Enable Keep-Awake";
            BtnStartWake.Content = "Start Wake";
            BtnStartWake.Background = (SolidColorBrush)Application.Current.Resources["AccentButtonBackground"];
            BtnStartWake.Foreground = (SolidColorBrush)Application.Current.Resources["AccentButtonForeground"];
            BtnStartWake.Visibility = Visibility.Collapsed; // hide until Custom is selected again

            // #7: Hide status pill
            if (StatusPill != null) StatusPill.Visibility = Visibility.Collapsed;

            // #4: Stop glow pulse
            try { GlowPulse.Stop(); GlowRing.Opacity = 0; } catch { }

            SetActivePreset(null); // clear preset button highlight
            UpdateTrayTooltip();
        }

        /// <summary>Keeps the tray tooltip in sync with the current wake state (Improvement 12).</summary>
        private void UpdateTrayTooltip()
        {
            if (_isActive)
            {
                TrayIcon.ToolTipText = _isInfinite
                    ? "NovaWake — ∞ Active"
                    : $"NovaWake — {FormatDuration(_remainingTime)} remaining";
            }
            else
            {
                TrayIcon.ToolTipText = "NovaWake — Inactive";
            }
        }

        private void Timer_Tick(object? sender, object e)
        {
            // Re-assert sleep lock from this thread every tick (SetThreadExecutionState is thread-bound)
            if (!SleepPreventer.Refresh())
            {
                DeactivateWakeState();
                NotifyWakeStateFailure("Windows could not maintain keep-awake. The timer was stopped.");
                return;
            }

            if (_isInfinite)
            {
                return; // Nothing else to update for infinite keep-awake
            }

            TimeSpan remaining = _timerDuration - _elapsedTimer.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                _remainingTime = remaining;
                CountdownText.Text = FormatDuration(_remainingTime);
                WakeProgressRing.Value = Math.Max(0, _remainingTime.TotalSeconds);
                UpdateTrayTooltip(); // Improvement 12: live tray countdown
            }
            else
            {
                // Play the bundled timer-end notification tone when enabled.
                if (LoadSoundAlertSetting())
                {
                    PlayTimerEndAlert();
                }
                DeactivateWakeState();
            }
        }

        private static string FormatDuration(TimeSpan duration)
        {
            int totalHours = (int)Math.Floor(duration.TotalHours);
            return $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        private void NotifyWakeStateFailure(string message)
        {
            App.Log(message);
            try
            {
                TrayIcon.ShowNotification("NovaWake error", message,
                    H.NotifyIcon.Core.NotificationIcon.Info);
            }
            catch (Exception ex)
            {
                App.Log($"Could not show keep-awake error notification: {ex.Message}");
            }
        }

        private static void PlayTimerEndAlert()
        {
            string soundPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Assets",
                "endNotify.wav");

            bool started = File.Exists(soundPath) &&
                PlaySound(soundPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);

            // Preserve an audible fallback if the deployed sound asset is missing or unreadable.
            if (!started)
            {
                MessageBeep(0x40);
            }
        }

        private async void EditShortcut_Click(object sender, RoutedEventArgs e)
        {
            // Set XamlRoot to prevent WinUI 3 dialog crashes
            EditShortcutDialog.XamlRoot = this.Content.XamlRoot;
            EditShortcutDialog.Title = "Edit Shortcut";
            
            // Set TextBox text to current hotkey
            DialogHotkeyTextBox.Text = CurrentHotkeyText.Text;

            // Keep Save button disabled until user enters valid new keys on this modal
            EditShortcutDialog.IsPrimaryButtonEnabled = false;
            _hasEnteredNewKeys = false;

            // Reset alert text block
            DialogAlertTextBlock.Text = string.Empty;
            DialogAlertTextBlock.Visibility = Visibility.Collapsed;

            // Show alert "Press the new shortcut keys" when user clicks or focuses the input field
            RoutedEventHandler gotFocusHandler = (s, args) =>
            {
                if (!_hasEnteredNewKeys)
                {
                    DialogAlertTextBlock.Text = "Press the new shortcut keys";
                    DialogAlertTextBlock.Foreground = _alertPromptBrush;
                    DialogAlertTextBlock.Visibility = Visibility.Visible;
                    EditShortcutDialog.IsPrimaryButtonEnabled = false;
                }
            };

            Microsoft.UI.Xaml.Input.PointerEventHandler pointerPressedHandler = (s, args) =>
            {
                if (!_hasEnteredNewKeys)
                {
                    DialogAlertTextBlock.Text = "Press the new shortcut keys";
                    DialogAlertTextBlock.Foreground = _alertPromptBrush;
                    DialogAlertTextBlock.Visibility = Visibility.Visible;
                    EditShortcutDialog.IsPrimaryButtonEnabled = false;
                }
            };

            DialogHotkeyTextBox.GotFocus += gotFocusHandler;
            DialogHotkeyTextBox.AddHandler(UIElement.PointerPressedEvent, pointerPressedHandler, true);

            // Wire up PreviewKeyDown event handler
            DialogHotkeyTextBox.PreviewKeyDown -= HotkeyTextBox_PreviewKeyDown;
            DialogHotkeyTextBox.PreviewKeyDown += HotkeyTextBox_PreviewKeyDown;

            // Handle PrimaryButtonClick dynamically
            Windows.Foundation.TypedEventHandler<ContentDialog, ContentDialogButtonClickEventArgs>? primaryHandler = null;
            primaryHandler = (dialog, args) =>
            {
                string hotkey = DialogHotkeyTextBox.Text.Trim();
                var (isValid, errorMessage) = ValidateShortcutKey(hotkey);

                if (!isValid)
                {
                    args.Cancel = true;
                    DialogAlertTextBlock.Text = errorMessage ?? "Invalid shortcut key combination.";
                    DialogAlertTextBlock.Foreground = _alertErrorBrush;
                    DialogAlertTextBlock.Visibility = Visibility.Visible;
                    EditShortcutDialog.IsPrimaryButtonEnabled = false;
                    return;
                }

                try
                {
                    // Explorer owns the .lnk hotkey so it can launch Novawake even while closed.
                    // Do not reserve the same key with RegisterHotKey before updating the link.
                    ShortcutManager.CreateDesktopShortcut(hotkey);
                    WriteSetting("Hotkey", hotkey);
                    CurrentHotkeyText.Text = hotkey;
                }
                catch (Exception ex)
                {
                    args.Cancel = true;
                    DialogAlertTextBlock.Text = $"Failed to create shortcut: {ex.Message}";
                    DialogAlertTextBlock.Foreground = _alertErrorBrush;
                    DialogAlertTextBlock.Visibility = Visibility.Visible;
                }
            };

            EditShortcutDialog.PrimaryButtonClick += primaryHandler;
            
            try
            {
                await EditShortcutDialog.ShowAsync();
            }
            finally
            {
                // Clean up handler references
                EditShortcutDialog.PrimaryButtonClick -= primaryHandler;
                DialogHotkeyTextBox.GotFocus -= gotFocusHandler;
                DialogHotkeyTextBox.RemoveHandler(UIElement.PointerPressedEvent, pointerPressedHandler);
                DialogHotkeyTextBox.PreviewKeyDown -= HotkeyTextBox_PreviewKeyDown;
            }
        }

        private void HotkeyTextBox_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (sender is not TextBox targetTextBox) return;
            e.Handled = true;

            var key = e.Key;
            bool ctrl = IsKeyDown(Windows.System.VirtualKey.Control);
            bool alt = IsKeyDown(Windows.System.VirtualKey.Menu);
            bool shift = IsKeyDown(Windows.System.VirtualKey.Shift);

            System.Text.StringBuilder sb = new();
            if (ctrl) sb.Append("Ctrl+");
            if (alt) sb.Append("Alt+");
            if (shift) sb.Append("Shift+");

            if (key == Windows.System.VirtualKey.Control || 
                key == Windows.System.VirtualKey.Shift || 
                key == Windows.System.VirtualKey.Menu ||
                key == Windows.System.VirtualKey.LeftWindows ||
                key == Windows.System.VirtualKey.RightWindows)
            {
                if (key == Windows.System.VirtualKey.LeftWindows || key == Windows.System.VirtualKey.RightWindows)
                {
                    sb.Append("Win+");
                }

                if (sb.Length > 0)
                {
                    targetTextBox.Text = sb.ToString() + "...";
                }
                else
                {
                    targetTextBox.Text = "Press key combo...";
                }
            }
            else
            {
                string keyName = FormatKeyName(key);
                if (!string.IsNullOrEmpty(keyName))
                {
                    sb.Append(keyName);
                    targetTextBox.Text = sb.ToString();
                }
                else
                {
                    if (sb.Length > 0)
                    {
                        targetTextBox.Text = sb.ToString() + "...";
                    }
                    else
                    {
                        targetTextBox.Text = "Press key combo...";
                    }
                }
            }

            // User has entered keys on this modal
            _hasEnteredNewKeys = true;

            // Validate current key combination and update live alert & Save button
            var (isValid, errorMessage) = ValidateShortcutKey(targetTextBox.Text.Trim());
            if (!isValid)
            {
                DialogAlertTextBlock.Text = errorMessage ?? "Invalid shortcut key combination.";
                DialogAlertTextBlock.Foreground = _alertErrorBrush;
                DialogAlertTextBlock.Visibility = Visibility.Visible;
                EditShortcutDialog.IsPrimaryButtonEnabled = false;
            }
            else
            {
                DialogAlertTextBlock.Visibility = Visibility.Collapsed;
                EditShortcutDialog.IsPrimaryButtonEnabled = true;
            }
        }

        private (bool isValid, string? errorMessage) ValidateShortcutKey(string hotkey)
        {
            if (string.IsNullOrWhiteSpace(hotkey) || hotkey == "Press key combo..." || hotkey == "Press the new shortcut keys")
            {
                return (false, "Please press your desired shortcut key combination.");
            }

            if (hotkey.Contains("Win"))
            {
                return (false, "The Windows (Win) key is not supported for desktop shortcuts. Please use Ctrl, Alt, or Shift.");
            }

            if (hotkey.EndsWith("..."))
            {
                return (false, "Please enter a complete key combination (e.g. Ctrl+Alt+N).");
            }

            string[] parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries);
            bool hasModifier = Array.Exists(parts, p => p is "Ctrl" or "Alt" or "Shift");
            bool hasKey = Array.Exists(parts, p => p is not "Ctrl" and not "Alt" and not "Shift");

            if (!hasModifier)
            {
                return (false, "You must include modifier keys (e.g. Ctrl, Alt, or Shift).");
            }

            if (!hasKey)
            {
                return (false, "Please enter a complete key combination (e.g. Ctrl+Alt+N).");
            }

            // Test if the hotkey is already registered by another application or Windows
            var (modifiers, vk) = ShortcutManager.ParseHotkey(hotkey);
            if (vk != 0)
            {
                string current = CurrentHotkeyText?.Text ?? "";
                if (!string.Equals(hotkey, current, StringComparison.OrdinalIgnoreCase))
                {
                    const int TEST_HOTKEY_ID = 0xBEE0;
                    bool testSuccess = RegisterHotKey(IntPtr.Zero, TEST_HOTKEY_ID, modifiers, vk);
                    if (testSuccess)
                    {
                        UnregisterHotKey(IntPtr.Zero, TEST_HOTKEY_ID);
                    }
                    else
                    {
                        testSuccess = RegisterHotKey(IntPtr.Zero, TEST_HOTKEY_ID, modifiers & ~0x4000u, vk);
                        if (testSuccess)
                        {
                            UnregisterHotKey(IntPtr.Zero, TEST_HOTKEY_ID);
                        }
                        else
                        {
                            return (false, "This shortcut is already in use by Windows or another application. Please choose a different key.");
                        }
                    }
                }
            }

            return (true, null);
        }

        private bool IsKeyDown(Windows.System.VirtualKey key)
        {
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }

        private string FormatKeyName(Windows.System.VirtualKey key)
        {
            if (key >= Windows.System.VirtualKey.A && key <= Windows.System.VirtualKey.Z)
            {
                return key.ToString();
            }
            if (key >= Windows.System.VirtualKey.Number0 && key <= Windows.System.VirtualKey.Number9)
            {
                return key.ToString().Replace("Number", "");
            }
            if (key >= Windows.System.VirtualKey.NumberPad0 && key <= Windows.System.VirtualKey.NumberPad9)
            {
                return key.ToString().Replace("NumberPad", "");
            }
            if (key >= Windows.System.VirtualKey.F1 && key <= Windows.System.VirtualKey.F24)
            {
                return key.ToString();
            }

            return key switch
            {
                Windows.System.VirtualKey.Space => "Space",
                Windows.System.VirtualKey.Insert => "Insert",
                Windows.System.VirtualKey.Delete => "Delete",
                Windows.System.VirtualKey.Home => "Home",
                Windows.System.VirtualKey.End => "End",
                Windows.System.VirtualKey.PageUp => "PageUp",
                Windows.System.VirtualKey.PageDown => "PageDown",
                Windows.System.VirtualKey.Up => "Up",
                Windows.System.VirtualKey.Down => "Down",
                Windows.System.VirtualKey.Left => "Left",
                Windows.System.VirtualKey.Right => "Right",
                _ => ""
            };
        }

        private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                if (StartupToggle.IsOn)
                {
                    ShortcutManager.CreateStartupShortcut();
                }
                else
                {
                    ShortcutManager.DeleteStartupShortcut();
                }
            }
            catch (Exception ex)
            {
                // Detach handler temporarily, revert state, and re-attach
                StartupToggle.Toggled -= StartupToggle_Toggled;
                StartupToggle.IsOn = !StartupToggle.IsOn;
                StartupToggle.Toggled += StartupToggle_Toggled;

                ContentDialog errorDialog = new ContentDialog
                {
                    Title = "Startup Shortcut Failed",
                    Content = $"Could not configure system boot startup shortcut: {ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await errorDialog.ShowAsync();
            }
        }

        private void DisplaySleepToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // Persist the new preference
            SaveDisplaySleepSetting(DisplaySleepToggle.IsOn);

            // Bug 5 fix: if wake is already active, refresh the execution state immediately
            if (_isActive)
            {
                if (!SleepPreventer.Start(DisplaySleepToggle.IsOn))
                {
                    DeactivateWakeState();
                    NotifyWakeStateFailure("Windows could not update keep-awake. The timer was stopped.");
                }
            }
        }

        // Window minimization to tray management
        public void HideWindow()
        {
            App.Log("HideWindow called");
            _appWindow?.Hide();
        }

        public void RestoreWindow()
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            App.Log($"RestoreWindow called for HWND={hwnd}");
            try
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Novawake");
                Directory.CreateDirectory(appData);
                File.WriteAllText(Path.Combine(appData, "hwnd.txt"), hwnd.ToString());
            }
            catch (Exception ex)
            {
                App.Log($"RestoreWindow save hwnd error: {ex.Message}");
            }

            ShowWindow(hwnd, SW_RESTORE);

            if (_appWindow != null)
            {
                _appWindow.Show();
                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Restore();
                }
            }

            try
            {
                this.Activate();
            }
            catch { }

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SwitchToThisWindow(hwnd, true);
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            App.Log($"Window_Closed called. _isExiting={_isExiting}");
            if (!_isExiting)
            {
                ExitApplication();
            }
        }

        // System Tray Command Executions
        private void ToggleWakeFromTray()
        {
            if (_isActive)
            {
                DeactivateWakeState();
                // Improvement 11: confirm to user via balloon since the window is hidden
                TrayIcon.ShowNotification("NovaWake", "Keep-Awake disabled.",
                    H.NotifyIcon.Core.NotificationIcon.Info);
            }
            else
            {
                // Default to infinite wake state when toggled from system tray
                if (ActivateWakeState(0, true))
                {
                    // Improvement 11: confirm activation via balloon notification
                    TrayIcon.ShowNotification("NovaWake", "Keep-Awake enabled (Infinite).",
                        H.NotifyIcon.Core.NotificationIcon.Info);
                }
            }
        }

        private void ExitApplication()
        {
            App.Log($"ExitApplication called. Trace: {Environment.StackTrace}");
            _isExiting = true;
            DeactivateWakeState();
            TrayIcon.Dispose();

            // Bug 3: release the single-instance mutex cleanly
            App.ReleaseSingleInstanceMutex();

            // Bug 4: delete the stale hwnd.txt so it can't be misused after exit
            try
            {
                string hwndFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Novawake", "hwnd.txt");
                if (File.Exists(hwndFile))
                    File.Delete(hwndFile);
            }
            catch { }

            this.Close();
            Environment.Exit(0);
        }
    }

    public class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
