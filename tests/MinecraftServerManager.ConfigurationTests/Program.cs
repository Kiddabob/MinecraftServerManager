using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

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

var testRoot = Path.Combine(
    Path.GetTempPath(),
    "MinecraftServerManager-ConfigurationTests",
    Guid.NewGuid().ToString("N"));

try
{
    Directory.CreateDirectory(Path.Combine(testRoot, "config", "ExampleMod"));
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

    var profile = new ServerProfile
    {
        Id = $"configuration-test-{Guid.NewGuid():N}",
        DisplayName = "Hybrid test",
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

    var legendsFolder = Path.Combine(testRoot, "tekkit-legends");
    Directory.CreateDirectory(legendsFolder);
    Directory.CreateDirectory(Path.Combine(legendsFolder, "mods"));
    Directory.CreateDirectory(Path.Combine(legendsFolder, "plugins"));
    await File.WriteAllBytesAsync(Path.Combine(legendsFolder, "CryofinityLegends.jar"), [1]);
    await File.WriteAllBytesAsync(Path.Combine(legendsFolder, "minecraft_server.1.7.10.jar"), [1]);
    await File.WriteAllTextAsync(
        Path.Combine(legendsFolder, "start.bat"),
        "@echo off\r\n:start\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\necho menu\r\njava -Dfml.debugExit=true -Xms1G -jar \"CryofinityLegends.jar\" nogui -Dfml.debugExit=true\r\n");
    await File.WriteAllTextAsync(
        Path.Combine(legendsFolder, "start.sh"),
        "java -Xmx2G -Xms1G -jar \"TekkitLegends.jar\" nogui\n");
    await File.WriteAllBytesAsync(Path.Combine(legendsFolder, "TekkitLegends.jar"), [1]);
    var legendsDetection = ServerFolderDetector.Detect(legendsFolder)!;
    AssertEqual("start.bat", legendsDetection.LaunchScript, "Legends launch script detection");
    AssertEqual("CryofinityLegends.jar", legendsDetection.ServerJar, "Legends scripted launcher selection");
    AssertEqual("1.7.10", legendsDetection.MinecraftVersion, "Legends companion JAR version detection");
    AssertEqual("Hybrid", legendsDetection.ServerType, "Legends mods and plugins classification");
    AssertTrue(
        legendsDetection.EffectiveJavaArguments.Contains("-Dfml.debugExit=true"),
        "Legends JVM argument preservation");

    var classicFolder = Path.Combine(testRoot, "tekkit-classic");
    Directory.CreateDirectory(classicFolder);
    Directory.CreateDirectory(Path.Combine(classicFolder, "mods"));
    Directory.CreateDirectory(Path.Combine(classicFolder, "plugins"));
    await File.WriteAllBytesAsync(Path.Combine(classicFolder, "TekkitClassic.jar"), [1]);
    await File.WriteAllTextAsync(
        Path.Combine(classicFolder, "launch.bat"),
        "\"C:\\Program Files\\Java\\jre1.8.0_191\\bin\\javaw.exe\" -Xms1G -Xmx6G -jar TekkitClassic.jar -o true\r\n");
    await File.WriteAllTextAsync(
        Path.Combine(classicFolder, "server.log"),
        "2012-06-01 10:20:30 [INFO] Starting minecraft server version 1.2.5\r\n" + new string('x', (2 * 1024 * 1024) + 20));
    var classicDetection = ServerFolderDetector.Detect(classicFolder)!;
    AssertEqual("launch.bat", classicDetection.LaunchScript, "Classic launch script detection");
    AssertEqual("TekkitClassic.jar", classicDetection.ServerJar, "Classic launcher selection");
    AssertEqual("1.2.5", classicDetection.MinecraftVersion, "Classic log version detection");
    AssertEqual(6144, JavaArgumentUtilities.GetMaximumMemoryMegabytes(classicDetection.EffectiveJavaArguments)!.Value, "Classic maximum memory detection");
    AssertTrue(classicDetection.EffectiveServerArguments.SequenceEqual(["-o", "true"]), "Classic server argument preservation");

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
    AssertEqual(2, modernForgeDetection.EffectiveDirectLaunchArguments.Count, "modern Forge argument-file preservation");

    var replacedMemory = JavaArgumentUtilities.ReplaceMemoryArguments(
        ["-server", "-Xms512M", "-Xmx1G"],
        2048,
        6144,
        ["-Dexample=true"]);
    AssertTrue(replacedMemory.SequenceEqual(["-Xms2G", "-Xmx6G", "-server", "-Dexample=true"]), "memory argument replacement");

    var runtimeService = new JavaRuntimeService();
    AssertEqual(8, runtimeService.GetRecommendedJavaMajor("1.16.5")!.Value, "Java 8 baseline");
    AssertEqual(16, runtimeService.GetRecommendedJavaMajor("1.17")!.Value, "Java 16 baseline");
    AssertEqual(17, runtimeService.GetRecommendedJavaMajor("1.18.2")!.Value, "Java 17 baseline");
    AssertEqual(21, runtimeService.GetRecommendedJavaMajor("1.20.5")!.Value, "Java 21 baseline");

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
            "@user_jvm_args.txt",
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

    Console.WriteLine("Configuration dashboard, launcher detection, and Java compatibility tests passed.");
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
