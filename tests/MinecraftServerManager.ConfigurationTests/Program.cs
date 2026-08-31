using MinecraftServerManager.Models;
using MinecraftServerManager.Services;
using MinecraftServerManager.ViewModels;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args is ["--detect", .. var folders])
{
    foreach (var folder in folders)
    {
        var detection = ServerFolderDetector.Detect(folder);
        Console.WriteLine(detection is null
            ? $"{folder}: no launcher detected"
            : $"{folder}: {detection.DisplayName} • Java {detection.JavaExecutable} • JVM {CommandLineArgumentParser.Join(detection.EffectiveJavaArguments)} • Server {CommandLineArgumentParser.Join(detection.EffectiveServerArguments)}");
    }

    return;
}

if (args is ["--java", .. var executables])
{
    var runtimes = await new JavaRuntimeService().DiscoverAsync(executables);
    foreach (var runtime in runtimes)
    {
        Console.WriteLine(runtime.DisplayName);
    }

    return;
}

if (args is ["--content", .. var contentFolders])
{
    foreach (var folder in contentFolders)
    {
        var detection = ServerFolderDetector.Detect(folder);
        var contentProfile = new ServerProfile
        {
            Id = $"content-scan-{Guid.NewGuid():N}",
            DisplayName = new DirectoryInfo(folder).Name,
            ServerDirectory = folder,
            ServerType = detection?.ServerType ?? "Minecraft",
            MinecraftVersion = detection?.MinecraftVersion ?? "Unknown"
        };
        var inventory = ServerContentInventoryService.Discover(contentProfile);
        Console.WriteLine($"{folder}: {inventory.EnvironmentText} • {inventory.ItemCountText} • {inventory.TargetSummary}");
        foreach (var item in inventory.Items)
        {
            Console.WriteLine($"  {item.KindText}: {item.Name} • {item.VersionText} • {item.FileName}");
        }
    }

    return;
}

if (args is ["--modpack-search", .. var modpackTerms])
{
    var query = string.Join(' ', modpackTerms).Trim();
    var catalog = new ModpackCatalogService(
    [
        new ModrinthModpackCatalogService(),
        new TechnicModpackCatalogService(),
        new FtbModpackCatalogService()
    ]);
    var page = await catalog.SearchAsync(query);
    foreach (var status in page.ProviderStatuses)
    {
        Console.WriteLine(
            $"{status.ProviderName}: {(status.Succeeded ? status.Message : $"unavailable - {status.Message}")}");
    }

    foreach (var pack in page.Items)
    {
        Console.WriteLine($"  [{pack.ProviderName}] {pack.Title} ({pack.Slug})");
    }

    var exactTechnicPack = page.Items.FirstOrDefault(pack =>
        pack.ProviderId.Equals("technic", StringComparison.OrdinalIgnoreCase)
        && (pack.Title.Equals(query, StringComparison.CurrentCultureIgnoreCase)
            || pack.Slug.Equals(query, StringComparison.OrdinalIgnoreCase)));
    if (exactTechnicPack is not null)
    {
        var versions = await catalog.GetVersionsAsync(exactTechnicPack);
        foreach (var version in versions)
        {
            Console.WriteLine(
                $"    {version.DisplayName} - {version.ImportReadinessText}");
        }
    }

    return;
}

if (args is ["--content-search", var searchMinecraftVersion, var searchKind, var searchLoaders, .. var searchTerms])
{
    var kind = searchKind.Equals("plugin", StringComparison.OrdinalIgnoreCase)
        ? ServerContentKind.Plugin
        : ServerContentKind.Mod;
    var loaders = searchLoaders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var catalog = new ModrinthServerContentCatalogService();
    var page = await catalog.SearchAsync(
        string.Join(' ', searchTerms),
        searchMinecraftVersion,
        kind,
        loaders);
    Console.WriteLine($"{page.TotalHits:N0} compatible projects found; showing {page.Items.Count:N0}.");
    foreach (var project in page.Items)
    {
        Console.WriteLine($"  {project.Title} • {project.MetadataText}");
    }

    if (page.Items.FirstOrDefault() is { } firstProject)
    {
        var versions = await catalog.GetVersionsAsync(
            firstProject.ProjectId,
            searchMinecraftVersion,
            loaders);
        Console.WriteLine($"{firstProject.Title}: {versions.Count:N0} verified compatible versions.");
    }

    return;
}

