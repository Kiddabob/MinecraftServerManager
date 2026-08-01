# Minecraft Server Manager

An initial WinUI 3 desktop application for running the existing Tekkit 1.6.4 server. The current scope is intentionally limited to the project foundation and the working server-console path.

## Requirements

- Windows 10 version 1809 or later
- Visual Studio 2022 with the .NET desktop development tools, or the .NET 10 SDK
- The Windows App SDK runtime is bundled by this unpackaged x64 build
- A working Java 8 x64 runtime and Tekkit server directory

The starter profile is `src/MinecraftServerManager/Profiles/tekkit.json`. It currently points to:

- Server: `%USERPROFILE%\OneDrive\Tekkit Server`
- Java: `%USERPROFILE%\OneDrive\jre-legacy\bin\java.exe`
- JAR: `TekkitServer.jar`

Edit that JSON if any of those paths or the memory arguments change, then rebuild so the profile is copied beside the executable.

## Build

```powershell
dotnet build MinecraftServerManager.sln -c Debug -p:Platform=x64
```

The app validates the configured paths before enabling Start. It sends the profile's `stop` command and waits up to 60 seconds for a safe exit; it does not force-kill Java after a timeout.

## Installer and shortcuts

Build a versioned per-user installer with:

```powershell
.\scripts\build-release.ps1 -Version 0.1.0
```

Install the newest locally built release with:

```powershell
.\scripts\install-local.ps1
```

The one-click installer places the app under `%LocalAppData%\Kidda.MinecraftServerManager` and creates Desktop and Start Menu shortcuts. It also installs a stable launcher and `Update.exe`, so shortcuts continue to work after an update.

## GitHub updates

The installed app checks GitHub Releases on launch and every 15 minutes. When a newer version exists, it downloads the update in the background and enables **Restart to update**. The update experience is controlled from the app; `Update.exe` is only the installed helper that replaces files after the app closes, and you do not need to run it manually. If Tekkit is running, the app sends the safe stop command first and will not apply the update unless the server exits.

The update source is the public repository at `https://github.com/Kiddabob/MinecraftServerManager`. Raw source commits are not installed directly; the release workflow turns each push to `main` into a versioned Velopack release first.

Once this folder is pushed to GitHub, every push to `main` automatically publishes a new `0.1.<run number>` GitHub Release. You can also run the **Build and publish installer** workflow manually and optionally supply a semantic version such as `0.2.0`. These release assets are what the installed app detects and downloads.
