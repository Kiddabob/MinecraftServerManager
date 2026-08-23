# Minecraft Server Manager

A WinUI 3 desktop application for running the existing Tekkit 1.6.4 server. The current scope is intentionally Tekkit-first: profile selection, validation, Java process control, live console output, safe shutdown, profile-scoped player playtime, profile-driven configuration editing, in-app server-file browsing, personalisation, and GitHub updates.

## Requirements

- Windows 10 version 1809 or later
- Visual Studio 2022 with the .NET desktop development tools, or the .NET 10 SDK
- The Windows App SDK runtime is bundled by this unpackaged x64 build
- A working Java 8 x64 runtime and Tekkit server directory

The starter profile is `src/MinecraftServerManager/Profiles/tekkit.json`. It currently points to:

- Server: `%USERPROFILE%\OneDrive\Tekkit Server`
- Java: `%USERPROFILE%\OneDrive\jre-legacy\bin\java.exe`
- JAR: `TekkitServer.jar`

You can also choose the folder containing `TekkitServer.jar` from the app's **Profiles** page. The app detects the packaged Tekkit definition and stores the resulting user profile under `%LocalAppData%\Kidda.MinecraftServerManager\UserProfiles`, so an app update does not overwrite it.

## Player playtime

The **Players** page begins tracking when a player joins a server launched by the manager. Tekkit's profile supplies the join and leave patterns; the generic process service contains no Minecraft-specific log rules. Each profile has an independent history, while **All servers** adds a player's profile totals together. Existing history is not backfilled.

Completed time and 30-second live checkpoints are stored under `%LocalAppData%\Kidda.MinecraftServerManager\PlayerPlaytime`. Duplicate join/leave messages are ignored, and any open sessions are closed when that profile's Java process exits.

## Server dashboard

The **Dashboard** page discovers editable files from the selected profile's `configurationSources`. A profile can describe core settings, mod configuration folders, plugin folders, or any combination, so hybrid mod-and-plugin servers do not need special cases in the editor service. The packaged Tekkit profile scans `server.properties`, supported text files under `config`, and an optional `plugins` folder when one is present.

Configuration files are read-only while that profile's Java process is active. Saving validates JSON and XML where applicable, refuses to overwrite a file that changed after it was opened, writes through a same-folder temporary file, and first stores the previous bytes under `%LocalAppData%\Kidda.MinecraftServerManager\ConfigurationBackups\<profile>`. Files over 2 MB, binary files, reparse points, and configuration paths outside the server root are not editable.

## Build

```powershell
dotnet build MinecraftServerManager.sln -c Debug -p:Platform=x64
```

The dependency-free configuration-service checks can be run with:

```powershell
dotnet run --project tests\MinecraftServerManager.ConfigurationTests -c Release
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

The installed app checks GitHub Releases on launch and then at the interval selected in **Settings** (from 5 minutes to daily). When a newer version exists, it downloads the update in the background and enables **Restart to update**. The update experience is controlled from the app; `Update.exe` is only the installed helper that replaces files after the app closes, and you do not need to run it manually. If Tekkit is running, the app sends the safe stop command first and will not apply the update unless the server exits.

Theme, accent colour, update interval, and last selected profile are stored under `%LocalAppData%\Kidda.MinecraftServerManager` for the current Windows account.

The update source is the public repository at `https://github.com/Kiddabob/MinecraftServerManager`. Raw source commits are not installed directly; the release workflow turns each push to `main` into a versioned Velopack release first.

Once this folder is pushed to GitHub, every push to `main` automatically publishes a new `0.1.<run number>` GitHub Release. You can also run the **Build and publish installer** workflow manually and optionally supply a semantic version such as `0.2.0`. These release assets are what the installed app detects and downloads.