if (args is ["--content-install-smoke", var smokeMinecraftVersion, var smokeKind, var smokeLoaders, .. var smokeTerms])
{
    var kind = smokeKind.Equals("plugin", StringComparison.OrdinalIgnoreCase)
        ? ServerContentKind.Plugin
        : ServerContentKind.Mod;
    var loaders = smokeLoaders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var query = string.Join(' ', smokeTerms).Trim();
    var catalog = new ModrinthServerContentCatalogService();
    var page = await catalog.SearchAsync(query, smokeMinecraftVersion, kind, loaders);
    var project = page.Items.FirstOrDefault(item => item.Slug.Equals(query, StringComparison.OrdinalIgnoreCase))
        ?? page.Items.FirstOrDefault()
        ?? throw new InvalidOperationException("No compatible Modrinth project was found for the smoke test.");
    var versions = await catalog.GetVersionsAsync(project.ProjectId, smokeMinecraftVersion, loaders);
    var version = versions.FirstOrDefault()
        ?? throw new InvalidOperationException("No compatible verified version was found for the smoke test.");

    var smokeBase = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "MinecraftServerManager-ContentInstallSmoke"));
    var smokeRoot = Path.Combine(smokeBase, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(smokeRoot);
    try
    {
        var profile = new ServerProfile
        {
            Id = $"content-install-smoke-{Guid.NewGuid():N}",
            DisplayName = "Content install smoke test",
            ServerDirectory = smokeRoot,
            ServerType = kind == ServerContentKind.Mod ? "Forge" : "Paper",
            MinecraftVersion = smokeMinecraftVersion
        };
        var target = new ServerContentTarget(
            kind,
            kind == ServerContentKind.Mod ? "mods" : "plugins",
            loaders);
        var installer = new ServerContentInstallService(catalog);
        var plan = await installer.CreatePlanAsync(profile, target, project, version);
        Console.WriteLine($"Installing {plan.Items.Count:N0} file(s), {plan.TotalBytes / 1024d / 1024d:0.0} MB, into a disposable folder.");
        var result = await installer.InstallAsync(
            plan,
            new Progress<ServerContentInstallProgress>(progress => Console.WriteLine(progress.Message)));
        if (result.InstalledFileCount != plan.Items.Count
            || result.InstalledFiles.Any(file => !File.Exists(Path.Combine(result.DestinationDirectory, file))))
        {
            throw new InvalidDataException("The content install smoke test did not commit every verified file.");
        }

        Console.WriteLine($"Installed and verified {result.InstalledFileCount:N0} file(s) for {project.Title} {version.VersionNumber}.");
    }
    finally
    {
        var normalizedRoot = Path.GetFullPath(smokeRoot);
        if (normalizedRoot.StartsWith(
                smokeBase.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(normalizedRoot))
        {
            Directory.Delete(normalizedRoot, recursive: true);
        }

        if (Directory.Exists(smokeBase)
            && !Directory.EnumerateFileSystemEntries(smokeBase).Any())
        {
            Directory.Delete(smokeBase);
        }
    }

    return;
}

var testRoot = Path.Combine(
    Path.GetTempPath(),
    "MinecraftServerManager-ConfigurationTests",
    Guid.NewGuid().ToString("N"));

try
{
    Directory.CreateDirectory(Path.Combine(testRoot, "config", "ExampleMod"));
    Directory.CreateDirectory(Path.Combine(testRoot, "mods"));
    Directory.CreateDirectory(Path.Combine(testRoot, "plugins", "ExamplePlugin"));
    await File.WriteAllTextAsync(
        Path.Combine(testRoot, "server.properties"),
        "motd=Test server\nmax-players=10\n");
    await File.WriteAllTextAsync(
        Path.Combine(testRoot, "config", "ExampleMod", "example.cfg"),
        "enabled=true\n");
    await File.WriteAllTextAsync(
        Path.Combine(testRoot, "plugins", "ExamplePlugin", "config.yml"),
        "enabled: true\n");
    await File.WriteAllTextAsync(
        Path.Combine(testRoot, "settings.json"),
        "{\"enabled\":true}\n");
    await File.WriteAllTextAsync(
        Path.Combine(testRoot, "bom.json"),
        "{\"message\":\"BOM preserved\"}\n",
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    var fabricModPath = Path.Combine(testRoot, "mods", "example-fabric.jar");
    CreateJarWithTextEntries(
        fabricModPath,
        new Dictionary<string, string>
        {
            ["fabric.mod.json"] = """
                {
                  "id": "example_fabric",
                  "name": "Example Fabric Mod",
                  "version": "2.4.0"
                }
                """
        });
    File.Copy(fabricModPath, Path.Combine(testRoot, "mods", "example-fabric.jar.disabled"));
    CreateJarWithTextEntries(
        Path.Combine(testRoot, "mods", "example-forge.jar"),
        new Dictionary<string, string>
        {
            ["META-INF/mods.toml"] = """
                [[mods]]
                modId="example_forge"
                version="1.3.0"
                displayName="Example Forge Mod"
                """
        });
    CreateJarWithTextEntries(
        Path.Combine(testRoot, "plugins", "ExamplePlugin", "example-plugin.jar"),
        new Dictionary<string, string>
        {
            ["plugin.yml"] = """
                name: Example Plugin
                version: 5.1.0
                main: example.plugin.Main
                """
        });

    var profile = new ServerProfile
    {
        Id = $"configuration-test-{Guid.NewGuid():N}",
        DisplayName = "Hybrid test",
        ServerType = "Hybrid",
        MinecraftVersion = "1.20.1",
        ServerDirectory = testRoot,
        ConfigurationSources =
        [
            new ServerConfigurationSource
            {
                Id = "core",
                DisplayName = "Server settings",
                Category = "Core",
                RelativePath = "server.properties",
                FilePatterns = ["server.properties"]
            },
            new ServerConfigurationSource
            {
                Id = "mods",
                DisplayName = "Mod configurations",
                Category = "Mods",
                RelativePath = "config",
                FilePatterns = ["*.cfg"],
                Recursive = true
            },
            new ServerConfigurationSource
            {
                Id = "plugins",
                DisplayName = "Plugin configurations",
                Category = "Plugins",
                RelativePath = "plugins",
                FilePatterns = ["*.yml"],
                Recursive = true
            },
            new ServerConfigurationSource
            {
                Id = "additional",
                DisplayName = "Additional settings",
                Category = "Other",
                RelativePath = string.Empty,
                FilePatterns = ["*.json"]
            }
        ],
        ConfigurationSchemas =
        [
            new ServerConfigurationSchema
            {
                FilePattern = "server.properties",
                Fields =
                [
                    new ServerConfigurationFieldDefinition
                    {
                        Key = "max-players",
                        DisplayName = "Maximum players",
                        Kind = "Integer",
                        Minimum = 1,
                        Maximum = 100,
                        Step = 1
                    },
                    new ServerConfigurationFieldDefinition
                    {
                        Key = "gamemode",
                        DisplayName = "Default game mode",
                        Kind = "Choice",
                        Presentation = "Radio",
                        Options =
                        [
                            new ServerConfigurationOptionDefinition { Value = "0", DisplayName = "Survival" },
                            new ServerConfigurationOptionDefinition { Value = "1", DisplayName = "Creative" }
                        ]
                    }
                ]
            }
        ]
    };

    var contentInventory = ServerContentInventoryService.Discover(profile);
    AssertTrue(contentInventory.SupportsMods, "hybrid content inventory supports mods");
    AssertTrue(contentInventory.SupportsPlugins, "hybrid content inventory supports plugins");
    AssertEqual(4, contentInventory.Items.Count, "installed content discovery count");
    var fabricContent = contentInventory.Items.Single(item =>
        item.FileName.Equals("example-fabric.jar", StringComparison.OrdinalIgnoreCase));
    AssertEqual("Example Fabric Mod", fabricContent.Name, "Fabric metadata display name");
    AssertEqual("example_fabric", fabricContent.Id, "Fabric metadata ID");
    AssertEqual("2.4.0", fabricContent.Version, "Fabric metadata version");
    AssertEqual("fabric", fabricContent.Loader, "Fabric metadata loader");
    AssertTrue(fabricContent.IsEnabled, "enabled content state");
    AssertTrue(
        !contentInventory.Items.Single(item => item.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)).IsEnabled,
        "disabled content state");
    var forgeContent = contentInventory.Items.Single(item =>
        item.FileName.Equals("example-forge.jar", StringComparison.OrdinalIgnoreCase));
    AssertEqual("Example Forge Mod", forgeContent.Name, "Forge metadata display name");
    AssertEqual("example_forge", forgeContent.Id, "Forge metadata ID");
    var pluginContent = contentInventory.Items.Single(item =>
        item.FileName.Equals("example-plugin.jar", StringComparison.OrdinalIgnoreCase));
    AssertEqual(ServerContentKind.Plugin, pluginContent.Kind, "plugin content kind");
    AssertEqual("Example Plugin", pluginContent.Name, "Bukkit plugin metadata display name");
    AssertEqual("5.1.0", pluginContent.Version, "Bukkit plugin metadata version");
    var forgeOnlyInventory = ServerContentInventoryService.Discover(new ServerProfile
    {
        Id = $"forge-content-test-{Guid.NewGuid():N}",
        DisplayName = "Forge content test",
        ServerType = "Forge",
        MinecraftVersion = "1.20.1",
        ServerDirectory = testRoot
    });
    AssertTrue(forgeOnlyInventory.SupportsMods, "Forge content inventory supports mods");
    AssertTrue(!forgeOnlyInventory.SupportsPlugins, "stray plugins folder does not make Forge hybrid");
    AssertTrue(
        forgeOnlyInventory.Items.All(item => item.Kind == ServerContentKind.Mod),
        "Forge inventory excludes unrelated plugin-folder files");

    var service = new ServerConfigurationService(Path.Combine(testRoot, "backups"));
    var discovery = await service.DiscoverAsync(profile);
    AssertEqual(5, discovery.Files.Count, "profile-defined discovery count");
    AssertTrue(
        discovery.Sources.Any(source => source.Category == "Mods" && source.IsPresent && source.FileCount == 1),
        "mod configuration source detection");
    AssertTrue(
        discovery.Sources.Any(source => source.Category == "Plugins" && source.IsPresent && source.FileCount == 1),
        "plugin configuration source detection");

    var propertiesFile = discovery.Files.Single(file => file.Name == "server.properties");
    var propertiesDocument = await service.ReadAsync(profile, propertiesFile);
    var saved = await service.SaveAsync(
        profile,
        propertiesDocument,
        propertiesDocument.Content.Replace("max-players=10", "max-players=12", StringComparison.Ordinal));
    AssertTrue(File.Exists(saved.BackupPath), "pre-save backup creation");
    AssertTrue(
        (await File.ReadAllTextAsync(propertiesFile.FullPath)).Contains("max-players=12", StringComparison.Ordinal),
        "atomic configuration save");

    var conflictDocument = await service.ReadAsync(profile, propertiesFile);
    await File.AppendAllTextAsync(propertiesFile.FullPath, "online-mode=true\n");
    await AssertThrowsAsync<InvalidOperationException>(
        () => service.SaveAsync(profile, conflictDocument, conflictDocument.Content + "pvp=true\n"),
        "external-change conflict protection");

    var jsonFile = discovery.Files.Single(file => file.Name == "settings.json");
    var jsonDocument = await service.ReadAsync(profile, jsonFile);
    await AssertThrowsAsync<InvalidDataException>(
        () => service.SaveAsync(profile, jsonDocument, "{not valid json}"),
        "JSON validation before save");

    var bomFile = discovery.Files.Single(file => file.Name == "bom.json");
    var bomDocument = await service.ReadAsync(profile, bomFile);
    AssertTrue(bomDocument.HasByteOrderMark, "UTF-8 BOM detection");
    await service.SaveAsync(profile, bomDocument, bomDocument.Content.Replace("preserved", "retained"));
    var savedBom = await File.ReadAllBytesAsync(bomFile.FullPath);
    AssertTrue(
        savedBom.AsSpan().StartsWith(System.Text.Encoding.UTF8.Preamble),
        "UTF-8 BOM preservation after save");

    var outsideFile = new ServerConfigurationFile(
        "outside",
        "Outside",
        "Other",
        "outside.txt",
        "outside.txt",
        Path.Combine(Path.GetDirectoryName(testRoot)!, "outside.txt"),
        0,
        DateTime.UtcNow);
    await AssertThrowsAsync<InvalidOperationException>(
        () => service.ReadAsync(profile, outsideFile),
        "server-root containment");

    var editor = new ServerConfigurationEditorService();
    const string propertyText = "# Keep this comment\r\nmax-players=10\r\ngamemode=0\r\nonline-mode=true\r\nmotd=Test server\r\n";
    var friendlyProperties = editor.Parse(profile, propertiesFile, propertyText);
    AssertEqual(4, friendlyProperties.Fields.Count, "friendly server property count");
    var maximumPlayers = friendlyProperties.Fields.Single(field => field.Key == "max-players");
    AssertEqual(1d, maximumPlayers.DeclaredMinimum!.Value, "profile minimum guidance");
    AssertEqual(100d, maximumPlayers.DeclaredMaximum!.Value, "profile maximum guidance");
    maximumPlayers.NumericValue = 12;
    friendlyProperties.Fields.Single(field => field.Key == "online-mode").BooleanValue = false;
    friendlyProperties.Fields.Single(field => field.Key == "gamemode").SelectedOption =
        friendlyProperties.Fields.Single(field => field.Key == "gamemode").Options[1];
    var updatedProperties = editor.Apply(friendlyProperties);
    AssertTrue(updatedProperties.Contains("# Keep this comment\r\n", StringComparison.Ordinal), "property comment preservation");
    AssertTrue(updatedProperties.Contains("max-players=12\r\n", StringComparison.Ordinal), "number-box property update");
    AssertTrue(updatedProperties.Contains("gamemode=1\r\n", StringComparison.Ordinal), "choice property update");
    AssertTrue(updatedProperties.Contains("online-mode=false\r\n", StringComparison.Ordinal), "toggle property update");

    var reopenedProperties = editor.Parse(profile, propertiesFile, updatedProperties);
    AssertEqual(
        "12",
        reopenedProperties.Fields.Single(field => field.Key == "max-players").NumericText,
        "number-box value survives friendly editor rebuild");
    AssertTrue(
        !reopenedProperties.Fields.Single(field => field.Key == "online-mode").BooleanValue,
        "toggle value survives friendly editor rebuild");
    AssertEqual(
        "1",
        reopenedProperties.Fields.Single(field => field.Key == "gamemode").SelectedOption!.Value,
        "choice value survives friendly editor rebuild");

    var unschematizedPropertiesFile = propertiesFile with
    {
        Name = "custom.properties",
        RelativePath = "config\\custom.properties",
        FullPath = Path.Combine(testRoot, "config", "custom.properties")
    };
    var unschematizedProperties = editor.Parse(profile, unschematizedPropertiesFile, "count=5\n");
    var unboundedNumber = unschematizedProperties.Fields.Single();
    AssertEqual(double.MinValue, unboundedNumber.Minimum, "unbounded number-box finite minimum");
    AssertEqual(double.MaxValue, unboundedNumber.Maximum, "unbounded number-box finite maximum");
    unboundedNumber.NumericText = "not-a-number";
    AssertTrue(!unboundedNumber.IsValid, "typed invalid number validation");
    AssertThrows<InvalidDataException>(
        () => editor.Apply(unschematizedProperties),
        "typed invalid number blocks apply");
    unboundedNumber.NumericText = "42";
    AssertTrue(unboundedNumber.IsValid, "typed number recovers from validation");
    AssertTrue(
        editor.Apply(unschematizedProperties).Contains("count=42", StringComparison.Ordinal),
        "typed number updates underlying properties text");

    const string forgeText = "general {\n    # Packet threshold, minimum 64, maximum 1024\n    I:clumpingThreshold=64\n    B:enableGlobalConfig=false\n}\n";
    var forgeFile = propertiesFile with
    {
        Name = "forge.cfg",
        RelativePath = "config\\forge.cfg",
        FullPath = Path.Combine(testRoot, "config", "forge.cfg")
    };
    var friendlyForge = editor.Parse(profile, forgeFile, forgeText);
    AssertEqual(2, friendlyForge.Fields.Count, "Forge typed setting count");
    var threshold = friendlyForge.Fields.Single(field => field.Key == "general.clumpingThreshold");
    AssertEqual(64d, threshold.DeclaredMinimum!.Value, "Forge comment minimum detection");
    AssertEqual(1024d, threshold.DeclaredMaximum!.Value, "Forge comment maximum detection");
    threshold.NumericValue = 128;
    var updatedForge = editor.Apply(friendlyForge);
    AssertTrue(updatedForge.Contains("I:clumpingThreshold=128", StringComparison.Ordinal), "Forge scalar round trip");
    AssertTrue(updatedForge.Contains("# Packet threshold, minimum 64, maximum 1024", StringComparison.Ordinal), "Forge comment round trip");

    const string jsonText = "{\n  \"enabled\": true,\n  \"limit\": 5,\n  \"name\": \"Example\"\n}\n";
    var jsonFriendly = editor.Parse(profile, jsonFile, jsonText);
    AssertEqual(3, jsonFriendly.Fields.Count, "JSON scalar setting count");
    jsonFriendly.Fields.Single(field => field.Key == "enabled").BooleanValue = false;
    jsonFriendly.Fields.Single(field => field.Key == "name").TextValue = "Updated";
    var updatedJson = editor.Apply(jsonFriendly);
    AssertTrue(updatedJson.Contains("\"enabled\": false", StringComparison.Ordinal), "JSON boolean round trip");
    AssertTrue(updatedJson.Contains("\"name\": \"Updated\"", StringComparison.Ordinal), "JSON string round trip");

    var fabricFolder = Path.Combine(testRoot, "fabric-server");
    Directory.CreateDirectory(fabricFolder);
    await File.WriteAllBytesAsync(Path.Combine(fabricFolder, "fabric-installer-1.0.jar"), [1, 2, 3, 4]);
    await File.WriteAllBytesAsync(Path.Combine(fabricFolder, "fabric-server-launch.jar"), [1]);
    var fabricDetection = ServerFolderDetector.Detect(fabricFolder);
    AssertEqual("fabric-server-launch.jar", fabricDetection!.ServerJar, "server launcher selection");
    AssertEqual("Fabric", fabricDetection.ServerType, "Fabric classification");

    var paperFolder = Path.Combine(testRoot, "paper-server");
    Directory.CreateDirectory(paperFolder);
    await File.WriteAllBytesAsync(Path.Combine(paperFolder, "paper-1.21.4-232.jar"), [1]);
    var paperDetection = ServerFolderDetector.Detect(paperFolder);
    AssertEqual("Paper", paperDetection!.ServerType, "Paper classification");
    AssertEqual("1.21.4", paperDetection.MinecraftVersion, "Minecraft version detection");

    var customFolder = Path.Combine(testRoot, "custom-server");
    Directory.CreateDirectory(customFolder);
    await File.WriteAllBytesAsync(Path.Combine(customFolder, "my-community-pack.jar"), [1]);
    AssertEqual("Minecraft", ServerFolderDetector.Detect(customFolder)!.ServerType, "unknown launcher fallback");

    var bytecodeFolder = Path.Combine(testRoot, "bytecode-server");
    Directory.CreateDirectory(bytecodeFolder);
    CreateJar(
        Path.Combine(bytecodeFolder, "community-launcher.jar"),
        "example.ServerMain",
        [],
        classFileMajor: 61);
    var bytecodeDetection = ServerFolderDetector.Detect(bytecodeFolder)!;
    AssertEqual(17, bytecodeDetection.RequiredJavaMajorVersion!.Value, "Java requirement from main class bytecode");

    var legendsFolder = Path.Combine(testRoot, "tekkit-legends");
    Directory.CreateDirectory(legendsFolder);
    Directory.CreateDirectory(Path.Combine(legendsFolder, "mods"));
    Directory.CreateDirectory(Path.Combine(legendsFolder, "plugins"));
    CreateJar(
        Path.Combine(legendsFolder, "CryofinityLegends.jar"),
        "cpw.mods.fml.relauncher.ServerLaunchWrapper",
        ["cpw/mods/fml/common/Loader.class"]);
    await File.WriteAllBytesAsync(Path.Combine(legendsFolder, "minecraft_server.1.7.10.jar"), [1]);
    await File.WriteAllTextAsync(
        Path.Combine(legendsFolder, "start.bat"),
        "@echo off\r\n:start\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\njava -Dfml.debugExit=true -Xms1G -jar \"CryofinityLegends.jar\" nogui -Dfml.debugExit=true\r\n");
    await File.WriteAllTextAsync(
        Path.Combine(legendsFolder, "start.sh"),
        "java -Xmx2G -Xms1G -jar \"TekkitLegends.jar\" nogui\n");
    CreateJar(
        Path.Combine(legendsFolder, "TekkitLegends.jar"),
        "cpw.mods.fml.relauncher.ServerLaunchWrapper",
        ["cpw/mods/fml/common/Loader.class"]);
    var legendsDetection = ServerFolderDetector.Detect(legendsFolder)!;
    AssertEqual("start.bat", legendsDetection.LaunchScript, "Legends launch script detection");
    AssertEqual("CryofinityLegends.jar", legendsDetection.ServerJar, "Legends scripted launcher selection");
    AssertEqual("1.7.10", legendsDetection.MinecraftVersion, "Legends companion JAR version detection");
    AssertEqual("Forge", legendsDetection.ServerType, "Legends executable JAR classification");
    AssertEqual(0, legendsDetection.EffectiveJavaArguments.Count, "Legends BAT JVM arguments ignored");
    AssertTrue(legendsDetection.EffectiveServerArguments.SequenceEqual(["nogui"]), "Legends safe server arguments");

    var classicFolder = Path.Combine(testRoot, "tekkit-classic");
    Directory.CreateDirectory(classicFolder);
    Directory.CreateDirectory(Path.Combine(classicFolder, "mods"));
    Directory.CreateDirectory(Path.Combine(classicFolder, "plugins"));
    CreateJar(
        Path.Combine(classicFolder, "TekkitClassic.jar"),
        "org.bukkit.craftbukkit.Main",
        ["cpw/mods/fml/common/Loader.class", "org/bukkit/craftbukkit/Main.class"]);
    await File.WriteAllTextAsync(
        Path.Combine(classicFolder, "launch.bat"),
        "set \"JAVA=C:\\Program Files\\Java\\jre1.8.0_191\\bin\\javaw.exe\"\r\n" +
        "set \"SERVER_JAR=TekkitClassic.jar\"\r\n" +
        "\"%JAVA%\" ^\r\n-Xms1G -Xmx6G ^\r\n-jar \"%SERVER_JAR%\" -o true\r\n");
    await File.WriteAllTextAsync(
        Path.Combine(classicFolder, "server.log"),
        "2012-06-01 10:20:30 [INFO] Starting minecraft server version 1.2.5\r\n" + new string('x', (2 * 1024 * 1024) + 20));
    var classicDetection = ServerFolderDetector.Detect(classicFolder)!;
    AssertEqual("launch.bat", classicDetection.LaunchScript, "Classic launch script detection");
    AssertEqual("TekkitClassic.jar", classicDetection.ServerJar, "Classic launcher selection");
    AssertEqual("1.2.5", classicDetection.MinecraftVersion, "Classic log version detection");
    AssertEqual("Hybrid", classicDetection.ServerType, "Classic Forge and CraftBukkit classification");
    AssertEqual(0, classicDetection.EffectiveJavaArguments.Count, "Classic BAT memory ignored");
    AssertTrue(classicDetection.EffectiveServerArguments.SequenceEqual(["nogui"]), "Classic BAT server arguments ignored");
    AssertEqual("java", classicDetection.JavaExecutable, "Classic BAT Java path ignored");

    var modernForgeFolder = Path.Combine(testRoot, "modern-forge");
    Directory.CreateDirectory(modernForgeFolder);
    Directory.CreateDirectory(Path.Combine(modernForgeFolder, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.3.0"));
    await File.WriteAllTextAsync(Path.Combine(modernForgeFolder, "user_jvm_args.txt"), "-Xmx4G\r\n");
    var winArgsPath = Path.Combine(modernForgeFolder, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.3.0", "win_args.txt");
    await File.WriteAllTextAsync(winArgsPath, "net.minecraftforge.server.ServerMain\r\n");
    await File.WriteAllTextAsync(
        Path.Combine(modernForgeFolder, "run.bat"),
        "java @user_jvm_args.txt @libraries/net/minecraftforge/forge/1.20.1-47.3.0/win_args.txt %*\r\n");
    var modernForgeDetection = ServerFolderDetector.Detect(modernForgeFolder)!;
    AssertEqual("run.bat", modernForgeDetection.LaunchScript, "modern Forge run script detection");
    AssertEqual("Forge", modernForgeDetection.ServerType, "modern Forge classification");
    AssertEqual("1.20.1", modernForgeDetection.MinecraftVersion, "modern Forge argument path version detection");
    AssertEqual(1, modernForgeDetection.EffectiveDirectLaunchArguments.Count, "only modern Forge main argument file preserved");
    AssertTrue(
        modernForgeDetection.EffectiveDirectLaunchArguments[0].Contains("win_args.txt", StringComparison.OrdinalIgnoreCase),
        "modern Forge main argument file selection");

    var replacedMemory = JavaArgumentUtilities.ReplaceMemoryArguments(
        ["-server", "-Xms512M", "-Xmx1G"],
        2048,
        6144,
        ["-Dexample=true"]);
    AssertTrue(replacedMemory.SequenceEqual(["-Xms2G", "-Xmx6G", "-server", "-Dexample=true"]), "memory argument replacement");

    var readinessFolder = Path.Combine(testRoot, "readiness");
    Directory.CreateDirectory(readinessFolder);
    var readinessProfile = new ServerProfile
    {
        Id = "readiness-test",
        DisplayName = "Readiness test",
        ServerDirectory = readinessFolder,
        ServerJar = "server.jar",
        JavaExecutable = "java",
        JavaVersion = "Java 17",
        JavaArguments = ["-Xms1G", "-Xmx4G"]
    };
    var readinessService = new ServerReadinessService(
        new StubProfileValidator(new ProfileValidationResult([])));

    var missingEulaReadiness = readinessService.Evaluate(readinessProfile);
    AssertEqual(ServerReadinessState.ActionRequired, missingEulaReadiness.State, "missing EULA readiness state");
    AssertEqual(ServerEulaState.Missing, missingEulaReadiness.EulaState, "missing EULA detection");
    AssertTrue(!missingEulaReadiness.CanStart, "missing EULA waits for explicit acceptance");
    AssertTrue(
        missingEulaReadiness.MemoryText.Contains("MB initial", StringComparison.Ordinal)
            && missingEulaReadiness.MemoryText.Contains("MB maximum", StringComparison.Ordinal),
        "readiness memory summary");

    var eulaPath = Path.Combine(readinessFolder, "eula.txt");
    var acceptedMissingEula = await readinessService.AcceptEulaAsync(readinessProfile);
    AssertEqual(ServerEulaState.Accepted, acceptedMissingEula.EulaState, "missing EULA acceptance");
    AssertTrue(
        (await File.ReadAllTextAsync(eulaPath)).Contains("eula=true", StringComparison.Ordinal),
        "accepted EULA file creation");

    await File.WriteAllTextAsync(
        eulaPath,
        "# Keep this comment\neula=false\nexample=value\neula=false\n");
    var pendingEulaReadiness = readinessService.Evaluate(readinessProfile);
    AssertEqual(ServerReadinessState.ActionRequired, pendingEulaReadiness.State, "pending EULA readiness state");
    AssertEqual(ServerEulaState.NotAccepted, pendingEulaReadiness.EulaState, "pending EULA detection");
    AssertTrue(!pendingEulaReadiness.CanStart, "pending EULA blocks launch");
    AssertEqual(eulaPath, pendingEulaReadiness.EulaPath, "readiness EULA path");

    var acceptedEulaReadiness = await readinessService.AcceptEulaAsync(readinessProfile);
    AssertEqual(ServerReadinessState.Ready, acceptedEulaReadiness.State, "accepted EULA readiness state");
    AssertEqual(ServerEulaState.Accepted, acceptedEulaReadiness.EulaState, "accepted EULA detection");
    AssertTrue(acceptedEulaReadiness.CanStart, "accepted EULA permits launch");
    var acceptedEulaText = await File.ReadAllTextAsync(eulaPath);
    AssertTrue(
        acceptedEulaText.Contains("# Keep this comment", StringComparison.Ordinal)
            && acceptedEulaText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line == "eula=true") == 2,
        "EULA acceptance preserves comments and normalizes duplicate settings");

    var invalidReadinessService = new ServerReadinessService(
        new StubProfileValidator(new ProfileValidationResult(["Server JAR is missing."])));
    var invalidProfileReadiness = invalidReadinessService.Evaluate(readinessProfile);
    AssertEqual(ServerReadinessState.ActionRequired, invalidProfileReadiness.State, "invalid profile readiness state");
    AssertTrue(!invalidProfileReadiness.CanStart, "invalid profile blocks launch");

    var runtimeService = new JavaRuntimeService();
    var installLocationService = new ModpackInstallLocationService(
        Path.Combine(testRoot, "local-app-data"));
    var managedInstancesDirectory = installLocationService.EnsureManagedInstancesDirectory();
    AssertEqual(
        Path.Combine(testRoot, "local-app-data", "Kidda.MinecraftServerManager", "Instances"),
        managedInstancesDirectory,
        "app-managed instances location");
    AssertTrue(
        Directory.Exists(managedInstancesDirectory),
        "app-managed instances directory creation");
    AssertEqual(8, runtimeService.GetRecommendedJavaMajor("1.16.5")!.Value, "Java 8 baseline");
    AssertEqual(16, runtimeService.GetRecommendedJavaMajor("1.17")!.Value, "Java 16 baseline");
    AssertEqual(17, runtimeService.GetRecommendedJavaMajor("1.18.2")!.Value, "Java 17 baseline");
    AssertEqual(21, runtimeService.GetRecommendedJavaMajor("1.20.5")!.Value, "Java 21 baseline");

    var recommendation = ServerLaunchRecommendationService.Create(
        legendsFolder,
        "Forge",
        "1.7.10",
        32L * 1024 * 1024 * 1024,
        32,
        8);
    AssertEqual(8, recommendation.JavaMajorVersion!.Value, "recommended legacy Java");
    AssertTrue(recommendation.MaximumMemoryMb >= 4096, "modded server memory recommendation");

    var managedOptions = new ManagedJavaRuntimeService(runtimeService).GetOptions();
    AssertEqual(4, managedOptions.Count, "managed Java option count");
    AssertTrue(
        managedOptions.Single(option => option.MajorVersion == 16).MinecraftVersions.Contains("1.17", StringComparison.Ordinal),
        "Java 16 Minecraft version label");

    using var latestAssetDocument = JsonDocument.Parse("""
        [{
          "binary": {
            "architecture": "x64",
            "image_type": "jre",
            "os": "windows",
            "package": {
              "checksum": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "link": "https://github.com/adoptium/temurin17-binaries/releases/download/test/java17.zip",
              "size": 43780109
            }
          }
        }]
        """);
    var latestAsset = ManagedJavaRuntimeService.ParseAssetMetadata(
        latestAssetDocument.RootElement,
        17);
    AssertEqual(43780109L, latestAsset.Size!.Value, "Adoptium latest binary package size");
    AssertTrue(
        latestAsset.DownloadUri.AbsolutePath.EndsWith("java17.zip", StringComparison.Ordinal),
        "Adoptium latest binary response shape");

    using var featureReleaseDocument = JsonDocument.Parse("""
        [{
          "binaries": [{
            "architecture": "x64",
            "image_type": "jdk",
            "os": "windows",
            "package": {
              "checksum": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "link": "https://github.com/adoptium/temurin16-binaries/releases/download/test/java16.zip",
              "size": 190000000
            }
          }]
        }]
        """);
    var featureReleaseAsset = ManagedJavaRuntimeService.ParseAssetMetadata(
        featureReleaseDocument.RootElement,
        16);
    AssertEqual(190000000L, featureReleaseAsset.Size!.Value, "Adoptium feature release package size");
    AssertTrue(
        featureReleaseAsset.DownloadUri.AbsolutePath.EndsWith("java16.zip", StringComparison.Ordinal),
        "Adoptium feature release binaries response shape");

    var installProgress = new ManagedJavaInstallProgress(
        ManagedJavaInstallStage.Downloading,
        "Downloading Java 17",
        25,
        100);
    AssertEqual(25d, installProgress.Percent!.Value, "managed Java progress percentage");

    using var modpackSearchDocument = JsonDocument.Parse("""
        {
          "hits": [{
            "project_id": "AABBCCDD",
            "slug": "example-pack",
            "title": "Example Pack",
            "description": "A server-capable example pack.",
            "author": "PackAuthor",
            "downloads": 12345,
            "icon_url": "https://cdn.modrinth.com/data/AABBCCDD/icon.png",
            "versions": ["1.20.1"],
            "display_categories": ["forge", "adventure"],
            "environment": ["client_and_server"]
          }],
          "offset": 0,
          "limit": 20,
          "total_hits": 1
        }
        """);
    var modpackSearch = ModrinthModpackCatalogService.ParseSearchResponse(
        modpackSearchDocument.RootElement);
    AssertEqual(1, modpackSearch.TotalHits, "Modrinth search total");
    AssertEqual("Example Pack", modpackSearch.Items.Single().Title, "Modrinth search title");
    AssertTrue(
        modpackSearch.Items.Single().IconUrl.StartsWith("https://cdn.modrinth.com/", StringComparison.Ordinal),
        "Modrinth icon host validation");

    using var technicSearchDocument = JsonDocument.Parse("""
        {
          "modpacks": [{
            "id": "552560",
            "name": "Tekkit Classic",
            "slug": "tekkit",
            "url": "https://www.technicpack.net/modpack/tekkit.552560",
            "iconUrl": "https://cdn.technicpack.net/platform2/pack-icons/552560.png"
          }]
        }
        """);
    var technicSearch = TechnicModpackCatalogService.ParseSearchResponse(
        technicSearchDocument.RootElement);
    var technicPack = technicSearch.Items.Single();
    AssertEqual("technic", technicPack.ProviderId, "Technic provider identity");
    AssertEqual("Tekkit Classic", technicPack.Title, "Technic Tekkit search result");
    AssertEqual(
        "Download count not supplied",
        technicPack.DownloadsText,
        "Technic unavailable download count");
    AssertTrue(
        technicPack.IconUrl.StartsWith("https://cdn.technicpack.net/", StringComparison.Ordinal),
        "Technic icon host validation");

    using var technicPackDocument = JsonDocument.Parse("""
        {
          "id": 552560,
          "name": "tekkit",
          "displayName": "Tekkit Classic",
          "user": "sct",
          "minecraft": "1.2.5",
          "version": "3.1.2",
          "isOfficial": true,
          "serverPackUrl": "https://servers.technicpack.net/Technic/servers/tekkit/Tekkit_Server_3.1.2.zip",
          "feed": [{ "date": 1720000000 }]
        }
        """);
    var technicVersion = TechnicModpackCatalogService.ParsePackResponse(
        technicPackDocument.RootElement,
        technicPack).Single();
    AssertEqual("1.2.5", technicVersion.MinecraftVersions.Single(), "Technic Minecraft version");
    AssertEqual(
        ModpackPackageKind.TechnicServerArchive,
        technicVersion.PackFile!.PackageKind,
        "Technic server archive selection");
    AssertTrue(technicVersion.IsServerCompatible, "Technic published server archive compatibility");

    using var untrustedTechnicPackDocument = JsonDocument.Parse("""
        {
          "id": 552560,
          "name": "tekkit",
          "displayName": "Tekkit Classic",
          "minecraft": "1.2.5",
          "version": "3.1.2",
          "serverPackUrl": "https://example.invalid/untrusted.zip"
        }
        """);
    AssertTrue(
        TechnicModpackCatalogService.ParsePackResponse(
            untrustedTechnicPackDocument.RootElement,
            technicPack).Single().PackFile is null,
        "Technic third-party server archive rejection");

    using var ftbSearchDocument = JsonDocument.Parse("""
        { "status": "success", "packs": [88, 88, 126], "count": 2 }
        """);
    AssertEqual(2, FtbModpackCatalogService.ParsePackIds(ftbSearchDocument.RootElement).Count, "FTB search IDs");

    using var ftbPackDocument = JsonDocument.Parse("""
        {
          "status": "success",
          "id": 88,
          "name": "FTB Academy 1.16",
          "slug": "ftb-academy-116",
          "synopsis": "A modpack for beginners",
          "installs": 125273,
          "tags": [{ "name": "FTB" }, { "name": "Tech" }],
          "authors": [{ "name": "FTB Team" }],
          "art": [{
            "url": "https://cdn.feed-the-beast.com/blob/example.png",
            "type": "square"
          }],
          "versions": [{
            "id": 100026,
            "name": "1.4.1",
            "type": "release",
            "released": 1739361055,
            "targets": [
              { "name": "minecraft", "version": "1.16.5", "type": "game" },
              { "name": "forge", "version": "36.2.34", "type": "modloader" },
              { "name": "java", "version": "8.0.312+7", "type": "runtime" }
            ]
          }]
        }
        """);
    var ftbPack = FtbModpackCatalogService.ParsePackItem(ftbPackDocument.RootElement)!;
    AssertEqual("ftb", ftbPack.ProviderId, "FTB provider identity");
    AssertEqual("FTB Academy 1.16", ftbPack.Title, "FTB catalogue title");
    var ftbVersion = FtbModpackCatalogService.ParseVersions(
        ftbPackDocument.RootElement,
        ftbPack).Single();
    AssertEqual("forge", ftbVersion.Loaders.Single(), "FTB loader metadata");
    AssertEqual(
        ModpackPackageKind.FtbManifest,
        ftbVersion.PackFile!.PackageKind,
        "FTB manifest package selection");

    using var contentSearchDocument = JsonDocument.Parse("""
        {
          "hits": [{
            "project_id": "CONTENT1",
            "slug": "example-content",
            "project_type": "mod",
            "all_project_types": ["mod"],
            "title": "Example Content",
            "description": "A server-side example mod.",
            "author": "ContentAuthor",
            "downloads": 6789,
            "icon_url": "https://cdn.modrinth.com/data/CONTENT1/icon.png",
            "versions": ["1.20.1"],
            "display_categories": ["forge"],
            "environment": ["server_only"]
          }],
          "offset": 0,
          "limit": 20,
          "total_hits": 1
        }
        """);
    var contentSearch = ModrinthServerContentCatalogService.ParseSearchResponse(
        contentSearchDocument.RootElement,
        ServerContentKind.Mod);
    AssertEqual(1, contentSearch.TotalHits, "Modrinth content search total");
    AssertEqual(ServerContentKind.Mod, contentSearch.Items.Single().Kind, "Modrinth content kind");
    var contentFacets = ModrinthServerContentCatalogService.BuildSearchFacets(
        "1.20.1",
        ServerContentKind.Mod,
        ["forge"]);
    AssertTrue(
        contentFacets.Any(group => group.Contains("project_type:mod")),
        "content search project type facet");
    AssertTrue(
        contentFacets.Any(group => group.Contains("versions:1.20.1")),
        "content search Minecraft version facet");
    AssertTrue(
        contentFacets.Any(group => group.Contains("categories:forge")),
        "content search loader facet");
    var pluginFacets = ModrinthServerContentCatalogService.BuildSearchFacets(
        "1.20.1",
        ServerContentKind.Plugin,
        ["paper"]);
    AssertTrue(
        pluginFacets.Any(group => group.Contains("all_project_types:plugin")),
        "content search multi-type plugin facet");
    var builderFacets = ModrinthServerContentCatalogService.BuildPackSearchFacets(
        new PackCatalogSearchRequest(
            "storage",
            "1.20.1",
            ServerContentKind.Mod,
            PackBuildTarget.ClientAndServer,
            ["fabric"],
            ["storage"]));
    AssertTrue(
        builderFacets.Any(group => group.Contains("categories:fabric")),
        "builder search loader facet");
    AssertTrue(
        builderFacets.Any(group => group.Contains("categories:storage")),
        "builder search category facet");
    AssertTrue(
        builderFacets.Any(group => group.Contains("environment:client_only"))
        && builderFacets.Any(group => group.Contains("environment:server_only")),
        "linked builder searches both client and server environments");

    var contentVersionsJson = """
        [{
          "id": "CONTENT_VERSION",
          "project_id": "CONTENT1",
          "name": "Example Content 3.0",
          "version_number": "3.0.0",
          "version_type": "release",
          "date_published": "2026-08-01T12:00:00Z",
          "game_versions": ["1.20.1"],
          "loaders": ["forge"],
          "environment": "server_only",
          "dependencies": [{
            "project_id": "DEPENDENCY1",
            "dependency_type": "required"
          }, {
            "project_id": "OPTIONAL1",
            "dependency_type": "optional"
          }],
          "files": [{
            "hashes": { "sha512": "__CONTENT_SHA512__" },
            "url": "https://cdn.modrinth.com/data/CONTENT1/versions/CONTENT_VERSION/example-content.jar",
            "filename": "example-content.jar",
            "primary": true,
            "size": 120000
          }]
        }]
        """.Replace("__CONTENT_SHA512__", new string('d', 128), StringComparison.Ordinal);
    using var contentVersionsDocument = JsonDocument.Parse(contentVersionsJson);
    var contentVersion = ModrinthServerContentCatalogService.ParseVersionsResponse(
        contentVersionsDocument.RootElement).Single();
    AssertEqual("example-content.jar", contentVersion.PrimaryFile!.FileName, "content primary JAR selection");
    AssertEqual(2, contentVersion.Dependencies.Count, "content dependency parsing");
    using var clientOnlyVersionsDocument = JsonDocument.Parse(
        contentVersionsJson.Replace("server_only", "client_only", StringComparison.Ordinal));
    AssertEqual(
        0,
        ModrinthServerContentCatalogService.ParseVersionsResponse(
            clientOnlyVersionsDocument.RootElement).Count,
        "server content search rejects client-only versions");
    AssertEqual(
        1,
        ModrinthServerContentCatalogService.ParsePackVersionsResponse(
            clientOnlyVersionsDocument.RootElement).Count,
        "pack builder retains client-only versions for side placement");
    using var minecraftVersionsDocument = JsonDocument.Parse("""
        [
          { "version": "1.21.8", "version_type": "release" },
          { "version": "26w01a", "version_type": "snapshot" },
          { "version": "1.20.1", "version_type": "release" }
        ]
        """);
    var builderMinecraftVersions = ModrinthServerContentCatalogService.ParseMinecraftVersions(
        minecraftVersionsDocument.RootElement);
    AssertTrue(
        builderMinecraftVersions.SequenceEqual(["1.21.8", "1.20.1"]),
        "builder Minecraft release metadata excludes snapshots");

    var curseForgeSearchRequest = new PackCatalogSearchRequest(
        "Croptopia",
        "1.20.1",
        ServerContentKind.Mod,
        PackBuildTarget.ClientAndServer,
        ["forge"],
        []);
    var curseForgeSearchUri = CurseForgePackContentCatalogProvider.BuildSearchRequestUri(
        curseForgeSearchRequest,
        CurseForgePackContentCatalogProvider.ToModLoaderType("forge"));
    AssertTrue(
        curseForgeSearchUri.Contains("gameId=432", StringComparison.Ordinal)
        && curseForgeSearchUri.Contains("classId=6", StringComparison.Ordinal)
        && curseForgeSearchUri.Contains("gameVersion=1.20.1", StringComparison.Ordinal)
        && curseForgeSearchUri.Contains("modLoaderType=1", StringComparison.Ordinal),
        "CurseForge search targets Minecraft mods, release, and Forge loader");
    using var curseForgeSearchDocument = JsonDocument.Parse("""
        {
          "data": [{
            "id": 415438,
            "name": "Croptopia",
            "slug": "croptopia",
            "summary": "Adds crops and foods.",
            "downloadCount": 25000000,
            "isAvailable": true,
            "authors": [{ "name": "thethonk" }],
            "logo": { "thumbnailUrl": "https://media.forgecdn.net/avatars/croptopia.png" },
            "categories": [{ "name": "Food" }],
            "latestFilesIndexes": [{
              "gameVersion": "1.20.1",
              "fileId": 4800000,
              "filename": "Croptopia-1.20.1-FORGE.jar",
              "releaseType": 1,
              "modLoader": 1
            }]
          }],
          "pagination": {
            "index": 0,
            "pageSize": 20,
            "resultCount": 1,
            "totalCount": 1
          }
        }
        """);
    var curseForgeSearch = CurseForgePackContentCatalogProvider.ParseSearchResponse(
        curseForgeSearchDocument.RootElement,
        ServerContentKind.Mod);
    AssertEqual(1, curseForgeSearch.Items.Count, "CurseForge Croptopia search parsing");
    AssertEqual("curseforge", curseForgeSearch.Items.Single().ProviderId, "CurseForge provider identity");
    AssertTrue(
        curseForgeSearch.Items.Single().MinecraftVersions.Contains("1.20.1"),
        "CurseForge search retains Minecraft 1.20.1 compatibility");

    using var curseForgeFilesDocument = JsonDocument.Parse("""
        {
          "data": [{
            "id": 4800000,
            "modId": 415438,
            "isAvailable": true,
            "displayName": "Croptopia 3.0.4 for Forge 1.20.1",
            "fileName": "Croptopia-1.20.1-FORGE-3.0.4.jar",
            "releaseType": 1,
            "hashes": [{
              "value": "0123456789abcdef0123456789abcdef01234567",
              "algo": 1
            }],
            "fileDate": "2024-01-01T12:00:00Z",
            "fileLength": 4200000,
            "downloadUrl": "https://edge.forgecdn.net/files/4800/000/Croptopia-1.20.1-FORGE-3.0.4.jar",
            "gameVersions": ["1.20.1", "Forge"],
            "dependencies": [{ "modId": 885449, "relationType": 3 }, { "modId": 999999, "relationType": 2 }]
          }],
          "pagination": {
            "index": 0,
            "pageSize": 50,
            "resultCount": 1,
            "totalCount": 1
          }
        }
        """);
    var curseForgeFiles = CurseForgePackContentCatalogProvider.ParseFilesResponse(
        curseForgeFilesDocument.RootElement);
    var curseForgeVersion = curseForgeFiles.Items.Single();
    AssertEqual("415438:4800000", curseForgeVersion.VersionId, "CurseForge composite version identity");
    AssertEqual("forge", curseForgeVersion.Loaders.Single(), "CurseForge file loader parsing");
    AssertEqual(
        "0123456789abcdef0123456789abcdef01234567",
        curseForgeVersion.PrimaryFile!.Sha1,
        "CurseForge SHA-1 metadata parsing");
    AssertTrue(
        curseForgeVersion.Dependencies.Any(dependency =>
            dependency.ProjectId == "885449" && dependency.DependencyType == "required"),
        "CurseForge required dependency relation parsing");
    AssertTrue(
        curseForgeVersion.Dependencies.Any(dependency =>
            dependency.ProjectId == "999999" && dependency.DependencyType == "optional"),
        "CurseForge optional dependency relation parsing");

    var curseForgeApplicationText = CurseForgeApplicationTemplate.CreatePlainText();
    AssertTrue(
        CurseForgeApplicationTemplate.ProjectName.Length <= 30,
        "CurseForge application project name remains form friendly");
    AssertTrue(
        curseForgeApplicationText.Contains("applicant's own local installation", StringComparison.Ordinal)
        && curseForgeApplicationText.Contains("no central API proxy", StringComparison.Ordinal)
        && curseForgeApplicationText.Contains("Windows Credential Manager", StringComparison.Ordinal),
        "CurseForge application template explains the per-installation key boundary");
    AssertTrue(
        curseForgeApplicationText.Contains("No monetization and no business model", StringComparison.Ordinal)
        && curseForgeApplicationText.Contains("does not collect or save", StringComparison.Ordinal)
        && curseForgeApplicationText.Contains("does not accept or submit", StringComparison.Ordinal),
        "CurseForge application template remains accurate and user-controlled");
    AssertTrue(
        curseForgeApplicationText.Contains("asks Overwolf to confirm", StringComparison.Ordinal)
        && curseForgeApplicationText.Contains("audit manifest", StringComparison.Ordinal),
        "CurseForge application template requests approval for retained local metadata");

    var curseForgeKeyStore = new InMemoryCurseForgeApiKeyStore();
    var curseForgeValidationHandler = new StubHttpMessageHandler(request =>
    {
        AssertEqual("v1/games/432", request.RequestUri!.PathAndQuery.TrimStart('/'), "CurseForge key validation endpoint");
        AssertTrue(
            request.Headers.TryGetValues("x-api-key", out var values)
            && values.Single() == "approved-test-key",
            "CurseForge validation sends the candidate key only in the supported header");
        return new HttpResponseMessage(HttpStatusCode.OK);
    });
    var curseForgeKeyService = new CurseForgeApiKeyService(
        curseForgeKeyStore,
        new HttpClient(curseForgeValidationHandler)
        {
            BaseAddress = new Uri("https://api.curseforge.com/")
        });
    var connectedCurseForge = await curseForgeKeyService.ValidateAndStoreAsync(" approved-test-key ");
    AssertTrue(
        connectedCurseForge.HasStoredKey && connectedCurseForge.IsValid,
        "approved CurseForge key is validated before storage");
    AssertEqual("approved-test-key", curseForgeKeyStore.Value, "approved CurseForge key storage normalization");
    AssertEqual("approved-test-key", curseForgeKeyService.GetApiKey(), "stored CurseForge key retrieval");

    var rejectedKeyStore = new InMemoryCurseForgeApiKeyStore { Value = "existing-approved-key" };
    var rejectedKeyService = new CurseForgeApiKeyService(
        rejectedKeyStore,
        new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)))
        {
            BaseAddress = new Uri("https://api.curseforge.com/")
        });
    var rejectedCurseForge = await rejectedKeyService.ValidateAndStoreAsync("rejected-replacement-key");
    AssertTrue(
        rejectedCurseForge.HasStoredKey && !rejectedCurseForge.IsValid,
        "rejected replacement reports the unchanged stored CurseForge connection");
    AssertEqual(
        "existing-approved-key",
        rejectedKeyStore.Value,
        "rejected CurseForge replacement never overwrites an existing key");
    rejectedKeyService.Remove();
    AssertEqual<string?>(null, rejectedKeyStore.Value, "CurseForge disconnect removes the stored key");

    if (OperatingSystem.IsWindows())
    {
        var testCredentialTarget = $"Kiddabob.MinecraftServerManager.Tests/{Guid.NewGuid():N}";
        var windowsKeyStore = new WindowsCredentialManagerApiKeyStore(testCredentialTarget);
        try
        {
            AssertEqual<string?>(null, windowsKeyStore.Read(), "isolated Windows credential starts empty");
            windowsKeyStore.Save("non-secret-round-trip-test-key");
            AssertEqual(
                "non-secret-round-trip-test-key",
                windowsKeyStore.Read(),
                "Windows Credential Manager CurseForge key round trip");
            windowsKeyStore.Remove();
            AssertEqual<string?>(null, windowsKeyStore.Read(), "Windows credential removal");
        }
        finally
        {
            windowsKeyStore.Remove();
        }
    }

    var dependencyVersion = new ServerContentVersion(
        "modrinth",
        "DEPENDENCY1",
        "DEPENDENCY_VERSION",
        "Required Library",
        "1.0.0",
        "release",
        DateTimeOffset.UtcNow,
        ["1.20.1"],
        ["forge"],
        "client_and_server",
        [new ServerContentFile(
            "required-library.jar",
            new Uri("https://cdn.modrinth.com/data/DEPENDENCY1/versions/DEPENDENCY_VERSION/required-library.jar"),
            64000,
            new string('e', 128),
            true)],
        []);
    var contentCatalogStub = new StubServerContentCatalogService(
        new Dictionary<string, IReadOnlyList<ServerContentVersion>>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEPENDENCY1"] = [dependencyVersion]
        },
        new Dictionary<string, ServerContentVersion>(StringComparer.OrdinalIgnoreCase)
        {
            [dependencyVersion.VersionId] = dependencyVersion
        });
    var contentInstaller = new ServerContentInstallService(contentCatalogStub);
    var modTarget = contentInventory.Targets.Single(target => target.Kind == ServerContentKind.Mod);
    var contentPlan = await contentInstaller.CreatePlanAsync(
        profile,
        modTarget,
        contentSearch.Items.Single(),
        contentVersion);
    AssertEqual(2, contentPlan.Items.Count, "required content dependency planning");
    AssertEqual(1, contentPlan.Warnings.Count, "optional content dependency warning");
    AssertTrue(contentPlan.Items[1].IsDependency, "dependency plan item marker");
    AssertThrows<InvalidDataException>(
        () => ServerContentInstallService.ResolveContainedDirectory(testRoot, ".."),
        "content directory traversal rejection");
    await AssertThrowsAsync<InvalidDataException>(
        () => contentInstaller.InstallAsync(new ServerContentInstallPlan(
            testRoot,
            Path.Combine(testRoot, "mods"),
            ServerContentKind.Mod,
            [contentPlan.Items[0] with { Kind = ServerContentKind.Plugin }],
            [])),
        "mixed mod and plugin install plan rejection");

    var conflictingDependencyVersion = dependencyVersion with
    {
        VersionId = "DEPENDENCY_VERSION_2",
        VersionNumber = "2.0.0",
        Files = [new ServerContentFile(
            "required-library-v2.jar",
            new Uri("https://cdn.modrinth.com/data/DEPENDENCY1/versions/DEPENDENCY_VERSION_2/required-library-v2.jar"),
            65000,
            new string('f', 128),
            true)]
    };
    var conflictingRootVersion = contentVersion with
    {
        Dependencies =
        [
            new ServerContentDependency(
                dependencyVersion.VersionId,
                dependencyVersion.ProjectId,
                string.Empty,
                "required"),
            new ServerContentDependency(
                conflictingDependencyVersion.VersionId,
                conflictingDependencyVersion.ProjectId,
                string.Empty,
                "required")
        ]
    };
    var conflictingCatalogStub = new StubServerContentCatalogService(
        new Dictionary<string, IReadOnlyList<ServerContentVersion>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, ServerContentVersion>(StringComparer.OrdinalIgnoreCase)
        {
            [dependencyVersion.VersionId] = dependencyVersion,
            [conflictingDependencyVersion.VersionId] = conflictingDependencyVersion
        });
    await AssertThrowsAsync<InvalidDataException>(
        () => new ServerContentInstallService(conflictingCatalogStub).CreatePlanAsync(
            profile,
            modTarget,
            contentSearch.Items.Single(),
            conflictingRootVersion),
        "conflicting dependency version rejection");

    var modpackVersionsJson = """
        [{
          "id": "VVVVVVVV",
          "project_id": "AABBCCDD",
          "name": "Example Pack 1.0",
          "version_number": "1.0.0",
          "version_type": "release",
          "date_published": "2026-08-01T12:00:00Z",
          "game_versions": ["1.20.1"],
          "loaders": ["forge"],
          "environment": "client_and_server",
          "files": [{
            "hashes": {
              "sha1": "dddddddddddddddddddddddddddddddddddddddd",
              "sha512": "__SHA512__"
            },
            "url": "https://cdn.modrinth.com/data/AABBCCDD/versions/VVVVVVVV/example.mrpack",
            "filename": "example.mrpack",
            "primary": true,
            "size": 500000
          }]
        }]
        """.Replace("__SHA512__", new string('c', 128), StringComparison.Ordinal);
    using var modpackVersionsDocument = JsonDocument.Parse(modpackVersionsJson);
    var modpackVersions = ModrinthModpackCatalogService.ParseVersionsResponse(
        modpackVersionsDocument.RootElement);
    var modpackVersion = modpackVersions.Single();
    AssertTrue(modpackVersion.IsServerCompatible, "Modrinth server environment");
    AssertEqual("forge", modpackVersion.Loaders.Single(), "Modrinth loader metadata");
    AssertEqual("example.mrpack", modpackVersion.PackFile!.FileName, "Modrinth package selection");
    AssertEqual(500000L, modpackVersion.PackFile.Size, "Modrinth package size");

    var unconfirmedServerVersion = modpackVersion with { Environment = string.Empty };
    AssertTrue(
        !unconfirmedServerVersion.IsServerCompatible,
        "Modrinth versions without confirmed server support are rejected");
    AssertTrue(
        (modpackVersion with { Environment = "client_only_server_optional" }).IsServerCompatible,
        "Modrinth optional server environment");

    var packManifestJson = """
        {
          "formatVersion": 1,
          "game": "minecraft",
          "versionId": "example-pack-1.0",
          "name": "Example Pack",
          "dependencies": {
            "minecraft": "1.20.1",
            "forge": "47.3.0"
          },
          "files": [{
            "path": "mods/server-mod.jar",
            "hashes": {
              "sha1": "__SHA1__",
              "sha512": "__SHA512__"
            },
            "env": {
              "client": "required",
              "server": "required"
            },
            "downloads": ["https://cdn.modrinth.com/data/AABBCCDD/versions/VVVVVVVV/server-mod.jar"],
            "fileSize": 125000
          }, {
            "path": "mods/client-only.jar",
            "hashes": {
              "sha1": "__SHA1__",
              "sha512": "__SHA512__"
            },
            "env": {
              "client": "required",
              "server": "unsupported"
            },
            "downloads": ["https://cdn.modrinth.com/data/AABBCCDD/versions/VVVVVVVV/client-only.jar"],
            "fileSize": 25000
          }]
        }
        """
        .Replace("__SHA1__", new string('a', 40), StringComparison.Ordinal)
        .Replace("__SHA512__", new string('b', 128), StringComparison.Ordinal);
    using var packManifestDocument = JsonDocument.Parse(packManifestJson);
    var packManifest = ModrinthModpackImportService.ParseManifest(packManifestDocument.RootElement);
    AssertEqual("1.20.1", packManifest.Dependencies["minecraft"], ".mrpack Minecraft dependency");
    AssertEqual("47.3.0", packManifest.Dependencies["forge"], ".mrpack Forge dependency");
    AssertEqual(2, packManifest.Files.Count, ".mrpack file count");
    AssertEqual("unsupported", packManifest.Files[1].ServerSide, ".mrpack client-only filtering");
    var importedProfile = new ServerProfile
    {
        JavaVersion = "Java 8",
        JavaExecutable = @"C:\managed-java-8\bin\java.exe"
    };
    ModrinthModpackImportService.ApplyManifestMetadata(
        importedProfile,
        modpackSearch.Items.Single(),
        modpackVersion,
        packManifest,
        new KeyValuePair<string, string>("forge", "47.3.0"),
        17,
        @"C:\managed-java-17\bin\java.exe");
    AssertEqual("Java 17", importedProfile.JavaVersion, "modpack profile recommended Java label");
    AssertEqual(
        @"C:\managed-java-17\bin\java.exe",
        importedProfile.JavaExecutable,
        "modpack profile replaces an incompatible detected Java executable");
    AssertEqual(
        "Example Pack 1.0.0",
        ModrinthModpackImportService.CreateServerFolderName("Example Pack", "1.0.0"),
        "modpack server folder naming");
    AssertThrows<InvalidDataException>(
        () => ModrinthModpackImportService.ResolveSafePath(testRoot, "../escape.jar"),
        ".mrpack path traversal rejection");
    var conflictingLoaderJson = packManifestJson.Replace(
        "\"forge\": \"47.3.0\"",
        "\"forge\": \"47.3.0\", \"fabric-loader\": \"0.16.0\"",
        StringComparison.Ordinal);
    using var conflictingLoaderDocument = JsonDocument.Parse(conflictingLoaderJson);
    AssertThrows<InvalidDataException>(
        () => ModrinthModpackImportService.ParseManifest(conflictingLoaderDocument.RootElement),
        ".mrpack conflicting loader rejection");

    var ftbManifestJson = """
        {
          "status": "success",
          "parent": 88,
          "id": 100026,
          "targets": [
            { "name": "minecraft", "version": "1.16.5", "type": "game" },
            { "name": "forge", "version": "36.2.34", "type": "modloader" }
          ],
          "files": [{
            "path": "./mods",
            "name": "example.jar",
            "url": "https://edge.forgecdn.net/files/1/2/example.jar",
            "mirrors": [],
            "size": 123,
            "hashes": { "sha512": "__SHA512__" }
          }, {
            "path": ".",
            "name": "eula.txt",
            "url": "https://files.feed-the-beast.com/blob/eula.txt",
            "mirrors": [],
            "size": 9,
            "hashes": { "sha512": "__SHA512__" }
          }]
        }
        """.Replace("__SHA512__", new string('f', 128), StringComparison.Ordinal);
    using var ftbManifestDocument = JsonDocument.Parse(ftbManifestJson);
    var ftbManifest = FtbModpackImportService.ParseManifest(
        ftbManifestDocument.RootElement,
        ftbPack,
        ftbVersion);
    AssertEqual("1.16.5", ftbManifest.MinecraftVersion, "FTB manifest Minecraft target");
    AssertEqual("forge", ftbManifest.LoaderId, "FTB manifest loader target");
    AssertEqual(1, ftbManifest.Files.Count, "FTB manifest leaves EULA acceptance to the app");
    AssertEqual("mods/example.jar", ftbManifest.Files.Single().RelativePath, "FTB safe file path");

    using var unsafeFtbManifestDocument = JsonDocument.Parse(
        ftbManifestJson.Replace("./mods", "../escape", StringComparison.Ordinal));
    AssertThrows<InvalidDataException>(
        () => FtbModpackImportService.ParseManifest(
            unsafeFtbManifestDocument.RootElement,
            ftbPack,
            ftbVersion),
        "FTB manifest path traversal rejection");

    var duplicateSource = Path.Combine(testRoot, "duplicate-source");
    Directory.CreateDirectory(duplicateSource);
    File.WriteAllText(
        Path.Combine(duplicateSource, "server.properties"),
        "level-name=academy-world\r\n");
    var duplicateWorldRoots = ServerProfileDuplicateService.ReadWorldRoots(duplicateSource);
    AssertTrue(duplicateWorldRoots.Contains("academy-world"), "duplicate custom world detection");
    AssertTrue(
        ServerProfileDuplicateService.ShouldExcludeRelativePath(
            "academy-world/level.dat",
            false,
            duplicateWorldRoots),
        "clean duplicate excludes world data");
    AssertTrue(
        ServerProfileDuplicateService.ShouldExcludeRelativePath(
            "eula.txt",
            false,
            duplicateWorldRoots),
        "duplicate always resets EULA acceptance");
    AssertTrue(
        ServerProfileDuplicateService.ShouldExcludeRelativePath(
            "logs/latest.log",
            true,
            duplicateWorldRoots),
        "duplicate excludes transient logs");
    AssertTrue(
        !ServerProfileDuplicateService.ShouldExcludeRelativePath(
            "mods/example.jar",
            false,
            duplicateWorldRoots),
        "duplicate retains editable server content");

    var technicArchivePath = Path.Combine(testRoot, "technic-server.zip");
    using (var technicArchive = System.IO.Compression.ZipFile.Open(
        technicArchivePath,
        System.IO.Compression.ZipArchiveMode.Create))
    {
        var serverJar = technicArchive.CreateEntry("Tekkit Server/server.jar");
        await using (var serverJarStream = serverJar.Open())
        {
            await serverJarStream.WriteAsync(new byte[] { 0x01, 0x02, 0x03 });
        }

        var eula = technicArchive.CreateEntry("Tekkit Server/eula.txt");
        await using var eulaWriter = new StreamWriter(eula.Open());
        await eulaWriter.WriteAsync("eula=true");
    }

    var technicExtractDirectory = Path.Combine(testRoot, "technic-extracted");
    Directory.CreateDirectory(technicExtractDirectory);
    var technicExtractedFiles = await TechnicModpackImportService.ExtractArchiveAsync(
        technicArchivePath,
        technicExtractDirectory,
        progress: null,
        CancellationToken.None);
    AssertEqual(1, technicExtractedFiles, "Technic archive leaves EULA acceptance to the app");
    AssertTrue(
        File.Exists(Path.Combine(technicExtractDirectory, "server.jar")),
        "Technic single root folder flattening");
    AssertTrue(
        !File.Exists(Path.Combine(technicExtractDirectory, "eula.txt")),
        "Technic archived EULA rejection");

    var unsafeTechnicArchivePath = Path.Combine(testRoot, "unsafe-technic-server.zip");
    using (var unsafeTechnicArchive = System.IO.Compression.ZipFile.Open(
        unsafeTechnicArchivePath,
        System.IO.Compression.ZipArchiveMode.Create))
    {
        unsafeTechnicArchive.CreateEntry("../escape.jar");
    }

    var unsafeTechnicExtractDirectory = Path.Combine(testRoot, "unsafe-technic-extracted");
    Directory.CreateDirectory(unsafeTechnicExtractDirectory);
    await AssertThrowsAsync<InvalidDataException>(
        () => TechnicModpackImportService.ExtractArchiveAsync(
            unsafeTechnicArchivePath,
            unsafeTechnicExtractDirectory,
            progress: null,
            CancellationToken.None),
        "Technic archive path traversal rejection");

    var overridePackagePath = Path.Combine(testRoot, "override-order.mrpack");
    using (var overrideArchive = System.IO.Compression.ZipFile.Open(
        overridePackagePath,
        System.IO.Compression.ZipArchiveMode.Create))
    {
        var normalOverride = overrideArchive.CreateEntry("overrides/config/example.cfg");
        await using (var writer = new StreamWriter(normalOverride.Open()))
        {
            await writer.WriteAsync("normal");
        }

        var serverOverride = overrideArchive.CreateEntry("server-overrides/config/example.cfg");
        await using (var writer = new StreamWriter(serverOverride.Open()))
        {
            await writer.WriteAsync("server");
        }
    }

    var overrideDestination = Path.Combine(testRoot, "override-destination");
    Directory.CreateDirectory(overrideDestination);
    using (var overrideArchive = System.IO.Compression.ZipFile.OpenRead(overridePackagePath))
    {
        await ModrinthModpackImportService.ExtractLayerAsync(
            overrideArchive,
            "overrides/",
            overrideDestination,
            null,
            CancellationToken.None);
        await ModrinthModpackImportService.ExtractLayerAsync(
            overrideArchive,
            "server-overrides/",
            overrideDestination,
            null,
            CancellationToken.None);
    }

    AssertEqual(
        "server",
        await File.ReadAllTextAsync(Path.Combine(overrideDestination, "config", "example.cfg")),
        ".mrpack server override layer order");

    using var fabricInstallerDocument = JsonDocument.Parse("""
        [
          { "version": "1.0.4", "stable": false },
          { "version": "1.0.3", "stable": true }
        ]
        """);
    AssertEqual(
        "1.0.3",
        FabricServerBaselineInstaller.ParseStableInstallerVersion(fabricInstallerDocument.RootElement),
        "stable Fabric installer selection");
    AssertTrue(
        new FabricServerBaselineInstaller().CanInstall("fabric-loader"),
        "Fabric baseline routing");
    var fabricLauncherPath = Path.Combine(testRoot, "fabric-server-launch.jar");
    CreateJar(
        fabricLauncherPath,
        "net.fabricmc.installer.ServerLauncher",
        ["net/fabricmc/installer/ServerLauncher.class"]);
    FabricServerBaselineInstaller.ValidateLauncherJar(fabricLauncherPath);

    using var mojangManifestDocument = JsonDocument.Parse("""
        {
          "versions": [{
            "id": "1.20.1",
            "url": "https://piston-meta.mojang.com/v1/packages/metadata/1.20.1.json",
            "sha1": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
          }]
        }
        """);
    var mojangMetadataReference = VanillaServerBaselineInstaller.ParseVersionMetadataReference(
        mojangManifestDocument.RootElement,
        "1.20.1");
    AssertEqual(
        "piston-meta.mojang.com",
        mojangMetadataReference.Uri.Host,
        "Mojang metadata host validation");
    using var mojangVersionDocument = JsonDocument.Parse("""
        {
          "downloads": {
            "server": {
              "url": "https://piston-data.mojang.com/v1/objects/server/server.jar",
              "sha1": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "size": 50000000
            }
          }
        }
        """);
    var mojangServerDownload = VanillaServerBaselineInstaller.ParseServerDownload(
        mojangVersionDocument.RootElement);
    AssertEqual(50000000L, mojangServerDownload.Size, "Mojang server JAR size metadata");
    AssertTrue(
        new VanillaServerBaselineInstaller().CanInstall("minecraft"),
        "Vanilla baseline routing");
    var vanillaServerPath = Path.Combine(testRoot, "minecraft_server.1.20.1.jar");
    CreateJar(
        vanillaServerPath,
        "net.minecraft.server.Main",
        ["net/minecraft/server/Main.class"]);
    VanillaServerBaselineInstaller.ValidateServerJar(vanillaServerPath);

    var mavenVersions = JavaServerInstallerUtilities.ParseMavenVersions("""
        <?xml version="1.0" encoding="UTF-8"?>
        <metadata>
          <versioning>
            <versions>
              <version>1.7.10-10.13.4.1614-1.7.10</version>
              <version>1.20.1-47.3.0</version>
              <version>47.3.0</version>
              <version>21.4.111-beta</version>
            </versions>
          </versioning>
        </metadata>
        """);
    AssertEqual(
        "1.20.1-47.3.0",
        JavaServerInstallerUtilities.ResolveForgeArtifactVersion(
            mavenVersions,
            "1.20.1",
            "47.3.0"),
        "modern Forge Maven coordinate");
    AssertEqual(
        "1.20.1-47.3.0",
        JavaServerInstallerUtilities.ResolveForgeArtifactVersion(
            mavenVersions,
            "1.20.1",
            "1.20.1-47.3.0"),
        "fully qualified Forge Maven coordinate");
    AssertEqual(
        "1.7.10-10.13.4.1614-1.7.10",
        JavaServerInstallerUtilities.ResolveForgeArtifactVersion(
            mavenVersions,
            "1.7.10",
            "10.13.4.1614"),
        "legacy Forge Maven coordinate");
    AssertEqual(
        "21.4.111-beta",
        JavaServerInstallerUtilities.ResolveExactArtifactVersion(
            mavenVersions,
            "NeoForge",
            "21.4.111-beta"),
        "NeoForge Maven coordinate");
    var installerSourcePath = Path.Combine(testRoot, "example-installer-source.jar");
    CreateJar(
        installerSourcePath,
        "example.Installer",
        ["example/Installer.class"]);
    var installerBytes = await File.ReadAllBytesAsync(installerSourcePath);
    var installerHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(installerBytes));
    var verifiedInstallerPath = Path.Combine(testRoot, "example-installer-verified.jar");
    await using (var installerStream = new MemoryStream(installerBytes, writable: false))
    {
        await JavaServerInstallerUtilities.WriteVerifiedInstallerAsync(
            installerStream,
            verifiedInstallerPath,
            installerHash,
            CancellationToken.None);
    }

    using (var exclusiveInstaller = new FileStream(
        verifiedInstallerPath,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None))
    {
        AssertTrue(
            exclusiveInstaller.Length > 0,
            "verified installer stream released before JAR validation");
    }

    AssertTrue(new ForgeServerBaselineInstaller(runtimeService).CanInstall("forge"), "Forge baseline routing");
    AssertTrue(
        new NeoForgeServerBaselineInstaller(runtimeService).CanInstall("neoforge"),
        "NeoForge baseline routing");
    using var quiltInstallerDocument = JsonDocument.Parse("""
        [{
          "version": "0.15.1",
          "url": "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/0.15.1/quilt-installer-0.15.1.jar",
          "hashes": {
            "sha256": "2bd88a1429eaeb3ce3f5e9c49c591c551012937b35bf332ca277b4d93d70408d"
          }
        }]
        """);
    var quiltInstaller = QuiltServerBaselineInstaller.ParseInstallerArtifact(
        quiltInstallerDocument.RootElement);
    AssertEqual("0.15.1", quiltInstaller.Version, "Quilt installer metadata version");
    AssertEqual("maven.quiltmc.org", quiltInstaller.DownloadUri.Host, "Quilt installer host validation");
    using var untrustedQuiltInstallerDocument = JsonDocument.Parse("""
        [{
          "version": "0.15.1",
          "url": "https://example.com/quilt-installer-0.15.1.jar",
          "hashes": {
            "sha256": "2bd88a1429eaeb3ce3f5e9c49c591c551012937b35bf332ca277b4d93d70408d"
          }
        }]
        """);
    AssertThrows<InvalidDataException>(
        () => QuiltServerBaselineInstaller.ParseInstallerArtifact(
            untrustedQuiltInstallerDocument.RootElement),
        "Quilt installer host rejection");
    using var quiltLoaderDocument = JsonDocument.Parse("""
        [{ "loader": { "version": "0.20.0-beta.9" } }]
        """);
    QuiltServerBaselineInstaller.ValidateLoaderVersion(
        quiltLoaderDocument.RootElement,
        "0.20.0-beta.9");
    AssertThrows<InvalidDataException>(
        () => QuiltServerBaselineInstaller.ValidateLoaderVersion(
            quiltLoaderDocument.RootElement,
            "0.19.0"),
        "Quilt exact loader validation");
    AssertTrue(
        new QuiltServerBaselineInstaller(runtimeService).CanInstall("quilt-loader"),
        "Quilt baseline routing");

    var quiltMergeSource = Path.Combine(testRoot, "quilt-merge-source");
    var quiltMergeDestination = Path.Combine(testRoot, "quilt-merge-destination");
    Directory.CreateDirectory(Path.Combine(quiltMergeSource, "libraries"));
    Directory.CreateDirectory(Path.Combine(quiltMergeDestination, "libraries"));
    File.WriteAllText(Path.Combine(quiltMergeSource, "libraries", "same.jar"), "same");
    File.WriteAllText(Path.Combine(quiltMergeDestination, "libraries", "same.jar"), "same");
    File.WriteAllText(Path.Combine(quiltMergeSource, "quilt-server-launch.jar"), "launcher");
    QuiltServerBaselineInstaller.MergeInstalledServer(quiltMergeSource, quiltMergeDestination);
    AssertTrue(
        File.Exists(Path.Combine(quiltMergeDestination, "quilt-server-launch.jar")),
        "Quilt staged output merge");

    var quiltConflictSource = Path.Combine(testRoot, "quilt-conflict-source");
    var quiltConflictDestination = Path.Combine(testRoot, "quilt-conflict-destination");
    Directory.CreateDirectory(quiltConflictSource);
    Directory.CreateDirectory(quiltConflictDestination);
    File.WriteAllText(Path.Combine(quiltConflictSource, "server.jar"), "generated");
    File.WriteAllText(Path.Combine(quiltConflictDestination, "server.jar"), "pack");
    AssertThrows<InvalidDataException>(
        () => QuiltServerBaselineInstaller.MergeInstalledServer(
            quiltConflictSource,
            quiltConflictDestination),
        "Quilt conflicting pack file protection");

    var modernProfile = new ServerProfile
    {
        Id = "modern-forge",
        ServerDirectory = modernForgeFolder,
        JavaExecutable = "java",
        JavaVersion = "Java 17",
        JavaArguments = ["-Xms2G", "-Xmx4G"],
        DirectLaunchArguments = modernForgeDetection.EffectiveDirectLaunchArguments,
        ServerArguments = ["nogui"]
    };
    var modernRequest = new JavaServerLaunchRequestFactory(runtimeService).Create(modernProfile);
    AssertTrue(
        modernRequest.Arguments.SequenceEqual(
        [
            "-Xms2G",
            "-Xmx4G",
            "@libraries/net/minecraftforge/forge/1.20.1-47.3.0/win_args.txt",
            "nogui"
        ]),
        "modern Forge memory override argument placement");

    var failureParser = new ProfileConsoleParserFactory().Create(new ServerProfile
    {
        FailurePatterns = ["LoaderException"]
    });
    var failureResult = failureParser.Parse(
        "2015-01-01 12:00:00 [SEVERE] [ForgeModLoader] cpw.mods.fml.common.LoaderException: broken mod",
        ServerOutputStream.StandardError);
    AssertEqual(ServerConsoleSignal.Failed, failureResult.Signal, "startup failure signal");
    AssertEqual(ServerLogLevel.Error, failureResult.Entry.Level, "startup failure log severity");

    var packRootProject = new ServerContentProject(
        "stub",
        "root-project",
        "root-project",
        "Root Project",
        "Builder resolver root",
        "Test author",
        string.Empty,
        100,
        ServerContentKind.Mod,
        ["1.20.1"],
        ["technology"],
        ["client_and_server"]);
    var packRootVersion = MakePackVersion(
        "root-project",
        "root-version",
        "Root Project 1.0",
        "1.0.0",
        "1.20.1",
        "fabric",
        "client_and_server",
        [
            new ServerContentDependency(string.Empty, "library-project", string.Empty, "required"),
            new ServerContentDependency(string.Empty, "optional-project", string.Empty, "optional"),
            new ServerContentDependency(string.Empty, "incompatible-project", string.Empty, "incompatible")
        ]);
    var incompatibleNewestLibrary = MakePackVersion(
        "library-project",
        "library-newest",
        "Library 2.0",
        "2.0.0",
        "1.21.1",
        "fabric",
        "client_only");
    var compatibleLibrary = MakePackVersion(
        "library-project",
        "library-compatible",
        "Library 1.0",
        "1.0.0",
        "1.20.1",
        "fabric",
        "client_only",
        [new ServerContentDependency(string.Empty, "root-project", string.Empty, "required")]);
    var packProvider = new StubPackContentCatalogProvider(
        "stub",
        [packRootProject],
        new Dictionary<string, IReadOnlyList<ServerContentVersion>>(StringComparer.OrdinalIgnoreCase)
        {
            ["root-project"] = [packRootVersion],
            ["library-project"] = [incompatibleNewestLibrary, compatibleLibrary]
        },
        new Dictionary<string, ServerContentVersion>(StringComparer.OrdinalIgnoreCase)
        {
            [packRootVersion.VersionId] = packRootVersion,
            [incompatibleNewestLibrary.VersionId] = incompatibleNewestLibrary,
            [compatibleLibrary.VersionId] = compatibleLibrary
        });
    var packCatalog = new PackContentCatalogService(
        [packProvider, new FailingPackContentCatalogProvider()]);
    var aggregatedSearch = await packCatalog.SearchAsync(new PackCatalogSearchRequest(
        "root",
        "1.20.1",
        ServerContentKind.Mod,
        PackBuildTarget.ClientAndServer,
        ["fabric"],
        ["technology"]));
    AssertEqual(1, aggregatedSearch.Items.Count, "pack catalogue successful provider result");
    AssertEqual(2, aggregatedSearch.Providers.Count, "pack catalogue provider status count");
    AssertEqual(1, aggregatedSearch.AvailableProviderCount, "pack catalogue partial provider availability");
    AssertTrue(
        aggregatedSearch.Providers.Any(provider => !provider.IsAvailable),
        "pack catalogue isolates a provider failure");
    var unconfiguredCurseForge = new CurseForgePackContentCatalogProvider(
        new HttpClient { BaseAddress = new Uri("https://api.curseforge.com/") },
        string.Empty);
    var providerConfigurationSearch = await new PackContentCatalogService(
        [packProvider, unconfiguredCurseForge]).SearchAsync(new PackCatalogSearchRequest(
            "root",
            "1.20.1",
            ServerContentKind.Mod,
            PackBuildTarget.ClientAndServer,
            ["fabric"],
            []));
    AssertTrue(
        providerConfigurationSearch.Providers.Any(provider =>
            provider.ProviderId == "curseforge"
            && !provider.IsAvailable
            && provider.Message.Contains("API key", StringComparison.OrdinalIgnoreCase)),
        "pack catalogue explains an unconfigured CurseForge provider without hiding Modrinth results");

    var resolver = new PackDependencyResolver(packCatalog);
    var readyPlan = await resolver.ResolveAsync(new PackResolveRequest(
        PackBuildTarget.ClientAndServer,
        "1.20.1",
        ["fabric"],
        ["fabric"],
        packRootProject,
        packRootVersion,
        []));
    AssertTrue(readyPlan.IsReady, "pack dependency plan ready");
    AssertEqual(2, readyPlan.Items.Count, "pack required dependency count");
    AssertTrue(
        readyPlan.Items.Any(item =>
            item.VersionId == "library-compatible"
            && item.Placement == PackContentPlacement.Client
            && item.IsDependency),
        "pack resolver selects compatible older dependency and keeps it client-only");
    AssertTrue(
        readyPlan.Items.Any(item =>
            item.VersionId == "root-version"
            && item.Placement == PackContentPlacement.Both),
        "pack resolver places shared content on both sides");
    AssertTrue(
        readyPlan.Warnings.Any(warning => warning.Contains("optional", StringComparison.OrdinalIgnoreCase)),
        "pack resolver surfaces optional dependency without auto-adding it");

    var rootOutputBytes = Encoding.UTF8.GetBytes("verified root content");
    var libraryOutputBytes = Encoding.UTF8.GetBytes("verified client library");
    var rootOutputFile = new ServerContentFile(
        "root-project.jar",
        new Uri("https://downloads.example.test/root-project.jar"),
        rootOutputBytes.Length,
        Convert.ToHexString(SHA512.HashData(rootOutputBytes)).ToLowerInvariant(),
        true);
    var libraryOutputFile = new ServerContentFile(
        "library-project.jar",
        new Uri("https://downloads.example.test/library-project.jar"),
        libraryOutputBytes.Length,
        Convert.ToHexString(SHA512.HashData(libraryOutputBytes)).ToLowerInvariant(),
        true);
    var outputRootVersion = packRootVersion with { Files = [rootOutputFile] };
    var outputLibraryVersion = compatibleLibrary with { Files = [libraryOutputFile] };
    var outputProvider = new StubPackContentCatalogProvider(
        "stub",
        [packRootProject],
        new Dictionary<string, IReadOnlyList<ServerContentVersion>>(StringComparer.OrdinalIgnoreCase)
        {
            ["root-project"] = [outputRootVersion],
            ["library-project"] = [outputLibraryVersion]
        },
        new Dictionary<string, ServerContentVersion>(StringComparer.OrdinalIgnoreCase)
        {
            [outputRootVersion.VersionId] = outputRootVersion,
            [outputLibraryVersion.VersionId] = outputLibraryVersion
        });
    var outputCatalog = new PackContentCatalogService([outputProvider]);
    var outputDownloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
    {
        [rootOutputFile.DownloadUri.AbsoluteUri] = rootOutputBytes,
        [libraryOutputFile.DownloadUri.AbsoluteUri] = libraryOutputBytes
    };
    var outputService = new PackDraftOutputService(
        outputCatalog,
        [new StubPackContentDownloadProvider("stub", outputDownloads)],
        [],
        runtimeService);
    var outputParent = Path.Combine(testRoot, "builder-output");
    Directory.CreateDirectory(outputParent);
    var outputRequest = new PackOutputRequest(
        "Example Built Pack",
        PackBuildTarget.ClientAndServer,
        "1.20.1",
        "fabric-client",
        "fabric-server",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        outputParent,
        readyPlan.Items);
    var outputPlan = await outputService.CreatePlanAsync(outputRequest);
    AssertEqual(2, outputPlan.Items.Count, "builder output plan resolves every draft version");
    AssertEqual(3, outputPlan.Items.Sum(item => item.RelativePaths.Count), "builder output side placement count");
    var outputResult = await outputService.CreateOutputAsync(outputPlan);
    AssertEqual(2, outputResult.DownloadedFileCount, "builder output unique verified download count");
    AssertEqual(3, outputResult.ArrangedFileCount, "builder output arranged file copy count");
    AssertTrue(
        File.Exists(Path.Combine(outputResult.OutputDirectory, "Client", "mods", rootOutputFile.FileName))
        && File.Exists(Path.Combine(outputResult.OutputDirectory, "Server", "mods", rootOutputFile.FileName)),
        "shared builder content is arranged on client and server");
    AssertTrue(
        File.Exists(Path.Combine(outputResult.OutputDirectory, "Client", "mods", libraryOutputFile.FileName))
        && !File.Exists(Path.Combine(outputResult.OutputDirectory, "Server", "mods", libraryOutputFile.FileName)),
        "client-only builder dependency stays out of server output");
    AssertTrue(
        !Directory.Exists(Path.Combine(outputResult.OutputDirectory, ".downloads")),
        "builder output removes its verified download cache");
    using (var outputManifest = JsonDocument.Parse(File.ReadAllText(outputResult.ManifestPath)))
    {
        AssertTrue(
            outputManifest.RootElement.GetProperty("contentOnly").GetBoolean(),
            "builder manifest declares content-only output");
        AssertEqual(2, outputManifest.RootElement.GetProperty("items").GetArrayLength(), "builder manifest item count");
    }

    var serverOutputParent = Path.Combine(testRoot, "builder-runnable-server");
    Directory.CreateDirectory(serverOutputParent);
    var serverOutputService = new PackDraftOutputService(
        outputCatalog,
        [new StubPackContentDownloadProvider("stub", outputDownloads)],
        [new StubServerBaselineInstaller()],
        runtimeService);
    var serverOutputPlan = await serverOutputService.CreatePlanAsync(new PackOutputRequest(
        "Runnable Built Pack",
        PackBuildTarget.Server,
        "1.20.1",
        string.Empty,
        "fabric-server",
        string.Empty,
        string.Empty,
        "stub-loader",
        "1.2.3",
        serverOutputParent,
        []));
    AssertTrue(serverOutputPlan.PreparesServerBaseline, "builder plans exact runnable server baseline");
    AssertEqual(0, serverOutputPlan.Items.Count, "builder permits an empty supported server baseline");
    AssertEqual(17, serverOutputPlan.RecommendedJavaMajor, "builder records recommended server Java");
    var serverOutputResult = await serverOutputService.CreateOutputAsync(serverOutputPlan);
    AssertTrue(serverOutputResult.ServerBaselinePrepared, "builder prepares runnable server baseline");
    AssertTrue(
        File.Exists(Path.Combine(serverOutputResult.ServerDirectory, serverOutputResult.ServerLauncherFileName)),
        "builder result exposes the installed server launcher");
    AssertTrue(
        !File.Exists(Path.Combine(serverOutputResult.ServerDirectory, "eula.txt")),
        "builder does not accept or create the Minecraft EULA");
    using (var serverOutputManifest = JsonDocument.Parse(File.ReadAllText(serverOutputResult.ManifestPath)))
    {
        AssertTrue(
            serverOutputManifest.RootElement.GetProperty("serverBaselinePrepared").GetBoolean(),
            "builder manifest records runnable server baseline");
        AssertTrue(
            !serverOutputManifest.RootElement.GetProperty("serverEulaAccepted").GetBoolean(),
            "builder manifest keeps EULA pending");
        AssertEqual(
            "1.2.3",
            serverOutputManifest.RootElement.GetProperty("serverLoaderVersion").GetString(),
            "builder manifest records exact server loader");
    }

    var rejectedEulaParent = Path.Combine(testRoot, "builder-rejected-eula");
    Directory.CreateDirectory(rejectedEulaParent);
    var rejectedEulaService = new PackDraftOutputService(
        outputCatalog,
        [new StubPackContentDownloadProvider("stub", outputDownloads)],
        [new StubServerBaselineInstaller(writesAcceptedEula: true)],
        runtimeService);
    var rejectedEulaPlan = await rejectedEulaService.CreatePlanAsync(new PackOutputRequest(
        "Rejected Eula Pack",
        PackBuildTarget.Server,
        "1.20.1",
        string.Empty,
        "fabric-server",
        string.Empty,
        string.Empty,
        "stub-loader",
        "1.2.3",
        rejectedEulaParent,
        []));
    await AssertThrowsAsync<InvalidDataException>(
        () => rejectedEulaService.CreateOutputAsync(rejectedEulaPlan),
        "builder rejects a baseline installer that silently accepts the EULA");
    AssertTrue(
        !Directory.Exists(rejectedEulaPlan.DestinationDirectory)
        && Directory.GetDirectories(rejectedEulaParent, ".msm-pack-*", SearchOption.TopDirectoryOnly).Length == 0,
        "silent EULA acceptance rolls back the final and staging folders");

    await AssertThrowsAsync<IOException>(
        () => outputService.CreatePlanAsync(outputRequest),
        "builder output never overwrites an existing pack folder");
    AssertThrows<ArgumentException>(
        () => PackDraftOutputService.NormalizePackName(".."),
        "builder output rejects traversal-like pack names");

    var failedOutputParent = Path.Combine(testRoot, "builder-output-failure");
    Directory.CreateDirectory(failedOutputParent);
    var failingOutputService = new PackDraftOutputService(
        outputCatalog,
        [new StubPackContentDownloadProvider("stub", outputDownloads, libraryOutputFile.FileName)],
        [],
        runtimeService);
    var failedOutputPlan = await failingOutputService.CreatePlanAsync(outputRequest with
    {
        PackName = "Failed Built Pack",
        DestinationParentDirectory = failedOutputParent
    });
    await AssertThrowsAsync<IOException>(
        () => failingOutputService.CreateOutputAsync(failedOutputPlan),
        "builder output reports a provider download failure");
    AssertTrue(
        !Directory.Exists(failedOutputPlan.DestinationDirectory)
        && Directory.GetDirectories(failedOutputParent, ".msm-pack-*", SearchOption.TopDirectoryOnly).Length == 0,
        "builder output rolls back its destination and staging folder after failure");

    var modrinthDownloadBytes = Encoding.UTF8.GetBytes("modrinth verified payload");
    var modrinthDownloadFile = new ServerContentFile(
        "modrinth-test.jar",
        new Uri("https://cdn.modrinth.com/data/test/modrinth-test.jar"),
        modrinthDownloadBytes.Length,
        Convert.ToHexString(SHA512.HashData(modrinthDownloadBytes)).ToLowerInvariant(),
        true);
    var modrinthDownloadProvider = new ModrinthPackContentDownloadProvider(
        new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(modrinthDownloadBytes)
            })));
    var modrinthDownloadPath = Path.Combine(testRoot, "modrinth-download-test.jar");
    await modrinthDownloadProvider.DownloadAndVerifyAsync(modrinthDownloadFile, modrinthDownloadPath);
    AssertTrue(
        File.ReadAllBytes(modrinthDownloadPath).SequenceEqual(modrinthDownloadBytes),
        "Modrinth builder download verifies SHA-512 content");

    var curseForgeDownloadBytes = Encoding.UTF8.GetBytes("curseforge verified payload");
    var curseForgeDownloadFile = new ServerContentFile(
        "curseforge-test.jar",
        new Uri("https://edge.forgecdn.net/files/1234/567/curseforge-test.jar"),
        curseForgeDownloadBytes.Length,
        string.Empty,
        true,
        Convert.ToHexString(SHA1.HashData(curseForgeDownloadBytes)).ToLowerInvariant());
    var curseForgeDownloadProvider = new CurseForgePackContentDownloadProvider(
        new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(curseForgeDownloadBytes)
            })));
    var curseForgeDownloadPath = Path.Combine(testRoot, "curseforge-download-test.jar");
    await curseForgeDownloadProvider.DownloadAndVerifyAsync(curseForgeDownloadFile, curseForgeDownloadPath);
    AssertTrue(
        File.ReadAllBytes(curseForgeDownloadPath).SequenceEqual(curseForgeDownloadBytes),
        "CurseForge builder download verifies SHA-1 content from a trusted host");
    AssertThrows<InvalidDataException>(
        () => curseForgeDownloadProvider.ValidateFile(curseForgeDownloadFile with
        {
            DownloadUri = new Uri("https://example.com/curseforge-test.jar")
        }),
        "CurseForge builder download rejects untrusted hosts");

    var conflictPlan = await resolver.ResolveAsync(new PackResolveRequest(
        PackBuildTarget.ClientAndServer,
        "1.20.1",
        ["fabric"],
        ["fabric"],
        packRootProject,
        packRootVersion,
        [
            new PackDraftItem(
                "stub",
                "incompatible-project",
                "incompatible-version",
                "Incompatible Project",
                "1.0.0",
                ServerContentKind.Mod,
                PackContentPlacement.Both,
                false,
                "Test")
        ]));
    AssertTrue(!conflictPlan.IsReady, "pack incompatible dependency blocks draft addition");
    AssertTrue(
        conflictPlan.Conflicts.Any(conflict => conflict.Contains("incompatible", StringComparison.OrdinalIgnoreCase)),
        "pack incompatible dependency explanation");

    var platformCatalog = new PackPlatformCatalogService();
    AssertTrue(
        platformCatalog.GetClientPlatforms().Any(platform => platform.Id == "fabric-client" && platform.SupportsMods),
        "builder client loader guidance");
    AssertTrue(
        platformCatalog.GetServerPlatforms().Any(platform =>
            platform.Id == "paper-server" && platform.SupportsPlugins && !platform.SupportsMods),
        "builder plugin platform guidance");
    AssertTrue(
        platformCatalog.GetServerPlatforms().Any(platform =>
            platform.Kind == PackPlatformKind.HybridPlatform
            && platform.SupportsMods
            && platform.SupportsPlugins
            && platform.IsExperimental),
        "builder hybrid platform is clearly experimental");
    AssertTrue(
        platformCatalog.GetClientPlatforms("1.6.4").Select(platform => platform.Id)
            .SequenceEqual(["vanilla-client", "forge-client"]),
        "legacy Minecraft client loaders are version filtered");
    AssertTrue(
        platformCatalog.GetServerPlatforms("1.6.4").Select(platform => platform.Id)
            .SequenceEqual(["vanilla-server", "forge-server"]),
        "legacy Minecraft server platforms are version filtered");
    AssertTrue(
        platformCatalog.GetClientPlatforms("1.14").Select(platform => platform.Id)
            .SequenceEqual(["vanilla-client", "fabric-client"]),
        "Fabric starts at its first supported release without exposing Quilt");
    AssertTrue(
        platformCatalog.GetClientPlatforms("1.14.4").Select(platform => platform.Id)
            .SequenceEqual(["vanilla-client", "fabric-client", "quilt-client", "forge-client"]),
        "Quilt and Forge appear only on a compatible 1.14 release");
    AssertTrue(
        !platformCatalog.GetClientPlatforms("1.20.1").Any(platform => platform.Id == "neoforge-client"),
        "NeoForge is hidden before Minecraft 1.20.2");
    AssertTrue(
        platformCatalog.GetClientPlatforms("1.20.2").Any(platform => platform.Id == "neoforge-client"),
        "NeoForge appears from Minecraft 1.20.2");
    AssertTrue(
        !platformCatalog.GetClientPlatforms("1.20.5").Any(platform => platform.Id == "forge-client"),
        "Forge is hidden for an unsupported release gap");
    AssertEqual(
        5,
        platformCatalog.GetClientPlatforms("26.2").Count,
        "current Minecraft release exposes every compatible client option");
    AssertEqual(21, runtimeService.GetRecommendedJavaMajor("26.2"), "calendar-version Minecraft Java baseline");

    var fabricLoaderVersions = PackPlatformVersionService.ParseLoaderJson(
        """
        [
          { "loader": { "version": "0.17.0-beta.1", "stable": false } },
          { "loader": { "version": "0.16.10", "stable": true } }
        ]
        """,
        "fabric-server",
        "fabric-loader",
        true,
        true,
        "Fabric");
    AssertEqual("0.16.10", fabricLoaderVersions[0].Version, "Fabric stable loader is preferred");
    AssertTrue(fabricLoaderVersions[0].CanPrepareServer, "Fabric server loader is runnable");

    var quiltLoaderVersions = PackPlatformVersionService.ParseLoaderJson(
        """
        [
          { "loader": { "version": "0.29.0-beta.3" } },
          { "loader": { "version": "0.28.1" } }
        ]
        """,
        "quilt-client",
        "quilt-loader",
        false,
        false,
        "Quilt");
    AssertEqual("0.28.1", quiltLoaderVersions[0].Version, "Quilt release loader is preferred");
    AssertTrue(!quiltLoaderVersions[0].CanPrepareServer, "client loader is recorded without server preparation");

    var forgeLoaderVersions = PackPlatformVersionService.ParseForgeVersions(
        """
        <metadata><versioning><versions>
          <version>1.20.1-47.1.0</version>
          <version>1.20.1-47.2.0-beta</version>
          <version>1.20.1-47.3.5</version>
          <version>1.20.2-48.0.1</version>
        </versions></versioning></metadata>
        """,
        "forge-server",
        "1.20.1",
        true);
    AssertEqual("47.3.5", forgeLoaderVersions[0].Version, "Forge filters and prefers newest stable loader");
    AssertTrue(
        forgeLoaderVersions.All(option => !option.Version.StartsWith("48.", StringComparison.Ordinal)),
        "Forge excludes builds for another Minecraft release");

    var neoForgeLoaderVersions = PackPlatformVersionService.ParseNeoForgeVersions(
        """
        <metadata><versioning><versions>
          <version>20.2.85</version>
          <version>20.2.86-beta</version>
          <version>20.4.10</version>
        </versions></versioning></metadata>
        """,
        "neoforge-server",
        "1.20.2",
        true);
    AssertEqual("20.2.85", neoForgeLoaderVersions[0].Version, "NeoForge maps official Minecraft version scheme");
    AssertEqual("20.2.", PackPlatformVersionService.GetNeoForgeVersionPrefix("1.20.2"), "NeoForge 1.x prefix");
    AssertEqual("26.1.0.", PackPlatformVersionService.GetNeoForgeVersionPrefix("26.1"), "NeoForge modern prefix");
    AssertEqual("26.1.2.", PackPlatformVersionService.GetNeoForgeVersionPrefix("26.1.2"), "NeoForge patched modern prefix");

    var emptyPackCatalog = new PackContentCatalogService([]);
    var builderViewModel = new PackBuilderViewModel(
        emptyPackCatalog,
        new PackDependencyResolver(emptyPackCatalog),
        platformCatalog,
        new StubPackPlatformVersionService(),
        runtimeService,
        new StubCurseForgeApiKeyService(),
        new StubPackDraftOutputService(),
        new ModpackInstallLocationService(Path.Combine(testRoot, "builder-local-app-data")));
    builderViewModel.SelectedMinecraftVersion = "1.6.4";
    AssertEqual("forge-client", builderViewModel.SelectedClientPlatform!.Id, "legacy client fallback");
    AssertEqual("forge-server", builderViewModel.SelectedServerPlatform!.Id, "legacy server fallback");
    builderViewModel.SelectedMinecraftVersion = "1.20.2";
    AssertEqual("forge-client", builderViewModel.SelectedClientPlatform!.Id, "valid client choice is preserved");
    builderViewModel.SelectedClientPlatform = builderViewModel.ClientPlatforms.Single(platform =>
        platform.Id == "neoforge-client");
    builderViewModel.SelectedServerPlatform = builderViewModel.ServerPlatforms.Single(platform =>
        platform.Id == "neoforge-server");
    builderViewModel.SelectedMinecraftVersion = "1.20.1";
    AssertEqual("fabric-client", builderViewModel.SelectedClientPlatform!.Id, "invalid client choice gets a compatible fallback");
    AssertEqual("fabric-server", builderViewModel.SelectedServerPlatform!.Id, "invalid server choice follows the compatible client fallback");
    AssertTrue(
        builderViewModel.ClientPlatformGuidance.Contains("Available for Minecraft 1.20.1", StringComparison.Ordinal),
        "loader guidance names the selected Minecraft release");
    builderViewModel.CommitPlan(readyPlan);
    AssertEqual(2, builderViewModel.DraftItems.Count, "builder commits selected content and required dependency together");
    AssertTrue(
        builderViewModel.DraftItems.Any(item => item.IsDependency && item.VersionId == "library-compatible"),
        "builder automatically places required dependency in draft");
    AssertTrue(
        builderViewModel.DraftStatusText.Contains("required dependency automatically", StringComparison.OrdinalIgnoreCase),
        "builder explains automatic required dependency addition");

    var clientOnlyPlacement = PackDependencyResolver.DeterminePlacement(
        compatibleLibrary,
        ServerContentKind.Mod,
        PackBuildTarget.ClientAndServer,
        ["fabric"],
        ["fabric"],
        out _);
    AssertEqual(
        PackContentPlacement.Client,
        clientOnlyPlacement,
        "client-only content cannot enter the server draft");
    var pluginWithoutLoaderMetadata = packRootVersion with
    {
        ProjectId = "plugin-project",
        VersionId = "plugin-version",
        Loaders = [],
        Environment = "client_and_server"
    };
    var pluginPlacement = PackDependencyResolver.DeterminePlacement(
        pluginWithoutLoaderMetadata,
        ServerContentKind.Plugin,
        PackBuildTarget.ClientAndServer,
        ["fabric"],
        ["paper"],
        out _);
    AssertEqual(
        PackContentPlacement.Server,
        pluginPlacement,
        "plugin content cannot enter the client draft without loader metadata");
    var undeclaredPlacement = PackDependencyResolver.DeterminePlacement(
        packRootVersion with { Environment = string.Empty },
        ServerContentKind.Mod,
        PackBuildTarget.ClientAndServer,
        ["fabric"],
        ["fabric"],
        out var undeclaredWarning);
    AssertEqual(
        PackContentPlacement.Review,
        undeclaredPlacement,
        "undeclared side metadata requires manual placement review");
    AssertTrue(
        undeclaredWarning.Contains("placement", StringComparison.OrdinalIgnoreCase),
        "undeclared side metadata explains review requirement");

    var mapServerRoot = Path.Combine(testRoot, "legacy-map-server");
    var mapWorldRoot = Path.Combine(mapServerRoot, "mapworld");
    var mapRegionRoot = Path.Combine(mapWorldRoot, "region");
    var mapNetherRegionRoot = Path.Combine(mapWorldRoot, "DIM-1", "region");
    var mapPlayersRoot = Path.Combine(mapWorldRoot, "players");
    Directory.CreateDirectory(mapRegionRoot);
    Directory.CreateDirectory(mapNetherRegionRoot);
    Directory.CreateDirectory(mapPlayersRoot);
    await File.WriteAllTextAsync(
        Path.Combine(mapServerRoot, "server.properties"),
        "level-name=mapworld\n");
    CreateLegacyLevelFile(Path.Combine(mapWorldRoot, "level.dat"), 8, 70, 8);
    CreateLegacyRegionFile(Path.Combine(mapRegionRoot, "r.0.0.mca"));
    CreateLegacyPlayerFile(
        Path.Combine(mapPlayersRoot, "TestPlayer.dat"),
        8.5,
        71,
        8.5,
        90,
        0,
        Guid.Parse("12345678-1234-5678-90ab-cdef12345678"));

    var mapProfile = new ServerProfile
    {
        Id = "legacy-map-test",
        DisplayName = "Legacy map test",
        ServerType = "Forge",
        MinecraftVersion = "1.6.4",
        ServerDirectory = mapServerRoot
    };
    var mapService = new LegacyAnvilWorldMapService(Path.Combine(testRoot, "map-cache"));
    var discoveredWorld = await mapService.DiscoverAsync(mapProfile);
    AssertEqual("mapworld", discoveredWorld.LevelName, "map discovers server.properties level-name");
    AssertEqual(8, discoveredWorld.SpawnX, "map reads SpawnX from level.dat");
    AssertEqual(70, discoveredWorld.SpawnY, "map reads SpawnY from level.dat");
    AssertEqual(8, discoveredWorld.SpawnZ, "map reads SpawnZ from level.dat");
    AssertEqual(2, discoveredWorld.Dimensions.Count, "map discovers overworld and Nether dimensions");
    AssertEqual("Overworld", discoveredWorld.Dimensions[0].DisplayName, "map orders the overworld first");
    AssertEqual("Nether", discoveredWorld.Dimensions[1].DisplayName, "map names DIM-1 as Nether");
    AssertEqual(120, discoveredWorld.Dimensions[1].SurfaceMaximumY, "Nether renderer stays below the bedrock roof");

    var mapRequest = new WorldMapRenderRequest(
        mapProfile,
        discoveredWorld.Dimensions[0],
        8,
        8,
        64);
    var firstMapRender = await mapService.RenderAsync(mapRequest);
    AssertTrue(File.Exists(firstMapRender.ImagePath), "map writes a cached image outside the world folder");
    AssertTrue(
        Path.GetFullPath(firstMapRender.ImagePath).StartsWith(
            Path.GetFullPath(Path.Combine(testRoot, "map-cache")),
            StringComparison.OrdinalIgnoreCase),
        "map image stays inside the configured cache root");
    AssertEqual(128, firstMapRender.PixelWidth, "map renders one block per pixel for a small area");
    AssertEqual(1, firstMapRender.LoadedChunkCount, "map counts the synthetic region chunk");
    AssertEqual(1, firstMapRender.ChangedChunkCount, "map parses a new legacy chunk once");
    AssertTrue(firstMapRender.HasTerrain, "map reports generated terrain");
    var mapBytes = await File.ReadAllBytesAsync(firstMapRender.ImagePath);
    AssertTrue(mapBytes is [0x42, 0x4D, ..], "map cache uses a valid BMP header");
    var cachedMapRender = await mapService.RenderAsync(mapRequest);
    AssertEqual(firstMapRender.ImagePath, cachedMapRender.ImagePath, "unchanged region headers reuse the map cache");
    AssertEqual(0, cachedMapRender.ChangedChunkCount, "cached map avoids reparsing unchanged chunks");

    var playerPositions = await mapService.ReadPlayerPositionsAsync(
        mapProfile,
        discoveredWorld,
        ["TestPlayer"]);
    AssertEqual(1, playerPositions.Count, "map reads a requested legacy player file");
    AssertEqual("TestPlayer", playerPositions[0].PlayerName, "map preserves tracked player casing");
    AssertEqual(8.5, playerPositions[0].X, "map reads player X position");
    AssertEqual(71d, playerPositions[0].Y, "map reads player Y position");
    AssertEqual(8.5, playerPositions[0].Z, "map reads player Z position");
    AssertEqual(90f, playerPositions[0].Yaw, "map reads player yaw");
    AssertEqual(0, playerPositions[0].DimensionId, "map reads the player's saved dimension");
    AssertTrue(playerPositions[0].PlayerId is not null, "map reads the player's legacy UUID fields");
    AssertEqual(
        0,
        (await mapService.ReadPlayerPositionsAsync(mapProfile, discoveredWorld, ["SomeoneElse"])).Count,
        "map does not scan unrelated player names when showing online players");

    var modernMapProfile = new ServerProfile
    {
        Id = "modern-map-test",
        DisplayName = "Modern map test",
        ServerType = "Fabric",
        MinecraftVersion = "1.20.1",
        ServerDirectory = mapServerRoot
    };
    await AssertThrowsAsync<NotSupportedException>(
        () => mapService.DiscoverAsync(modernMapProfile),
        "map reports known modern palette worlds as unsupported instead of rendering them incorrectly");

    var escapedWorldProfile = new ServerProfile
    {
        Id = "escaped-map-test",
        DisplayName = "Escaped map test",
        ServerDirectory = Path.Combine(testRoot, "escaped-map-server")
    };
    Directory.CreateDirectory(escapedWorldProfile.ServerDirectory);
    await File.WriteAllTextAsync(
        Path.Combine(escapedWorldProfile.ServerDirectory, "server.properties"),
        "level-name=..\\legacy-map-server\\mapworld\n");
    await AssertThrowsAsync<InvalidDataException>(
        () => mapService.DiscoverAsync(escapedWorldProfile),
        "map rejects a world path that escapes the server folder");

    var upgradedMojangSkinUri = MojangPlayerAvatarService.NormalizeTrustedSkinUri(
        "http://textures.minecraft.net/texture/example");
    AssertEqual("https", upgradedMojangSkinUri!.Scheme, "Mojang texture links are upgraded to HTTPS");
    AssertTrue(
        MojangPlayerAvatarService.NormalizeTrustedSkinUri("https://example.com/texture/example") is null,
        "player skins reject non-Mojang texture hosts");
    AssertTrue(
        MojangPlayerAvatarService.NormalizeTrustedSkinUri("file:///C:/skin.png") is null,
        "player skins reject non-HTTP texture schemes");

    Console.WriteLine("Configuration dashboard, pack builder, content management, map parsing, launcher detection, and Java compatibility tests passed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Configuration dashboard service tests failed: {exception}");
    Environment.ExitCode = 1;
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static void CreateLegacyLevelFile(string path, int spawnX, int spawnY, int spawnZ)
{
    using var payload = new MemoryStream();
    payload.WriteByte(10);
    WriteNbtString(payload, string.Empty);
    WriteNamedNbtTag(payload, 10, "Data");
    WriteNamedNbtInt(payload, "SpawnX", spawnX);
    WriteNamedNbtInt(payload, "SpawnY", spawnY);
    WriteNamedNbtInt(payload, "SpawnZ", spawnZ);
    payload.WriteByte(0);
    payload.WriteByte(0);
    WriteGzipFile(path, payload.ToArray());
}

