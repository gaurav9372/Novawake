using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;

namespace Novawake
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int HWND_BROADCAST = 0xFFFF;
        private static Mutex? _mutex;
        private static EventWaitHandle? _showEvent;
        private static EventWaitHandle? _hotkeyEvent;

        private MainWindow? window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            Log($"App constructor called. ProcessId={Environment.ProcessId}");
            this.InitializeComponent();

            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                Log($"AppDomain.ProcessExit triggered! Environment.StackTrace:\r\n{Environment.StackTrace}");
            };

            // Log unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Log($"AppDomain.UnhandledException: {ex?.Message}\r\n{ex?.StackTrace}");
                LogException(ex, "AppDomain");
            };

            this.UnhandledException += (s, e) =>
            {
                Log($"XAML UnhandledException: {e.Message}\r\n{e.Exception?.StackTrace}");
                LogException(e.Exception, "XAML");
            };
        }

        public static void Log(string message)
        {
            try
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Novawake");
                Directory.CreateDirectory(appData);
                File.AppendAllText(Path.Combine(appData, "launch_log.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [PID:{Environment.ProcessId}] {message}\r\n");
            }
            catch { }
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Log($"OnLaunched called. Args: {string.Join(" ", Environment.GetCommandLineArgs())}");

            // Single Instance Check
            _mutex = new Mutex(true, "Novawake_Unique_Mutex_Name_2026", out bool isNewInstance);
            Log($"Mutex created. isNewInstance={isNewInstance}");
            if (!isNewInstance)
            {
                bool isHotkey = Novawake.Helpers.ShortcutManager.IsLaunchedViaHotkey();
                Log($"Secondary instance detected. isHotkey={isHotkey}. Signaling primary instance...");

                // Signal the active instance through one IPC route. Posting the window message
                // as well would deliver the same request twice and restart hotkey timers twice.
                bool eventSignaled = false;
                try
                {
                    string eventName = isHotkey ? "Novawake_Hotkey_Event_2026" : "Novawake_Show_Event_2026";
                    using var handle = EventWaitHandle.OpenExisting(eventName);
                    handle.Set();
                    eventSignaled = true;
                    Log($"Successfully signaled EventWaitHandle: {eventName}");
                }
                catch (Exception ex)
                {
                    Log($"EventWaitHandle signal error: {ex.Message}");
                }

                // Fall back to a window message only when the named event was unavailable.
                if (!eventSignaled)
                {
                    try
                    {
                        IntPtr targetHwnd = IntPtr.Zero;
                        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Novawake");
                        string hwndPath = Path.Combine(appData, "hwnd.txt");
                        if (File.Exists(hwndPath))
                        {
                            string hwndText = File.ReadAllText(hwndPath).Trim();
                            if (long.TryParse(hwndText, out long hwndVal))
                            {
                                targetHwnd = (IntPtr)hwndVal;
                            }
                        }

                        uint msg = RegisterWindowMessage("Novawake_ShowAndStartTimer");
                        IntPtr wParam = isHotkey ? (IntPtr)1 : (IntPtr)0;
                        if (targetHwnd != IntPtr.Zero && IsWindow(targetHwnd))
                        {
                            Log($"Posting message to target HWND: {targetHwnd}");
                            PostMessage(targetHwnd, msg, wParam, IntPtr.Zero);
                        }
                        else
                        {
                            Log("Target HWND missing or invalid. Posting HWND_BROADCAST fallback.");
                            PostMessage((IntPtr)HWND_BROADCAST, msg, wParam, IntPtr.Zero);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"PostMessage fallback error: {ex.Message}");
                    }
                }

                Log("Secondary instance exiting with code 0.");
                Environment.Exit(0);
                return;
            }

            Log("Creating MainWindow...");
            try
            {
                window = new MainWindow();
                Log("MainWindow instantiated.");
            }
            catch (Exception ex)
            {
                Log($"FATAL EXCEPTION in MainWindow constructor: {ex.GetType().FullName}: {ex.Message}\r\n{ex.StackTrace}\r\nInner: {ex.InnerException?.Message}");
                throw;
            }

            // Named event listener for reliable single-instance show and hotkey requests
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Novawake_Show_Event_2026");
                _hotkeyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Novawake_Hotkey_Event_2026");

                new Thread(() =>
                {
                    WaitHandle[] handles = new WaitHandle[] { _showEvent, _hotkeyEvent };
                    while (_showEvent != null && _hotkeyEvent != null)
                    {
                        try
                        {
                            int index = WaitHandle.WaitAny(handles);
                            Log($"EventWaitHandle signaled with index {index}");
                            if (index == 0) // Normal show request (Start Menu or Desktop click)
                            {
                                window?.DispatcherQueue?.TryEnqueue(() =>
                                {
                                    Log("DispatcherQueue: Restoring window for normal show request");
                                    window.RestoreWindow();
                                });
                            }
                            else if (index == 1) // Hotkey launch request
                            {
                                window?.DispatcherQueue?.TryEnqueue(() =>
                                {
                                    Log("DispatcherQueue: Restoring window and starting default timer for hotkey request");
                                    window.RestoreWindow();
                                    window.StartDefaultTimer();
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"WaitHandle loop error: {ex.Message}");
                            break;
                        }
                    }
                }) { IsBackground = true }.Start();
            }
            catch (Exception ex)
            {
                Log($"Setting up EventWaitHandles failed: {ex.Message}");
            }

            // Persist HWND immediately for instant redirection
            try
            {
                IntPtr newHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                Log($"New HWND: {newHwnd}");
                if (newHwnd != IntPtr.Zero)
                {
                    string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Novawake");
                    Directory.CreateDirectory(appData);
                    File.WriteAllText(Path.Combine(appData, "hwnd.txt"), newHwnd.ToString());
                }
            }
            catch (Exception ex)
            {
                Log($"Writing hwnd.txt failed: {ex.Message}");
            }

            // Check command line arguments for startup launch
            string[] args = Environment.GetCommandLineArgs();
            bool startMinimized = false;
            foreach (var arg in args)
            {
                if (arg == "--startup" || arg == "-startup")
                {
                    startMinimized = true;
                    break;
                }
            }

            if (startMinimized)
            {
                Log("Starting minimized to system tray.");
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
                if (appWindow != null && appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Minimize();
                }
                window.Activate();
                window.HideWindow();
            }
            else
            {
                Log("Activating window normally.");
                window.Activate();
                window.RestoreWindow();
            }
        }
        /// <summary>Called by MainWindow.ExitApplication to cleanly release the named mutex before process exit.</summary>
        public static void ReleaseSingleInstanceMutex()
        {
            try { _showEvent?.Dispose(); _showEvent = null; } catch { }
            try { _hotkeyEvent?.Dispose(); _hotkeyEvent = null; } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            _mutex = null;
        }

        private static void LogException(Exception? ex, string source)
        {
            if (ex == null) return;
            try
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Novawake");
                Directory.CreateDirectory(appData);
                string logPath = Path.Combine(appData, "crash_log.txt");
                string logText = $"[{DateTime.Now}] Source: {source}\nException: {ex.Message}\nStackTrace: {ex.StackTrace}\nInnerException: {ex.InnerException?.Message}\n\n";
                File.AppendAllText(logPath, logText);
            }
            catch { }
        }
    }
}
