# Minecraft Server Manager — QA issues and proposed fixes

**QA pass:** 4 September 2026  
**Baseline:** commit `18ceade` plus the stability and pack-builder changes prepared for the next release  
**Scope tested:** startup, restored/maximised and compact windows, profiles, server lifecycle, console, players, dashboard editors, map, files, mods/plugins, modpack discovery, pack builder, Java management, settings, and updater presentation.

## Status key

- **Fixed in this update** — implemented and covered by the release checks described below.
- **Partly fixed** — the highest-risk part is resolved, with a clearly bounded follow-up remaining.
- **Planned** — confirmed issue or usability improvement retained for a later stability pass.
- **Verified working** — exercised successfully during this QA pass; keep under regression coverage.

## Issues and proposed fixes

| ID | Area | Finding and impact | Proposed fix | Status |
| --- | --- | --- | --- | --- |
| QA-01 | Profiles / multi-server | Importing or opening a profile could leave previously selected profiles ticked for bulk start. **Start selected** could therefore launch more servers than the user intended. | Make the imported/opened profile the sole bulk selection. If more than one runnable profile is selected, show a confirmation listing every server before starting any of them. | **Fixed in this update** |
| QA-02 | Map / compact window | At approximately 900 × 600, player, follow, and POI controls could extend below the visible map panel with no way to reach them. | Make the map control rail vertically scrollable while retaining the existing resizable map/content split. | **Fixed in this update** |
| QA-03 | Mods & plugins | A profile classified as Forge could contain both `mods` and `plugins`, while the content page hid plugin management because it trusted only the stored server type. | Promote capabilities from actual non-empty content folders. Keep server-specific classification in detection/inventory services rather than hard-coding it in the view. | **Fixed in this update** |
| QA-04 | Pack dependencies | Content whose provider did not declare a recognised client/server side opened a warning with no safe way to continue. | Require an explicit **Install on** choice for every undecided item: client and server, client only, or server only. Do not enable confirmation until each placement is reviewed, and retain the choice through dependency recalculation and final preflight. | **Fixed in this update** |
| QA-05 | Pack dependencies | A selected file with an impossible required dependency chain produced a dead end even when another compatible published version existed. | Test other versions compatible with the chosen Minecraft version and loader. Offer one explicit recommended version; never silently change versions. Re-resolve the complete draft immediately before download. | **Fixed in this update** |
| QA-06 | Pack search | Added projects disappeared from results, moving the list and making it hard to remember what was already selected. | Preserve the result collection and scroll position, mark matching cards **In draft**, and disable their add action. | **Fixed in this update** |
| QA-07 | CurseForge dependencies | Some dependency cards displayed a numeric project ID instead of a human-readable title and icon. | Hydrate CurseForge dependency metadata in bounded batches before presenting the review. Keep numeric IDs only as an internal fallback if the provider cannot return metadata. | **Fixed in this update** |
| QA-08 | CurseForge connection | A personal API key had to be entered on each installation, while bundling a literal key in source would expose it in Git history. | Let release builds receive the approved application key from a GitHub Actions secret and retain Windows Credential Manager as an optional local override. Make clear that any key distributed in a desktop binary can be extracted, so provider-side restrictions and rotation remain necessary. | **Fixed in this update** |
| QA-09 | Player history | Renaming a server profile did not update that profile's label in persisted playtime history. | Update the stored profile display name by stable profile ID and queue the change through the existing JSON persistence path. | **Fixed in this update** |
| QA-10 | Dashboard editor | Generic `server.properties` profiles did not show known numeric limits for common settings. | Supply conservative built-in limits for ports, player count, view/simulation distance, spawn protection, idle timeout, world size, permission levels, broadcast range, compression threshold, and rate limit; allow profile schema values to override them. Add known enumerations only where semantics are version-stable. | **Partly fixed** — numeric guidance is included; generic difficulty and game-mode choices remain planned |
| QA-11 | Pack search paging | **Show more results** keeps a cumulative result list but the summary says “page 2”, which can sound as though only page 2 is visible. | Change the copy to “40 loaded from 2 pages” for cumulative mode, or make numbered page selection replace the collection consistently. | **Planned** |
| QA-12 | Similar-content discovery | Category-only similarity can return technically tagged but irrelevant content, such as shader patches under a broad technology category. | Rank candidates with weighted purpose, environment, loader, game version, dependency relationships, description terms, and negative category signals. Label these as inferred recommendations, never compatibility guarantees. | **Planned** |
| QA-13 | Modrinth dependencies | A dependency can fall back to a filename-like title such as `Forge-1.20.1-1.0.3` rather than the project's public title and icon. | Add the same bounded project-metadata hydration used for CurseForge and cache it by provider/project ID. | **Planned** |
| QA-14 | Server content search | Server-compatible results can include client-only or server-optional projects that are normally unnecessary on a dedicated server. | Add a relevance control: **Required on server**, **Works on server**, and **All compatible**. Default server-only builds to the first two without hiding an explicit user choice. | **Planned** |
| QA-15 | Console ordering | A very fast server response can appear immediately before the locally echoed command row. | Append the manager command row atomically before writing to standard input, on the UI queue, then accept redirected output. Add a deterministic ordering test with an immediate fake response. | **Planned** |
| QA-16 | Console contrast | Some output appeared unusually dim after command activity. | Audit semantic brushes and opacity for every log level in light, dark, and high-contrast modes; do not derive log severity colours from the user's accent. | **Planned** |
| QA-17 | Dashboard mode copy | In **Text Editor** mode the header can still say “31 friendly settings”, which describes availability rather than the active editor and reads as contradictory. | Show active-mode copy separately from capability copy, for example “Text Editor · 31 friendly controls available”. | **Planned** |
| QA-18 | Startup state | Before asynchronous profile loading completes, the overview can briefly show incomplete profile details and potentially misleading start/EULA controls. | Add an explicit restoring state, reserve the final layout dimensions, and keep server actions disabled until profile validation and EULA state are known. | **Planned** |
| QA-19 | Debug/update presentation | Non-release debug builds identify as v0.1.0 and can leave the update card at “Updater is starting…”. | Display a clear **Development build** badge, suppress installed-release actions when Velopack is unavailable, and keep end-to-end updater verification on the signed/published build. | **Planned** |
| QA-20 | Accessibility | Icon-only **Up** and **Refresh** buttons on Server Files did not expose meaningful accessible names. | Add automation names and keep tooltips as supplementary help. | **Fixed in this update** |