static void CreateLegacyPlayerFile(
    string path,
    double x,
    double y,
    double z,
    float yaw,
    int dimension,
    Guid playerId)
{
    using var payload = new MemoryStream();
    payload.WriteByte(10);
    WriteNbtString(payload, string.Empty);

    WriteNamedNbtTag(payload, 9, "Pos");
    payload.WriteByte(6);
    WriteNbtInt(payload, 3);
    WriteNbtLong(payload, BitConverter.DoubleToInt64Bits(x));
    WriteNbtLong(payload, BitConverter.DoubleToInt64Bits(y));
    WriteNbtLong(payload, BitConverter.DoubleToInt64Bits(z));

    WriteNamedNbtTag(payload, 9, "Rotation");
    payload.WriteByte(5);
    WriteNbtInt(payload, 2);
    WriteNbtInt(payload, BitConverter.SingleToInt32Bits(yaw));
    WriteNbtInt(payload, BitConverter.SingleToInt32Bits(0));
    WriteNamedNbtInt(payload, "Dimension", dimension);

    var compactId = playerId.ToString("N");
    WriteNamedNbtLong(payload, "UUIDMost", unchecked((long)Convert.ToUInt64(compactId[..16], 16)));
    WriteNamedNbtLong(payload, "UUIDLeast", unchecked((long)Convert.ToUInt64(compactId[16..], 16)));
    payload.WriteByte(0);
    WriteGzipFile(path, payload.ToArray());
}

