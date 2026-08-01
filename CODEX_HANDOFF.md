# Minecraft Server Manager — Codex Project Handoff

## 1. Project Summary

Build a modern Windows desktop application for launching, monitoring and managing Minecraft Java Edition servers.

The application should be built using:

- C#
- .NET
- WinUI 3
- Windows App SDK
- MVVM architecture

Tekkit for Minecraft 1.6.4 is the first server profile to support, but the application must be designed so that additional server types and Java versions can be added without rewriting the core application.

The long-term application name is:

**Minecraft Server Manager**

Tekkit is a server profile, not the identity of the entire application.

---

## 2. Immediate Objective

Create the first working WinUI 3 version with support for:

- Loading a Tekkit server profile from JSON
- Validating required files and paths
- Starting the Tekkit Java server
- Capturing standard output and standard error
- Displaying a live server console
- Sending console commands to the Java process
- Detecting when the server is ready
- Stopping the server safely
- Showing server state
- Showing basic process information
- Preserving modularity for future server profiles

The first implementation should prioritise reliability and maintainability over advanced features.

---

## 3. Current Tekkit Server Details

### Minecraft and Forge

- Server name: Tekkit
- Minecraft version: 1.6.4
- Server type: Forge
- Forge version visible in the client:
  - Forge 9.11.1.965
- Mod count reported by Forge:
  - Approximately 117 loaded mods
- Mod files currently present:
  - Approximately 73 files
- Client includes OptiFine
- Client uses a 128× resource pack with textures for most mods

### Server directory

Current server directory:

```text
%USERPROFILE%\OneDrive\Tekkit Server
