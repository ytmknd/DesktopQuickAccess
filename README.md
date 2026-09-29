# Desktop Quick Access

A small Windows tray utility that replicates the "taskbar toolbar" quick-access
feature Windows 10 offered (add a folder toolbar to the taskbar) and that
Windows 11 removed. It sits in the notification area and lets you browse and
open items straight from your Desktop folder without opening File Explorer.

## Features

- **Left-click** the tray icon to pop up a menu listing everything on your
  Desktop. Folders expand into submenus (lazily loaded, always reflecting the
  current contents); clicking a file opens it with its default application.
- **Incremental search box** at the top of that menu: just start typing and
  the list narrows as you type (the box is focused the moment the menu opens).
  Matching ignores the differences Japanese filenames tend to vary in —
  hiragana vs. katakana, full-width vs. half-width, upper vs. lower case — so
  `かいぎ`, `カイギ` and `ｶｲｷﾞ` all find the same file, and IME input works.
  Space-separated words are ANDed. `Enter` opens the top hit, `Down`/`Tab`
  moves into the list, `Esc` clears the box.
- **Right-click** the tray icon for the app menu: open the Desktop folder,
  toggle "launch at Windows sign-in", and exit.
- Hovering over the scroll arrows at the top/bottom of a long menu
  auto-scrolls it (instead of requiring a press-and-hold click).
- File/folder icons match their real Explorer icons (via the shell icon
  API), with caching so repeated menu opens stay fast.
- The tray icon is set to always show in the visible tray area (like
  OneDrive) instead of being hidden under the "show hidden icons" chevron,
  on a best-effort basis (this relies on an undocumented Windows setting).
- Single-instance guard; safe to launch multiple times (a second launch just
  notifies you and exits).

## Requirements

- Windows 10/11 (x64)
- [.NET SDK](https://dotnet.microsoft.com/) 10.0+ to build from source
  (the published app is self-contained and does **not** require .NET to be
  installed on the target machine)
- [Inno Setup](https://jrsoftware.org/isinfo.php) 6/7 to build the installer
  (only needed if you want to produce `DesktopQuickAccessSetup.exe`)

## Project layout

```
DesktopQuickAccess.slnx      Solution file (XML-based .slnx format)
DesktopQuickAccess.csproj    Project file (net10.0-windows, WinForms)
Program.cs                   Entry point, single-instance guard
TrayAppContext.cs            Tray icon, menus, hover-scroll, autostart wiring
ShellIcon.cs                 Shell icon retrieval + caching (SHGetFileInfo)
SearchFilter.cs              Incremental search: kana/width/case folding + matching
StartupManager.cs            "Run at sign-in" toggle (HKCU Run key)
Assets/app.ico               Application / tray icon
installer/DesktopQuickAccess.iss   Inno Setup script
dist/                        Compiled installer output (generated)
publish/                     Published self-contained exe output (generated)
```

## Building

Build and run locally:

```powershell
dotnet build DesktopQuickAccess.csproj -c Release
dotnet run --project DesktopQuickAccess.csproj -c Release
```

## Publishing a single-file executable

Produces a self-contained, single-file `DesktopQuickAccess.exe` that runs on
a machine without .NET installed:

```powershell
dotnet publish DesktopQuickAccess.csproj -c Release -r win-x64 `
  -p:SelfContained=true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish\win-x64
```

The output is written to `publish\win-x64\DesktopQuickAccess.exe`.

## Building the installer

With Inno Setup installed:

```powershell
& "C:\Program Files\Inno Setup 7\ISCC.exe" installer\DesktopQuickAccess.iss
```

This produces `dist\DesktopQuickAccessSetup.exe`. The installer:

- Installs per-user (no admin rights required) to
  `%LocalAppData%\Programs\Desktop Quick Access`.
- Lets the user choose a desktop shortcut (off by default) and launch at
  sign-in (on by default).
- Automatically detects and asks to close a running instance before
  upgrading/uninstalling.
- Cleans up the "run at sign-in" registry entry on uninstall.

## Notes / limitations

- The always-visible tray icon behavior and the hover-to-scroll behavior rely
  on undocumented Windows/.NET internals (registry keys and reflection into
  private WinForms members). They may stop working in a future Windows or
  .NET update; the app degrades gracefully (icon just stays hidden by
  default, scroll buttons still work with click-and-hold) if they do.

## License

MIT — see [LICENSE](LICENSE).