static void CreateLegacyRegionFile(string path)
{
    var blocks = new byte[4096];
    for (var z = 0; z < 16; z++)
    {
        for (var x = 0; x < 16; x++)
        {
            blocks[4 * 256 + z * 16 + x] = 2;
        }
    }

    using var nbt = new MemoryStream();
    nbt.WriteByte(10);
    WriteNbtString(nbt, string.Empty);
    WriteNamedNbtTag(nbt, 10, "Level");
    WriteNamedNbtTag(nbt, 9, "Sections");
    nbt.WriteByte(10);
    WriteNbtInt(nbt, 1);
    WriteNamedNbtTag(nbt, 1, "Y");
    nbt.WriteByte(0);
    WriteNamedNbtTag(nbt, 7, "Blocks");
    WriteNbtInt(nbt, blocks.Length);
    nbt.Write(blocks);
    WriteNamedNbtTag(nbt, 7, "Data");
    WriteNbtInt(nbt, 2048);
    nbt.Write(new byte[2048]);
    nbt.WriteByte(0);
    nbt.WriteByte(0);
    nbt.WriteByte(0);

    byte[] compressed;
    using (var compressedStream = new MemoryStream())
    {
        using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(nbt.ToArray());
        }

        compressed = compressedStream.ToArray();
    }

    var chunkLength = compressed.Length + 1;
    var sectorCount = (4 + chunkLength + 4095) / 4096;
    var header = new byte[8192];
    var location = (2 << 8) | sectorCount;
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), location);
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4096, 4), 1);

    using var region = File.Create(path);
    region.Write(header);
    Span<byte> lengthBytes = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(lengthBytes, chunkLength);
    region.Write(lengthBytes);
    region.WriteByte(2);
    region.Write(compressed);
    var allocatedLength = 8192 + sectorCount * 4096;
    region.SetLength(allocatedLength);
}

