using MinecraftServerManager.Models;
using MinecraftServerManager.Services;

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

    Console.WriteLine("Configuration dashboard service tests passed.");
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
    where T : IEquatable<T>
{
    if (!expected.Equals(actual))
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