## Verified working in this QA pass

- Profile import, icon discovery, rename, Java selection, and Java recommendations.
- Java launch with redirected stdout/stderr; incremental coloured console rows and auto-scroll.
- One-second CPU and memory history, with the usage rail resizable from the console.
- Direct commands, broadcast, save-now, safe stop, emergency stop, and unexpected-exit handling.
- Closing the manager safely stops the running test server; no orphan Java process remained.
- Per-profile player playtime persists across app restart.
- User Friendly and Text Editor configuration modes, reload/discard, backup creation, and supported properties/CFG/JSON/YAML parsing.
- Map dimension selection, player follow, offline saved positions, and fixed-screen-size player markers while zooming.
- Multi-provider catalogue search, result-size selection, page loading, draft removal/sorting, optional dependency review, and explicit placement review.
- Theme, accent colour, Mica backdrop, update interval, changelog display, and managed Java scanning.
- Overview content centres after restoration, and the maximised window state is restored.

## Release checks

The prepared update must pass all of these gates before publication:

1. Debug x64 build with zero compiler errors.
2. Release x64 build with zero compiler errors.
3. The configuration, pack-builder, content-management, map-parsing, launcher-detection, and Java-compatibility regression executable.
4. `git diff --check` and a scan confirming no literal CurseForge credential is committed.
5. Push to `main`, successful GitHub release workflow, and confirmation that the release contains installer/update assets.

The real Forge/client installation and a large third-party modpack were not downloaded or launched during this pass. Those actions can install provider content and require the user's explicit choices. Automated output/install tests cover the non-interactive paths, but a published-build acceptance run remains worthwhile.

## Next major update after this stability release

The next major milestone is the **compatibility-assisted pack build pipeline**:

1. Resolve and lock a complete client/server manifest, including required and chosen optional dependencies.
2. Download into a staging instance and verify hashes before changing a runnable instance.
3. Install the correct Minecraft and loader baseline into the manager-owned launcher and matching server profile.
4. Run staged client/server startup probes, capture loader errors, and map failures back to the responsible mod or dependency.
5. Offer version alternatives or removal actions, while leaving the final decision visible to the user.
6. Promote the staged output only after review, with an explicit Minecraft EULA acceptance step for server creation. The manager must never silently accept the EULA.

This keeps automated compatibility help advisory and auditable while still allowing the user to build and test manually when provider metadata is incomplete.