static void WriteGzipFile(string path, byte[] payload)
{
    using var file = File.Create(path);
    using var gzip = new GZipStream(file, CompressionLevel.Optimal);
    gzip.Write(payload);
}

static void WriteNamedNbtTag(Stream stream, byte type, string name)
{
    stream.WriteByte(type);
    WriteNbtString(stream, name);
}

static void WriteNamedNbtInt(Stream stream, string name, int value)
{
    WriteNamedNbtTag(stream, 3, name);
    WriteNbtInt(stream, value);
}

static void WriteNamedNbtLong(Stream stream, string name, long value)
{
    WriteNamedNbtTag(stream, 4, name);
    WriteNbtLong(stream, value);
}

static void WriteNbtString(Stream stream, string value)
{
    var bytes = Encoding.UTF8.GetBytes(value);
    Span<byte> length = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
    stream.Write(length);
    stream.Write(bytes);
}

static void WriteNbtInt(Stream stream, int value)
{
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(bytes, value);
    stream.Write(bytes);
}

static void WriteNbtLong(Stream stream, long value)
{
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteInt64BigEndian(bytes, value);
    stream.Write(bytes);
}

static void CreateJar(
    string path,
    string mainClass,
    IReadOnlyList<string> entries,
    int classFileMajor = 52)
{
    using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
    var manifestEntry = archive.CreateEntry("META-INF/MANIFEST.MF");
    using (var writer = new StreamWriter(manifestEntry.Open()))
    {
        writer.Write($"Manifest-Version: 1.0\r\nMain-Class: {mainClass}\r\n\r\n");
    }

    var mainClassEntryName = mainClass.Replace('.', '/') + ".class";
    foreach (var entryName in entries.Append(mainClassEntryName).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var entry = archive.CreateEntry(entryName);
        if (entryName.Equals(mainClassEntryName, StringComparison.OrdinalIgnoreCase))
        {
            using var stream = entry.Open();
            stream.Write([
                0xCA, 0xFE, 0xBA, 0xBE,
                0x00, 0x00,
                (byte)(classFileMajor >> 8),
                (byte)classFileMajor
            ]);
        }
    }
}

