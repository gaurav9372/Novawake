using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Novawake.Helpers
{
    public static class ShortcutManager
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHGetPropertyStoreFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            uint flags,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant propVariant);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const int SHCNE_UPDATEITEM = 0x00002000;
        private const uint SHCNF_IDLIST = 0x0000;
        private const uint SHCNF_PATHW = 0x0005;
        private const uint SHCNF_FLUSH = 0x1000;
        private const uint GPS_READWRITE = 0x00000002;
        private const ushort VT_LPWSTR = 31;
        private const int HOTKEY_PROBE_ID = 0xBEEF;
        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
        private const string AppUserModelId = "PuffDesigns.Novawake";
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;

            public PropertyKey(Guid formatId, uint propertyId)
            {
                FormatId = formatId;
                PropertyId = propertyId;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort VarType;
            public ushort Reserved1;
            public ushort Reserved2;
            public ushort Reserved3;
            public IntPtr Value;
            public IntPtr Value2;
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint propertyCount);
            void GetAt(uint propertyIndex, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        public static string GetAppPath()
        {
            return Environment.ProcessPath ?? typeof(ShortcutManager).Assembly.Location.Replace(".dll", ".exe");
        }

        private static string GetDesktopShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NovaWake.lnk");
        }

        private static string GetDesktopHotkeyShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NovawakeHotkey.lnk");
        }

        private static string GetStartupShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup\Novawake.lnk");
        }

        private static string GetStartMenuShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\Novawake.lnk");
        }

        private static string GetCommonStartMenuShortcutPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                "Novawake.lnk");
        }

        private static string GetStartMenuHotkeyShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\NovawakeHotkey.lnk");
        }

        private static string GetCommonHotkeyShortcutPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                "Novawake Quick Start.lnk");
        }

        public static bool DesktopShortcutExists()
        {
            return File.Exists(GetDesktopShortcutPath());
        }

        public static bool StartupShortcutExists()
        {
            return File.Exists(GetStartupShortcutPath());
        }

        public static bool HotkeyShortcutMatches(string expectedHotkey)
        {
            string shortcutPath = GetDesktopHotkeyShortcutPath();
            return File.Exists(shortcutPath)
                && ShortcutHasHotkey(shortcutPath, expectedHotkey)
                && IsHotkeyRegistered(expectedHotkey);
        }

        private static bool IsHotkeyRegistered(string hotkey)
        {
            var (modifiers, key) = ParseHotkey(hotkey);
            if (key == 0)
            {
                return false;
            }

            bool probeAcquired = RegisterHotKey(IntPtr.Zero, HOTKEY_PROBE_ID, modifiers, key);
            if (probeAcquired)
            {
                UnregisterHotKey(IntPtr.Zero, HOTKEY_PROBE_ID);
                return false;
            }

            return Marshal.GetLastWin32Error() == ERROR_HOTKEY_ALREADY_REGISTERED;
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "WScript.Shell is safe COM interop for shortcut inspection")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "WScript.Shell is safe COM interop for shortcut inspection")]
        private static bool ShortcutHasHotkey(string shortcutPath, string expectedHotkey)
        {
            try
            {
                Type wshShellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("WScript.Shell ProgID not found.");
                object shell = Activator.CreateInstance(wshShellType)
                    ?? throw new InvalidOperationException("Could not create WScript.Shell instance.");
                object shortcut = wshShellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath })
                    ?? throw new InvalidOperationException("Could not open shortcut.");
                object? hotkey = shortcut.GetType().InvokeMember(
                    "Hotkey",
                    System.Reflection.BindingFlags.GetProperty,
                    null,
                    shortcut,
                    null);
                var actual = ParseHotkey(hotkey?.ToString() ?? "");
                var expected = ParseHotkey(expectedHotkey);
                return actual.key != 0 && actual == expected;
            }
            catch
            {
                return false;
            }
        }

        public static (uint modifiers, uint key) ParseHotkey(string hotkey)
        {
            if (string.IsNullOrWhiteSpace(hotkey)) return (0, 0);

            uint modifiers = 0x4000; // MOD_NOREPEAT
            string[] parts = hotkey.Split('+');
            string keyStr = "";

            foreach (string part in parts)
            {
                string p = part.Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase))
                    modifiers |= 0x0002;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    modifiers |= 0x0001;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    modifiers |= 0x0004;
                else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase))
                    modifiers |= 0x0008;
                else
                    keyStr = p;
            }

            uint vk = 0;
            if (!string.IsNullOrEmpty(keyStr))
            {
                if (keyStr.Length == 1 && char.IsLetterOrDigit(keyStr[0]))
                {
                    vk = (uint)char.ToUpper(keyStr[0]);
                }
                else if (Enum.TryParse<Windows.System.VirtualKey>(keyStr, true, out var parsedKey))
                {
                    vk = (uint)parsedKey;
                }
            }

            return (modifiers, vk);
        }

        public static void RegisterAppPaths()
        {
            try
            {
                string exePath = GetAppPath();
                string appDir = Path.GetDirectoryName(exePath) ?? "";
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\Novawake.exe");
                if (key != null)
                {
                    key.SetValue("", exePath);
                    key.SetValue("Path", appDir);
                }
            }
            catch { }
        }

        public static bool IsLaunchedViaHotkey()
        {
            string[] args = Environment.GetCommandLineArgs();
            foreach (var arg in args)
            {
                if (string.Equals(arg, "--shortcut-launch", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static void EnsureHotkeyShortcut(string hotkey = "Ctrl+Alt+N")
        {
            RegisterAppPaths();

            string targetPath = GetAppPath();
            string workingDir = Path.GetDirectoryName(targetPath) ?? "";
            string userStartMenuPath = GetStartMenuShortcutPath();
            string commonStartMenuPath = GetCommonStartMenuShortcutPath();
            string userHotkeyPath = GetStartMenuHotkeyShortcutPath();
            string commonHotkeyPath = GetCommonHotkeyShortcutPath();
            string desktopHotkeyPath = GetDesktopHotkeyShortcutPath();

            // Visible Start and Desktop entries are always normal launches that open Home.
            string startMenuPath = File.Exists(commonStartMenuPath)
                ? commonStartMenuPath
                : userStartMenuPath;
            CreateShortcut(
                startMenuPath,
                targetPath,
                workingDir,
                "NovaWake - Keep your PC awake",
                "",
                "");

            // Explorer reliably registers hotkeys from the Desktop namespace. Keep the
            // keyboard-only launcher hidden so the user still sees only NovaWake.
            if (File.Exists(desktopHotkeyPath))
            {
                File.SetAttributes(desktopHotkeyPath, FileAttributes.Normal);
            }

            CreateShortcut(
                desktopHotkeyPath,
                targetPath,
                workingDir,
                "NovaWake keyboard shortcut launcher",
                hotkey,
                "--shortcut-launch");
            File.SetAttributes(
                desktopHotkeyPath,
                File.GetAttributes(desktopHotkeyPath) | FileAttributes.Hidden | FileAttributes.System);

            // Remove legacy Start-menu keyboard launchers so there is one hotkey owner.
            foreach (string legacyPath in new[] { userHotkeyPath, commonHotkeyPath })
            {
                try
                {
                    if (File.Exists(legacyPath))
                    {
                        File.Delete(legacyPath);
                    }
                }
                catch { }
            }

            // Notify Windows Explorer shell to refresh hotkey bindings
            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        public static void CreateDesktopShortcut(string hotkey = "Ctrl+Alt+N")
        {
            string targetPath = GetAppPath();
            string workingDir = Path.GetDirectoryName(targetPath) ?? "";

            // Desktop clicks launch the main app directly and always open normally.
            // A separate hidden Desktop link owns the configurable global hotkey.
            string desktopPath = GetDesktopShortcutPath();
            try
            {
                if (File.Exists(desktopPath)) File.Delete(desktopPath);
            }
            catch { }

            CreateShortcut(desktopPath, targetPath, workingDir, "Keep your PC awake and active", "", "");

            // Ensure the visible Start entry is updated with the user's hotkey.
            EnsureHotkeyShortcut(hotkey);
        }

        public static void DeleteDesktopShortcut()
        {
            string desktopPath = GetDesktopShortcutPath();
            if (File.Exists(desktopPath))
            {
                File.Delete(desktopPath);
            }

            string desktopHotkeyPath = GetDesktopHotkeyShortcutPath();
            if (File.Exists(desktopHotkeyPath))
            {
                File.SetAttributes(desktopHotkeyPath, FileAttributes.Normal);
                File.Delete(desktopHotkeyPath);
            }

            string startMenuPath = GetStartMenuShortcutPath();
            if (File.Exists(startMenuPath))
            {
                File.Delete(startMenuPath);
            }

            string hotkeyPath = GetStartMenuHotkeyShortcutPath();
            if (File.Exists(hotkeyPath))
            {
                File.Delete(hotkeyPath);
            }

            string commonHotkeyPath = GetCommonHotkeyShortcutPath();
            try
            {
                if (File.Exists(commonHotkeyPath))
                {
                    File.Delete(commonHotkeyPath);
                }
            }
            catch { }

            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        public static void CreateStartupShortcut()
        {
            string shortcutPath = GetStartupShortcutPath();
            string targetPath = GetAppPath();
            string workingDir = Path.GetDirectoryName(targetPath) ?? "";
            
            CreateShortcut(shortcutPath, targetPath, workingDir, "NovaWake Startup", "", "--startup");
        }

        public static void DeleteStartupShortcut()
        {
            string shortcutPath = GetStartupShortcutPath();
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "WScript.Shell is safe COM interop for shortcut creation")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "WScript.Shell is safe COM interop for shortcut creation")]
        private static void CreateShortcut(
            string shortcutPath,
            string targetPath,
            string workingDir,
            string description,
            string hotkey,
            string arguments = "",
            string appUserModelId = AppUserModelId)
        {
            bool shortcutAlreadyExists = File.Exists(shortcutPath);
            Type wshShellType = Type.GetTypeFromProgID("WScript.Shell") 
                ?? throw new InvalidOperationException("WScript.Shell ProgID not found.");
            
            object shell = Activator.CreateInstance(wshShellType) 
                ?? throw new InvalidOperationException("Could not create WScript.Shell instance.");
            
            object shortcut = wshShellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })
                ?? throw new InvalidOperationException("Could not invoke CreateShortcut.");
            
            Type shortcutType = shortcut.GetType();

            // Writing the same HotKey value over itself can leave Explorer's global
            // registration stale. Clear and save it before assigning the new value.
            if (shortcutAlreadyExists && !string.IsNullOrWhiteSpace(hotkey))
            {
                shortcutType.InvokeMember("Hotkey", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "" });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            }
            
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { description });
            
            shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { arguments ?? "" });
            shortcutType.InvokeMember("Hotkey", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { hotkey ?? "" });

            // Explicitly set shortcut icon to bypass executable caching issues
            string iconPath = Path.Combine(workingDir, "Assets", "logo.ico");
            if (File.Exists(iconPath))
            {
                shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { $"{iconPath},0" });
            }
            
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

            SetShortcutAppUserModelId(shortcutPath, appUserModelId);
            NotifyShortcutUpdated(shortcutPath);
        }

        private static void NotifyShortcutUpdated(string shortcutPath)
        {
            IntPtr pathPointer = IntPtr.Zero;
            try
            {
                pathPointer = Marshal.StringToCoTaskMemUni(shortcutPath);
                SHChangeNotify(
                    SHCNE_UPDATEITEM,
                    SHCNF_PATHW | SHCNF_FLUSH,
                    pathPointer,
                    IntPtr.Zero);
            }
            finally
            {
                if (pathPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pathPointer);
                }
            }
        }

        [System.Diagnostics.CodeAnalysis.DynamicDependency(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All,
            typeof(IPropertyStore))]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
            "Trimming",
            "IL2050",
            Justification = "The COM property-store interface is explicitly preserved by DynamicDependency.")]
        private static void SetShortcutAppUserModelId(string shortcutPath, string appUserModelId)
        {
            IPropertyStore? propertyStore = null;
            PropVariant value = default;

            try
            {
                Guid propertyStoreId = typeof(IPropertyStore).GUID;
                SHGetPropertyStoreFromParsingName(shortcutPath, IntPtr.Zero, GPS_READWRITE, ref propertyStoreId, out propertyStore);

                var appIdKey = new PropertyKey(
                    new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                    5);

                value.VarType = VT_LPWSTR;
                value.Value = Marshal.StringToCoTaskMemUni(appUserModelId);
                propertyStore.SetValue(ref appIdKey, ref value);
                propertyStore.Commit();
            }
            catch
            {
                // Shortcut creation still succeeds on systems where the property store is unavailable.
            }
            finally
            {
                if (value.Value != IntPtr.Zero)
                {
                    PropVariantClear(ref value);
                }

                if (propertyStore != null)
                {
                    Marshal.ReleaseComObject(propertyStore);
                }
            }
        }
    }
}
