<div align="center">
  <img src="Assets/logo.png" alt="NovaWake logo" width="96" height="96">
  <h1>NovaWake</h1>
  <p>A lightweight Windows utility that keeps your PC awake when you need it.</p>
</div>

## Overview

NovaWake prevents Windows from entering sleep during downloads, presentations,
long-running tasks, or any other time your computer needs to stay available. Use
a preset, enter a custom duration, or keep the system awake indefinitely.

The app is built with WinUI 3 and follows the Windows 11 visual style, including
a Mica backdrop, native navigation, system-tray controls, and a focused countdown
view.

## Features

- Keep the system awake for 30 minutes, 1, 2, 4, 6, or 12 hours.
- Enter a custom duration of up to 99 hours.
- Use Infinite mode when no end time is required.
- Optionally keep the display awake along with the system.
- Play a notification tone when a timed session finishes.
- Start or restore NovaWake with a configurable global keyboard shortcut.
- Choose the timer duration started by the keyboard shortcut.
- Launch normally from Start or the desktop without automatically starting a timer.
- Control the wake state and restore or exit the app from the system tray.
- Start NovaWake with Windows and keep it available in the tray.

## Installation

1. Open the [GitHub Releases page](https://github.com/gaurav9372/NovaWake/releases).
2. Download the Windows setup executable.
3. Run the installer and follow the setup prompts.
4. Open **NovaWake** from the Windows Start menu.

NovaWake currently targets 64-bit Windows 10 and Windows 11.

## Usage

### Start a timer

Open NovaWake and select a preset, **Custom**, or **Infinite**. Timed sessions show
the remaining duration and stop automatically. Select **Stop Wake** to end any
session manually.

### Configure the global shortcut

1. Open **Settings**.
2. Select **Edit** under Desktop Shortcut.
3. Press a key combination containing Ctrl, Alt, or Shift.
4. Save the shortcut.
5. Select the default duration used by shortcut launches.

Pressing the shortcut opens or restores NovaWake and immediately starts the saved
duration. Opening the app normally from Start or the desktop opens the home screen
without starting a timer.

### System tray

Use the tray icon to restore NovaWake, enable or disable keep-awake, or exit the
app. Closing the main window with the title-bar close button stops the active wake
state and exits the app completely.

## Build from source

### Requirements

- Windows 10 or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 with the .NET desktop development workload, or the `dotnet` CLI

### Build and run

```powershell
git clone https://github.com/gaurav9372/NovaWake.git
cd NovaWake
dotnet restore
dotnet build
dotnet run
```

### Publish a self-contained build

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

Published files are written to:

```text
bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/
```

The installer is defined in [`setup.iss`](setup.iss) and can be compiled with
[Inno Setup](https://jrsoftware.org/isinfo.php).

## Project documentation

- [Developer guide](Development.md)
- [Changelog](Changelog.md)

## Built with

- [.NET 8](https://dotnet.microsoft.com/)
- [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK)
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)
- Native Windows power-management APIs

## Developer

Created by **Shreyansh Gaurav**.

- [GitHub](https://github.com/gaurav9372/NovaWake)
- [Donate](https://solidbilla.com/donate)
- [My Apps](https://solidbilla.com/foundry/)
- [Contact](https://solidbilla.com/contact)
- [Website](https://solidbilla.com)

## License

NovaWake is open-source software released under the [MIT License](LICENSE).