static void CreateJarWithTextEntries(
    string path,
    IReadOnlyDictionary<string, string> entries)
{
    using var archive = System.IO.Compression.ZipFile.Open(
        path,
        System.IO.Compression.ZipArchiveMode.Create);
    foreach (var entry in entries)
    {
        var archiveEntry = archive.CreateEntry(entry.Key);
        using var writer = new StreamWriter(archiveEntry.Open());
        writer.Write(entry.Value);
    }
}

static void AssertTrue(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Assertion failed: {description}");
    }
}

static void AssertEqual<T>(T expected, T actual, string description)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"Assertion failed: {description}. Expected {expected}; received {actual}.");
    }
}

static void AssertThrows<TException>(Action action, string description)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(
        $"Assertion failed: {description}. Expected {typeof(TException).Name}.");
}

static ServerContentVersion MakePackVersion(
    string projectId,
    string versionId,
    string name,
    string versionNumber,
    string minecraftVersion,
    string loader,
    string environment,
    IReadOnlyList<ServerContentDependency>? dependencies = null) =>
    new(
        "stub",
        projectId,
        versionId,
        name,
        versionNumber,
        "release",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        [minecraftVersion],
        [loader],
        environment,
        [],
        dependencies ?? []);

static async Task AssertThrowsAsync<TException>(Func<Task> action, string description)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(
        $"Assertion failed: {description}. Expected {typeof(TException).Name}.");
}

