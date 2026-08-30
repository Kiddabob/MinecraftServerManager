namespace MinecraftServerManager.Models;

public sealed record ServerProfileDuplicateRequest(
    ServerProfile SourceProfile,
    string DisplayName,
    string DestinationParentDirectory,
    bool IncludeWorldData);

public sealed record ServerProfileDuplicateResult(
    string ServerDirectory,
    int CopiedFileCount,
    bool IncludedWorldData,
    ProfileImportResult ProfileImport);