sealed class StubProfileValidator : IProfileValidator
{
    private readonly ProfileValidationResult _result;

    public StubProfileValidator(ProfileValidationResult result)
    {
        _result = result;
    }

    public ProfileValidationResult Validate(ServerProfile profile) => _result;
}

sealed class StubServerContentCatalogService : IServerContentCatalogService
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ServerContentVersion>> _versionsByProject;
    private readonly IReadOnlyDictionary<string, ServerContentVersion> _versionsById;

    public StubServerContentCatalogService(
        IReadOnlyDictionary<string, IReadOnlyList<ServerContentVersion>> versionsByProject,
        IReadOnlyDictionary<string, ServerContentVersion> versionsById)
    {
        _versionsByProject = versionsByProject;
        _versionsById = versionsById;
    }

    public string ProviderId => "stub";

    public Task<ServerContentSearchPage> SearchAsync(
        string query,
        string minecraftVersion,
        ServerContentKind kind,
        IReadOnlyList<string> loaderIds,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ServerContentSearchPage([], offset, limit, 0));

    public Task<IReadOnlyList<ServerContentVersion>> GetVersionsAsync(
        string projectId,
        string minecraftVersion,
        IReadOnlyList<string> loaderIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_versionsByProject.TryGetValue(projectId, out var versions) ? versions : []);

    public Task<ServerContentVersion> GetVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_versionsById.TryGetValue(versionId, out var version)
            ? version
            : throw new InvalidDataException($"Unknown test version: {versionId}"));
}

sealed class StubPackContentCatalogProvider : IPackContentCatalogProvider
{
    private readonly IReadOnlyList<ServerContentProject> _projects;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ServerContentVersion>> _versionsByProject;
    private readonly IReadOnlyDictionary<string, ServerContentVersion> _versionsById;

    public StubPackContentCatalogProvider(
        string providerId,
        IReadOnlyList<ServerContentProject> projects,
        IReadOnlyDictionary<string, IReadOnlyList<ServerContentVersion>> versionsByProject,
        IReadOnlyDictionary<string, ServerContentVersion> versionsById)
    {
        ProviderId = providerId;
        _projects = projects;
        _versionsByProject = versionsByProject;
        _versionsById = versionsById;
    }

    public string ProviderId { get; }

    public string DisplayName => "Test provider";

    public Task<ServerContentSearchPage> SearchPackContentAsync(
        PackCatalogSearchRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ServerContentSearchPage(
            _projects,
            request.Offset,
            request.Limit,
            _projects.Count));

    public Task<IReadOnlyList<ServerContentVersion>> GetPackVersionsAsync(
        string projectId,
        string minecraftVersion,
        IReadOnlyList<string> loaderIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_versionsByProject.TryGetValue(projectId, out var versions) ? versions : []);

    public Task<ServerContentVersion> GetPackVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_versionsById.TryGetValue(versionId, out var version)
            ? version
            : throw new InvalidDataException($"Unknown test version: {versionId}"));

    public Task<IReadOnlyList<string>> GetMinecraftVersionsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(["1.20.1"]);
}

sealed class StubPackContentDownloadProvider : IPackContentDownloadProvider
{
    private readonly IReadOnlyDictionary<string, byte[]> _content;
    private readonly string? _failFileName;

    public StubPackContentDownloadProvider(
        string providerId,
        IReadOnlyDictionary<string, byte[]> content,
        string? failFileName = null)
    {
        ProviderId = providerId;
        _content = content;
        _failFileName = failFileName;
    }

    public string ProviderId { get; }

    public void ValidateFile(ServerContentFile file)
    {
        if (!_content.ContainsKey(file.DownloadUri.AbsoluteUri))
        {
            throw new InvalidDataException($"No test content exists for {file.DownloadUri}.");
        }
    }

    public async Task DownloadAndVerifyAsync(
        ServerContentFile file,
        string destinationPath,
        Action<long>? reportDownloadedBytes = null,
        CancellationToken cancellationToken = default)
    {
        ValidateFile(file);
        if (file.FileName.Equals(_failFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Simulated provider download failure.");
        }

        var bytes = _content[file.DownloadUri.AbsoluteUri];
        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
        reportDownloadedBytes?.Invoke(bytes.LongLength);
    }
}

sealed class StubServerBaselineInstaller(bool writesAcceptedEula = false) : IServerBaselineInstaller
{
    public bool CanInstall(string loaderId) =>
        loaderId.Equals("stub-loader", StringComparison.OrdinalIgnoreCase);

    public Task<ServerBaselineInstallResult> InstallAsync(
        ServerBaselineInstallRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanInstall(request.LoaderId))
        {
            throw new NotSupportedException();
        }

        cancellationToken.ThrowIfCancellationRequested();
        const string launcherFileName = "minecraft_server.1.20.1.jar";
        var launcherPath = Path.Combine(request.ServerDirectory, launcherFileName);
        using (var archive = System.IO.Compression.ZipFile.Open(
            launcherPath,
            System.IO.Compression.ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry("META-INF/MANIFEST.MF");
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                writer.Write("Manifest-Version: 1.0\r\nMain-Class: net.minecraft.server.Main\r\n\r\n");
            }

            var classEntry = archive.CreateEntry("net/minecraft/server/Main.class");
            using var classStream = classEntry.Open();
            classStream.Write([0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x3D]);
        }

        if (writesAcceptedEula)
        {
            File.WriteAllText(Path.Combine(request.ServerDirectory, "eula.txt"), "eula=true\r\n");
        }

        progress?.Report("Installed the deterministic test server baseline.");
        return Task.FromResult(new ServerBaselineInstallResult(
            true,
            launcherFileName,
            "Installed the deterministic test server baseline."));
    }
}

sealed class StubPackDraftOutputService : IPackDraftOutputService
{
    public Task<PackOutputPlan> CreatePlanAsync(
        PackOutputRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PackOutputResult> CreateOutputAsync(
        PackOutputPlan plan,
        IProgress<PackOutputProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

sealed class StubPackPlatformVersionService : IPackPlatformVersionService
{
    public bool CanResolve(string platformId) =>
        !platformId.Equals("paper-server", StringComparison.OrdinalIgnoreCase)
        && !platformId.Equals("hybrid-forge-server", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<PackPlatformVersionOption>> GetVersionsAsync(
        string platformId,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var loaderId = platformId switch
        {
            "vanilla-client" or "vanilla-server" => "minecraft",
            "fabric-client" or "fabric-server" => "fabric-loader",
            "quilt-client" or "quilt-server" => "quilt-loader",
            "forge-client" or "forge-server" => "forge",
            "neoforge-client" or "neoforge-server" => "neoforge",
            _ => throw new NotSupportedException()
        };
        var canPrepareServer = platformId.EndsWith("-server", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<PackPlatformVersionOption>>(
        [
            new PackPlatformVersionOption(
                platformId,
                loaderId,
                loaderId == "minecraft" ? minecraftVersion : "1.0.0",
                true,
                canPrepareServer)
        ]);
    }
}

sealed class FailingPackContentCatalogProvider : IPackContentCatalogProvider
{
    public string ProviderId => "failing";

    public string DisplayName => "Unavailable test provider";

    public Task<ServerContentSearchPage> SearchPackContentAsync(
        PackCatalogSearchRequest request,
        CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("Simulated provider outage.");

    public Task<IReadOnlyList<ServerContentVersion>> GetPackVersionsAsync(
        string projectId,
        string minecraftVersion,
        IReadOnlyList<string> loaderIds,
        CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("Simulated provider outage.");

    public Task<ServerContentVersion> GetPackVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("Simulated provider outage.");

    public Task<IReadOnlyList<string>> GetMinecraftVersionsAsync(
        CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("Simulated provider outage.");
}

sealed class InMemoryCurseForgeApiKeyStore : ICurseForgeApiKeyStore
{
    public string? Value { get; set; }

    public string? Read() => Value;

    public void Save(string apiKey) => Value = apiKey;

    public void Remove() => Value = null;
}

sealed class StubCurseForgeApiKeyService : ICurseForgeApiKeyService
{
    private string? _apiKey;

    public string? GetApiKey() => _apiKey;

    public Task<CurseForgeApiKeyStatus> ValidateStoredAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CurseForgeApiKeyStatus(
            _apiKey is not null,
            _apiKey is not null,
            _apiKey is null ? "Not connected." : "Connected."));

    public Task<CurseForgeApiKeyStatus> ValidateAndStoreAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        _apiKey = apiKey.Trim();
        return Task.FromResult(new CurseForgeApiKeyStatus(true, true, "Connected."));
    }

    public void Remove() => _apiKey = null;
}

sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(_responseFactory(request));
}
