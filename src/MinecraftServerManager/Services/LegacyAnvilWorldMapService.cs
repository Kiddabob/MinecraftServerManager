using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Models;

namespace MinecraftServerManager.Services;

public sealed class LegacyAnvilWorldMapService : IWorldMapService
{
    private const int RegionHeaderLength = 8 * 1024;
    private const int RegionSideBlocks = 512;
    private const int ChunkSideBlocks = 16;
    private const int MaximumChunkCompressedBytes = 4 * 1024 * 1024;
    private const int MaximumChunkNbtBytes = 16 * 1024 * 1024;
    private const int MaximumPlayerNbtBytes = 4 * 1024 * 1024;
    private const int MaximumMapPixelsPerSide = 1_536;
    private const int MaximumCachedChunks = 50_000;
    private const string RendererVersion = "anvil-v2";

    private static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kidda.MinecraftServerManager",
        "MapCache");

    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly Dictionary<string, CachedChunk> _chunkCache = new(StringComparer.OrdinalIgnoreCase);

    public LegacyAnvilWorldMapService()
        : this(DefaultCacheRoot)
    {
    }

    public LegacyAnvilWorldMapService(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    public Task<WorldMapDescriptor> DiscoverAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Task.Run(() => Discover(profile, cancellationToken), cancellationToken);
    }

    public async Task<WorldMapRenderResult> RenderAsync(
        WorldMapRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RadiusBlocks is < 64 or > 8_192)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Map radius must be between 64 and 8,192 blocks.");
        }

        await _renderGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Render(request, cancellationToken), cancellationToken);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    public Task<IReadOnlyList<WorldMapPlayerPosition>> ReadPlayerPositionsAsync(
        ServerProfile profile,
        WorldMapDescriptor world,
        IReadOnlyCollection<string>? playerNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(world);
        return Task.Run<IReadOnlyList<WorldMapPlayerPosition>>(
            () => ReadPlayerPositions(profile, world, playerNames, cancellationToken),
            cancellationToken);
    }

    private static WorldMapDescriptor Discover(ServerProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serverRoot = NormalizeDirectory(profile.ServerDirectory);
        if (!Directory.Exists(serverRoot))
        {
            throw new DirectoryNotFoundException("The selected server folder does not exist.");
        }

        EnsureNotLink(serverRoot, "The selected server folder is a symbolic link and cannot be inspected safely.");

        var levelName = ReadLevelName(Path.Combine(serverRoot, "server.properties"));
        var worldRoot = Path.GetFullPath(Path.Combine(serverRoot, levelName));
        EnsureWithinRoot(serverRoot, worldRoot);
        EnsureNotLink(worldRoot, "The configured world folder is a symbolic link and cannot be inspected safely.");
        if (!Directory.Exists(worldRoot))
        {
            throw new DirectoryNotFoundException($"The configured world folder '{levelName}' does not exist.");
        }

        EnsureContainedTreeHasNoLinks(
            serverRoot,
            worldRoot,
            "The configured world path contains a symbolic link and cannot be inspected safely.");
        var dimensions = new List<WorldMapDimension>();
        TryAddDimension(dimensions, worldRoot, "overworld", "Overworld", 0, 319);
        foreach (var directory in Directory.EnumerateDirectories(worldRoot, "DIM*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (!TryDescribeLegacyDimensionDirectory(name, out var numericId, out var displayName))
            {
                continue;
            }

            TryAddDimension(
                dimensions,
                directory,
                name,
                displayName,
                numericId,
                numericId == -1 ? 120 : 319);
        }

        DiscoverCustomDimensions(dimensions, serverRoot, worldRoot, cancellationToken);

        if (dimensions.Count == 0)
        {
            throw new InvalidDataException("No compatible Anvil region folders were found in the selected world.");
        }

        var (spawnX, spawnY, spawnZ) = ReadSpawn(Path.Combine(worldRoot, "level.dat"));
        return new WorldMapDescriptor(
            worldRoot,
            levelName,
            dimensions
                .OrderBy(dimension => dimension.NumericId == 0 ? int.MinValue : dimension.NumericId)
                .ToArray(),
            spawnX,
            spawnY,
            spawnZ);
    }

    private static void TryAddDimension(
        ICollection<WorldMapDimension> dimensions,
        string directory,
        string id,
        string displayName,
        int numericId,
        int surfaceMaximumY)
    {
        var regionDirectory = Path.Combine(directory, "region");
        if (!Directory.Exists(regionDirectory))
        {
            return;
        }

        EnsureNotLink(directory, $"Dimension '{displayName}' is a symbolic link and cannot be inspected safely.");
        dimensions.Add(new WorldMapDimension(
            id,
            displayName,
            Path.GetFullPath(directory),
            numericId,
            WorldMapFormat.Anvil,
            surfaceMaximumY));
    }

    private static bool TryDescribeLegacyDimensionDirectory(
        string name,
        out int numericId,
        out string displayName)
    {
        numericId = 0;
        displayName = string.Empty;
        if (name.Length <= 3 || !name.StartsWith("DIM", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = name[3..];
        if (int.TryParse(suffix, out numericId))
        {
            displayName = numericId switch
            {
                -1 => "Nether",
                1 => "The End",
                _ => $"Dimension {numericId}"
            };
            return true;
        }

        const string spaceStationPrefix = "_SPACESTATION";
        if (suffix.StartsWith(spaceStationPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(suffix[spaceStationPrefix.Length..], out numericId))
        {
            displayName = $"Space Station {numericId}";
            return true;
        }

        const string mystcraftPrefix = "_MYST";
        if (suffix.StartsWith(mystcraftPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(suffix[mystcraftPrefix.Length..], out numericId))
        {
            displayName = $"Mystcraft Age {numericId}";
            return true;
        }

        return false;
    }

    private static void DiscoverCustomDimensions(
        ICollection<WorldMapDimension> dimensions,
        string serverRoot,
        string worldRoot,
        CancellationToken cancellationToken)
    {
        var dimensionsRoot = Path.Combine(worldRoot, "dimensions");
        if (!Directory.Exists(dimensionsRoot))
        {
            return;
        }

        EnsureContainedTreeHasNoLinks(
            serverRoot,
            dimensionsRoot,
            "The custom-dimension path contains a symbolic link and cannot be inspected safely.");
        var pending = new Queue<(string Directory, string RelativePath, int Depth)>();
        foreach (var namespaceDirectory in Directory.EnumerateDirectories(dimensionsRoot).Take(256))
        {
            var info = new DirectoryInfo(namespaceDirectory);
            if (info.LinkTarget is not null)
            {
                throw new InvalidDataException(
                    "The custom-dimension path contains a symbolic link and cannot be inspected safely.");
            }

            pending.Enqueue((namespaceDirectory, info.Name, 0));
        }

        var visited = 0;
        var nextNumericId = 1_000;
        while (pending.TryDequeue(out var item) && visited++ < 2_000 && dimensions.Count < 128)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var regionDirectory = Path.Combine(item.Directory, "region");
            if (Directory.Exists(regionDirectory))
            {
                var separator = item.RelativePath.IndexOf(Path.DirectorySeparatorChar);
                var displayName = separator > 0
                    ? $"{item.RelativePath[..separator]}:{item.RelativePath[(separator + 1)..].Replace(Path.DirectorySeparatorChar, '/')}"
                    : item.RelativePath;
                TryAddDimension(
                    dimensions,
                    item.Directory,
                    $"dimensions/{item.RelativePath.Replace(Path.DirectorySeparatorChar, '/')}",
                    displayName,
                    nextNumericId++,
                    319);
            }

            if (item.Depth >= 4)
            {
                continue;
            }

            foreach (var childDirectory in Directory.EnumerateDirectories(item.Directory).Take(256))
            {
                var info = new DirectoryInfo(childDirectory);
                if (info.Name.Equals("region", StringComparison.OrdinalIgnoreCase)
                    || info.Name.Equals("entities", StringComparison.OrdinalIgnoreCase)
                    || info.Name.Equals("poi", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (info.LinkTarget is not null)
                {
                    throw new InvalidDataException(
                        "The custom-dimension path contains a symbolic link and cannot be inspected safely.");
                }

                pending.Enqueue((
                    childDirectory,
                    Path.Combine(item.RelativePath, info.Name),
                    item.Depth + 1));
            }
        }
    }

    private WorldMapRenderResult Render(WorldMapRenderRequest request, CancellationToken cancellationToken)
    {
        var dimension = request.Dimension;
        if (dimension.Format != WorldMapFormat.Anvil)
        {
            throw new NotSupportedException("This world format is not supported by the current map renderer.");
        }

        var serverRoot = NormalizeDirectory(request.Profile.ServerDirectory);
        var dimensionRoot = Path.GetFullPath(dimension.DirectoryPath);
        EnsureWithinRoot(serverRoot, dimensionRoot);
        EnsureContainedTreeHasNoLinks(
            serverRoot,
            dimensionRoot,
            "The selected dimension path contains a symbolic link and cannot be inspected safely.");

        var minimumX = checked(request.CenterX - request.RadiusBlocks);
        var minimumZ = checked(request.CenterZ - request.RadiusBlocks);
        var maximumX = checked(request.CenterX + request.RadiusBlocks - 1);
        var maximumZ = checked(request.CenterZ + request.RadiusBlocks - 1);
        var span = checked(request.RadiusBlocks * 2);
        var blocksPerPixel = 1;
        while ((span + blocksPerPixel - 1) / blocksPerPixel > MaximumMapPixelsPerSide)
        {
            blocksPerPixel *= 2;
        }

        var pixelWidth = (span + blocksPerPixel - 1) / blocksPerPixel;
        var pixelHeight = pixelWidth;
        var snapshots = ReadRegionSnapshots(
            Path.Combine(dimensionRoot, "region"),
            minimumX,
            minimumZ,
            maximumX,
            maximumZ,
            cancellationToken);
        var fingerprint = BuildFingerprint(request, snapshots, blocksPerPixel);
        var cacheDirectory = Path.Combine(
            _cacheRoot,
            SanitizeFileName(request.Profile.Id),
            SanitizeFileName(dimension.Id));
        var imagePath = Path.Combine(cacheDirectory, $"map-{fingerprint[..24]}.bmp");
        var loadedChunkCount = CountChunks(snapshots, minimumX, minimumZ, maximumX, maximumZ);

        if (!request.ForceRefresh && File.Exists(imagePath))
        {
            var cachedInfo = new FileInfo(imagePath);
            return new WorldMapRenderResult(
                imagePath,
                pixelWidth,
                pixelHeight,
                minimumX,
                minimumZ,
                maximumX,
                maximumZ,
                blocksPerPixel,
                loadedChunkCount,
                0,
                cachedInfo.LastWriteTimeUtc,
                loadedChunkCount > 0,
                fingerprint);
        }

        var colors = new int[pixelWidth * pixelHeight];
        var heights = new short[colors.Length];
        Array.Fill(heights, short.MinValue);
        FillBackground(colors, pixelWidth, pixelHeight);

        var changedChunkCount = 0;
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changedChunkCount += RenderRegion(
                snapshot,
                dimension.SurfaceMaximumY,
                minimumX,
                minimumZ,
                maximumX,
                maximumZ,
                blocksPerPixel,
                pixelWidth,
                colors,
                heights,
                cancellationToken);
        }

        Shade(colors, heights, pixelWidth, pixelHeight);
        var pixels = ToBgra(colors);
        BgraBitmapWriter.WriteAsync(imagePath, pixelWidth, pixelHeight, pixels, cancellationToken)
            .GetAwaiter()
            .GetResult();
        CleanupOldMaps(cacheDirectory, imagePath);
        TrimChunkCache();

        return new WorldMapRenderResult(
            imagePath,
            pixelWidth,
            pixelHeight,
            minimumX,
            minimumZ,
            maximumX,
            maximumZ,
            blocksPerPixel,
            loadedChunkCount,
            changedChunkCount,
            DateTimeOffset.UtcNow,
            loadedChunkCount > 0,
            fingerprint);
    }

    private int RenderRegion(
        RegionSnapshot snapshot,
        int surfaceMaximumY,
        int minimumX,
        int minimumZ,
        int maximumX,
        int maximumZ,
        int blocksPerPixel,
        int pixelWidth,
        int[] colors,
        short[] heights,
        CancellationToken cancellationToken)
    {
        var pending = new List<(string Key, CachedChunk Value)>();
        var surfaces = new List<LegacyChunkSurface>();
        var changedCount = 0;
        using var stream = new FileStream(
            snapshot.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.RandomAccess);

        for (var localZ = 0; localZ < 32; localZ++)
        {
            for (var localX = 0; localX < 32; localX++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkX = snapshot.RegionX * 32 + localX;
                var chunkZ = snapshot.RegionZ * 32 + localZ;
                if (!ChunkIntersects(chunkX, chunkZ, minimumX, minimumZ, maximumX, maximumZ))
                {
                    continue;
                }

                var index = localX + localZ * 32;
                var location = ReadUInt32BigEndian(snapshot.Header, index * 4);
                if ((location >> 8) == 0 || (location & 0xFF) == 0)
                {
                    continue;
                }

                var timestamp = ReadUInt32BigEndian(snapshot.Header, 4096 + index * 4);
                var cacheKey = $"{snapshot.Path}|{index}";
                if (_chunkCache.TryGetValue(cacheKey, out var cached)
                    && cached.Location == location
                    && cached.Timestamp == timestamp)
                {
                    surfaces.Add(cached.Surface);
                    continue;
                }

                try
                {
                    var surface = ReadChunkSurface(
                        stream,
                        location,
                        chunkX,
                        chunkZ,
                        surfaceMaximumY);
                    if (surface is not null)
                    {
                        surfaces.Add(surface);
                        pending.Add((cacheKey, new CachedChunk(location, timestamp, surface)));
                        changedCount++;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                        or InvalidDataException
                        or EndOfStreamException
                        or ArgumentException
                        or OverflowException)
                {
                    if (cached is not null)
                    {
                        surfaces.Add(cached.Surface);
                    }
                }
            }
        }

        var info = new FileInfo(snapshot.Path);
        info.Refresh();
        if (info.Exists
            && info.Length == snapshot.Length
            && info.LastWriteTimeUtc == snapshot.LastWriteTimeUtc)
        {
            foreach (var item in pending)
            {
                _chunkCache[item.Key] = item.Value;
            }
        }
        else
        {
            changedCount = 0;
        }

        foreach (var surface in surfaces)
        {
            CompositeSurface(
                surface,
                minimumX,
                minimumZ,
                maximumX,
                maximumZ,
                blocksPerPixel,
                pixelWidth,
                colors,
                heights);
        }

        return changedCount;
    }

    private static LegacyChunkSurface? ReadChunkSurface(
        Stream stream,
        uint location,
        int chunkX,
        int chunkZ,
        int surfaceMaximumY)
    {
        var sectorOffset = location >> 8;
        var sectorCount = location & 0xFF;
        var byteOffset = checked((long)sectorOffset * 4096L);
        var allocatedBytes = checked((int)sectorCount * 4096);
        if (sectorOffset < 2 || byteOffset > stream.Length - 5)
        {
            throw new InvalidDataException("A region chunk points outside its file.");
        }

        stream.Position = byteOffset;
        Span<byte> lengthBytes = stackalloc byte[4];
        ReadExactly(stream, lengthBytes);
        var chunkLength = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (chunkLength is < 2 or > MaximumChunkCompressedBytes
            || chunkLength > allocatedBytes - 4
            || byteOffset + 4L + chunkLength > stream.Length)
        {
            throw new InvalidDataException("A region chunk has an invalid compressed length.");
        }

        var compression = stream.ReadByte();
        if (compression < 0)
        {
            throw new EndOfStreamException();
        }

        var compressed = new byte[chunkLength - 1];
        ReadExactly(stream, compressed);
        var nbt = Decompress(compressed, (byte)compression, MaximumChunkNbtBytes);
        var paletteSurface = PaletteAnvilChunkDecoder.TryDecode(nbt, surfaceMaximumY);
        if (paletteSurface is not null)
        {
            return new LegacyChunkSurface(
                chunkX,
                chunkZ,
                paletteSurface.Colors,
                paletteSurface.Heights);
        }

        return ParseLegacySurface(nbt, chunkX, chunkZ, surfaceMaximumY);
    }

    private static LegacyChunkSurface? ParseLegacySurface(
        byte[] nbt,
        int chunkX,
        int chunkZ,
        int surfaceMaximumY)
    {
        var sections = new List<LegacySection>();
        var reader = new MinecraftNbtReader(nbt);

        bool VisitLevel(string name, byte type, MinecraftNbtReader current, int depth)
        {
            if (name.Equals("Sections", StringComparison.Ordinal) && type == 9)
            {
                var (elementType, count) = current.ReadListHeader();
                if (elementType != 10 || count > 64)
                {
                    for (var index = 0; index < count; index++)
                    {
                        current.SkipPayload(elementType, depth + 1);
                    }

                    return true;
                }

                for (var index = 0; index < count; index++)
                {
                    sbyte sectionY = -1;
                    byte[]? blocks = null;
                    byte[]? data = null;
                    byte[]? add = null;
                    current.ReadCompound((sectionName, sectionType, sectionReader, sectionDepth) =>
                    {
                        if (sectionName.Equals("Y", StringComparison.Ordinal) && sectionType == 1)
                        {
                            sectionY = sectionReader.ReadSignedByte();
                            return true;
                        }

                        if (sectionName.Equals("Blocks", StringComparison.Ordinal) && sectionType == 7)
                        {
                            blocks = sectionReader.ReadByteArray(8_192);
                            return true;
                        }

                        if (sectionName.Equals("Data", StringComparison.Ordinal) && sectionType == 7)
                        {
                            data = sectionReader.ReadByteArray(4_096);
                            return true;
                        }

                        if (sectionName.Equals("Add", StringComparison.Ordinal) && sectionType == 7)
                        {
                            add = sectionReader.ReadByteArray(4_096);
                            return true;
                        }

                        return false;
                    }, depth + 1);

                    if (sectionY >= 0 && blocks?.Length == 4096)
                    {
                        sections.Add(new LegacySection(sectionY, blocks, data, add));
                    }
                }

                return true;
            }

            return false;
        }

        reader.ReadRootCompound((name, type, current, depth) =>
        {
            if (name.Equals("Level", StringComparison.Ordinal) && type == 10)
            {
                current.ReadCompound(VisitLevel, depth + 1);
                return true;
            }

            return VisitLevel(name, type, current, depth);
        });

        if (sections.Count == 0)
        {
            return null;
        }

        sections.Sort((left, right) => right.Y.CompareTo(left.Y));
        var colors = new int[256];
        var heights = new short[256];
        Array.Fill(heights, short.MinValue);
        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                foreach (var section in sections)
                {
                    var sectionBaseY = section.Y * 16;
                    if (sectionBaseY > surfaceMaximumY)
                    {
                        continue;
                    }

                    var maximumLocalY = Math.Min(15, surfaceMaximumY - sectionBaseY);
                    for (var y = maximumLocalY; y >= 0; y--)
                    {
                        var blockIndex = y * 256 + z * 16 + x;
                        var blockId = (int)section.Blocks[blockIndex];
                        if (section.Add is not null && section.Add.Length > blockIndex / 2)
                        {
                            blockId |= ReadNibble(section.Add, blockIndex) << 8;
                        }

                        if (blockId == 0)
                        {
                            continue;
                        }

                        var metadata = section.Data is not null && section.Data.Length > blockIndex / 2
                            ? ReadNibble(section.Data, blockIndex)
                            : 0;
                        var columnIndex = z * 16 + x;
                        colors[columnIndex] = LegacyBlockColor(blockId, metadata);
                        heights[columnIndex] = (short)(sectionBaseY + y);
                        goto NextColumn;
                    }
                }

            NextColumn:
                ;
            }
        }

        return new LegacyChunkSurface(chunkX, chunkZ, colors, heights);
    }

    private static IReadOnlyList<WorldMapPlayerPosition> ReadPlayerPositions(
        ServerProfile profile,
        WorldMapDescriptor world,
        IReadOnlyCollection<string>? playerNames,
        CancellationToken cancellationToken)
    {
        var serverRoot = NormalizeDirectory(profile.ServerDirectory);
        var worldRoot = Path.GetFullPath(world.WorldRoot);
        EnsureWithinRoot(serverRoot, worldRoot);
        var modernPlayerDirectory = Path.Combine(worldRoot, "playerdata");
        var legacyPlayerDirectory = Path.Combine(worldRoot, "players");
        var playerDirectory = Directory.Exists(modernPlayerDirectory)
            ? modernPlayerDirectory
            : legacyPlayerDirectory;
        if (!Directory.Exists(playerDirectory))
        {
            return [];
        }

        EnsureNotLink(playerDirectory, "The player-data folder is a symbolic link and cannot be inspected safely.");
        EnsureContainedTreeHasNoLinks(
            serverRoot,
            playerDirectory,
            "The player-data path contains a symbolic link and cannot be inspected safely.");
        HashSet<string>? requestedNames = playerNames is null
            ? null
            : new HashSet<string>(playerNames, StringComparer.OrdinalIgnoreCase);
        if (requestedNames?.Count == 0)
        {
            return [];
        }

        var playerNamesById = ReadPlayerNameCache(serverRoot);
        var positions = new List<WorldMapPlayerPosition>();
        foreach (var path in Directory.EnumerateFiles(playerDirectory, "*.dat", SearchOption.TopDirectoryOnly).Take(2_000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || info.Length is <= 0 or > MaximumPlayerNbtBytes)
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(path);
            var filePlayerId = Guid.TryParse(fileName, out var parsedFileId)
                ? parsedFileId
                : (Guid?)null;
            var displayName = filePlayerId is not null
                && playerNamesById.TryGetValue(filePlayerId.Value, out var cachedName)
                    ? cachedName
                    : fileName;
            if (requestedNames is not null
                && !requestedNames.TryGetValue(displayName, out displayName))
            {
                continue;
            }

            try
            {
                var beforeLength = info.Length;
                var beforeWrite = info.LastWriteTimeUtc;
                var bytes = ReadCompressedFile(path, MaximumPlayerNbtBytes);
                var position = ParsePlayer(bytes, displayName, beforeWrite, filePlayerId);
                info.Refresh();
                if (position is not null
                    && info.Exists
                    && info.Length == beforeLength
                    && info.LastWriteTimeUtc == beforeWrite)
                {
                    positions.Add(position);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidDataException
                    or EndOfStreamException
                    or UnauthorizedAccessException
                    or ArgumentException)
            {
                // A partially written player file is retried on the next refresh.
            }
        }

        return positions;
    }

    private static WorldMapPlayerPosition? ParsePlayer(
        byte[] nbt,
        string playerName,
        DateTime savedUtc,
        Guid? fallbackPlayerId = null)
    {
        double[]? position = null;
        float[]? rotation = null;
        var dimension = 0;
        var dimensionKey = "overworld";
        long? uuidMost = null;
        long? uuidLeast = null;
        int[]? uuidParts = null;
        var reader = new MinecraftNbtReader(nbt);
        reader.ReadRootCompound((name, type, current, depth) =>
        {
            if (name.Equals("Pos", StringComparison.Ordinal) && type == 9)
            {
                var (elementType, count) = current.ReadListHeader();
                if (elementType == 6 && count is >= 3 and <= 16)
                {
                    position = new double[count];
                    for (var index = 0; index < count; index++)
                    {
                        position[index] = current.ReadDouble();
                    }
                }
                else
                {
                    for (var index = 0; index < count; index++)
                    {
                        current.SkipPayload(elementType, depth + 1);
                    }
                }

                return true;
            }

            if (name.Equals("Rotation", StringComparison.Ordinal) && type == 9)
            {
                var (elementType, count) = current.ReadListHeader();
                if (elementType == 5 && count is >= 2 and <= 16)
                {
                    rotation = new float[count];
                    for (var index = 0; index < count; index++)
                    {
                        rotation[index] = current.ReadSingle();
                    }
                }
                else
                {
                    for (var index = 0; index < count; index++)
                    {
                        current.SkipPayload(elementType, depth + 1);
                    }
                }

                return true;
            }

            if (name.Equals("Dimension", StringComparison.Ordinal) && type == 3)
            {
                dimension = current.ReadInt32();
                dimensionKey = DimensionKeyFromNumericId(dimension);
                return true;
            }

            if (name.Equals("Dimension", StringComparison.Ordinal) && type == 8)
            {
                dimensionKey = NormalizeDimensionKey(current.ReadString(), out dimension);
                return true;
            }

            if (name.Equals("UUIDMost", StringComparison.Ordinal) && type == 4)
            {
                uuidMost = current.ReadInt64();
                return true;
            }

            if (name.Equals("UUIDLeast", StringComparison.Ordinal) && type == 4)
            {
                uuidLeast = current.ReadInt64();
                return true;
            }

            if (name.Equals("UUID", StringComparison.Ordinal) && type == 11)
            {
                uuidParts = current.ReadIntArray(4);
                return true;
            }

            return false;
        });

        if (position is not { Length: >= 3 }
            || position.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            return null;
        }

        var playerId = fallbackPlayerId;
        if (uuidMost is not null && uuidLeast is not null)
        {
            var text = $"{unchecked((ulong)uuidMost.Value):x16}{unchecked((ulong)uuidLeast.Value):x16}";
            if (Guid.TryParseExact(text, "N", out var parsedId))
            {
                playerId = parsedId;
            }
        }
        else if (uuidParts is { Length: 4 })
        {
            var text = string.Concat(uuidParts.Select(part => unchecked((uint)part).ToString("x8")));
            if (Guid.TryParseExact(text, "N", out var parsedId))
            {
                playerId = parsedId;
            }
        }

        return new WorldMapPlayerPosition(
            playerName,
            playerId,
            position[0],
            position[1],
            position[2],
            rotation is { Length: >= 1 } ? rotation[0] : 0,
            dimension,
            dimensionKey,
            new DateTimeOffset(DateTime.SpecifyKind(savedUtc, DateTimeKind.Utc)));
    }

    private static IReadOnlyDictionary<Guid, string> ReadPlayerNameCache(string serverRoot)
    {
        var cachePath = Path.Combine(serverRoot, "usercache.json");
        if (!File.Exists(cachePath))
        {
            return new Dictionary<Guid, string>();
        }

        try
        {
            var info = new FileInfo(cachePath);
            if (info.LinkTarget is not null || info.Length is <= 0 or > 4 * 1024 * 1024)
            {
                return new Dictionary<Guid, string>();
            }

            using var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<Guid, string>();
            }

            var names = new Dictionary<Guid, string>();
            foreach (var entry in document.RootElement.EnumerateArray().Take(2_000))
            {
                if (!entry.TryGetProperty("uuid", out var idElement)
                    || !Guid.TryParse(idElement.GetString(), out var playerId)
                    || !entry.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }

                var name = nameElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && name.Length <= 64)
                {
                    names[playerId] = name;
                }
            }

            return names;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException)
        {
            return new Dictionary<Guid, string>();
        }
    }

    private static string NormalizeDimensionKey(string value, out int numericId)
    {
        var normalized = value.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "minecraft:overworld":
            case "overworld":
                numericId = 0;
                return "overworld";
            case "minecraft:the_nether":
            case "the_nether":
            case "dim-1":
                numericId = -1;
                return "DIM-1";
            case "minecraft:the_end":
            case "the_end":
            case "dim1":
                numericId = 1;
                return "DIM1";
            default:
                numericId = int.MinValue;
                var separator = normalized.IndexOf(':');
                return separator > 0
                    ? $"dimensions/{normalized[..separator]}/{normalized[(separator + 1)..]}"
                    : normalized;
        }
    }

    private static string DimensionKeyFromNumericId(int numericId) => numericId switch
    {
        0 => "overworld",
        -1 => "DIM-1",
        1 => "DIM1",
        _ => $"DIM{numericId}"
    };

    private static IReadOnlyList<RegionSnapshot> ReadRegionSnapshots(
        string regionDirectory,
        int minimumX,
        int minimumZ,
        int maximumX,
        int maximumZ,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(regionDirectory))
        {
            return [];
        }

        var snapshots = new List<RegionSnapshot>();
        foreach (var path in Directory.EnumerateFiles(regionDirectory, "r.*.*.mca", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.LinkTarget is not null
                || info.Length < RegionHeaderLength
                || !TryParseRegionCoordinates(info.Name, out var regionX, out var regionZ))
            {
                continue;
            }

            var regionMinimumX = regionX * RegionSideBlocks;
            var regionMinimumZ = regionZ * RegionSideBlocks;
            if (regionMinimumX > maximumX
                || regionMinimumX + RegionSideBlocks - 1 < minimumX
                || regionMinimumZ > maximumZ
                || regionMinimumZ + RegionSideBlocks - 1 < minimumZ)
            {
                continue;
            }

            try
            {
                var header = new byte[RegionHeaderLength];
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    RegionHeaderLength,
                    FileOptions.SequentialScan);
                ReadExactly(stream, header);
                info.Refresh();
                snapshots.Add(new RegionSnapshot(
                    path,
                    regionX,
                    regionZ,
                    header,
                    info.Length,
                    info.LastWriteTimeUtc));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                // A region being replaced is picked up by the next refresh.
            }
        }

        return snapshots
            .OrderBy(snapshot => snapshot.RegionZ)
            .ThenBy(snapshot => snapshot.RegionX)
            .ToArray();
    }

    private static string BuildFingerprint(
        WorldMapRenderRequest request,
        IReadOnlyList<RegionSnapshot> snapshots,
        int blocksPerPixel)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, RendererVersion);
        Append(hash, request.Profile.Id);
        Append(hash, request.Dimension.Id);
        Append(hash, request.CenterX.ToString());
        Append(hash, request.CenterZ.ToString());
        Append(hash, request.RadiusBlocks.ToString());
        Append(hash, blocksPerPixel.ToString());
        foreach (var snapshot in snapshots)
        {
            Append(hash, snapshot.RegionX.ToString());
            Append(hash, snapshot.RegionZ.ToString());
            hash.AppendData(snapshot.Header);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));

    private static int CountChunks(
        IEnumerable<RegionSnapshot> snapshots,
        int minimumX,
        int minimumZ,
        int maximumX,
        int maximumZ)
    {
        var count = 0;
        foreach (var snapshot in snapshots)
        {
            for (var localZ = 0; localZ < 32; localZ++)
            {
                for (var localX = 0; localX < 32; localX++)
                {
                    var chunkX = snapshot.RegionX * 32 + localX;
                    var chunkZ = snapshot.RegionZ * 32 + localZ;
                    if (ChunkIntersects(chunkX, chunkZ, minimumX, minimumZ, maximumX, maximumZ)
                        && (ReadUInt32BigEndian(snapshot.Header, (localX + localZ * 32) * 4) >> 8) != 0)
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    private static bool ChunkIntersects(
        int chunkX,
        int chunkZ,
        int minimumX,
        int minimumZ,
        int maximumX,
        int maximumZ)
    {
        var chunkMinimumX = chunkX * ChunkSideBlocks;
        var chunkMinimumZ = chunkZ * ChunkSideBlocks;
        return chunkMinimumX <= maximumX
            && chunkMinimumX + 15 >= minimumX
            && chunkMinimumZ <= maximumZ
            && chunkMinimumZ + 15 >= minimumZ;
    }

    private static void CompositeSurface(
        LegacyChunkSurface surface,
        int minimumX,
        int minimumZ,
        int maximumX,
        int maximumZ,
        int blocksPerPixel,
        int pixelWidth,
        int[] colors,
        short[] heights)
    {
        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var sourceIndex = z * 16 + x;
                var height = surface.Heights[sourceIndex];
                if (height == short.MinValue)
                {
                    continue;
                }

                var worldX = surface.ChunkX * 16 + x;
                var worldZ = surface.ChunkZ * 16 + z;
                if (worldX < minimumX || worldX > maximumX || worldZ < minimumZ || worldZ > maximumZ)
                {
                    continue;
                }

                var pixelX = (worldX - minimumX) / blocksPerPixel;
                var pixelZ = (worldZ - minimumZ) / blocksPerPixel;
                var targetIndex = pixelZ * pixelWidth + pixelX;
                if (height >= heights[targetIndex])
                {
                    heights[targetIndex] = height;
                    colors[targetIndex] = surface.Colors[sourceIndex];
                }
            }
        }
    }

    private static void FillBackground(int[] colors, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                colors[y * width + x] = ((x / 16 + y / 16) & 1) == 0 ? 0x171A1D : 0x1C2024;
            }
        }
    }

    private static void Shade(int[] colors, short[] heights, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (heights[index] == short.MinValue)
                {
                    continue;
                }

                var neighbourHeight = x > 0 && heights[index - 1] != short.MinValue
                    ? heights[index - 1]
                    : y > 0 && heights[index - width] != short.MinValue
                        ? heights[index - width]
                        : heights[index];
                var difference = Math.Clamp(heights[index] - neighbourHeight, -6, 6);
                var elevation = Math.Clamp(0.78 + heights[index] / 1024d, 0.76, 1.02);
                var shade = Math.Clamp(elevation + difference * 0.025, 0.62, 1.16);
                colors[index] = ShadeColor(colors[index], shade);
            }
        }
    }

    private static int ShadeColor(int color, double factor)
    {
        var red = Math.Clamp((int)(((color >> 16) & 0xFF) * factor), 0, 255);
        var green = Math.Clamp((int)(((color >> 8) & 0xFF) * factor), 0, 255);
        var blue = Math.Clamp((int)((color & 0xFF) * factor), 0, 255);
        return red << 16 | green << 8 | blue;
    }

    private static byte[] ToBgra(IReadOnlyList<int> colors)
    {
        var pixels = new byte[colors.Count * 4];
        for (var index = 0; index < colors.Count; index++)
        {
            var color = colors[index];
            pixels[index * 4] = (byte)(color & 0xFF);
            pixels[index * 4 + 1] = (byte)((color >> 8) & 0xFF);
            pixels[index * 4 + 2] = (byte)((color >> 16) & 0xFF);
            pixels[index * 4 + 3] = 255;
        }

        return pixels;
    }

    private static int LegacyBlockColor(int blockId, int metadata) => blockId switch
    {
        1 or 4 or 43 or 44 or 67 or 98 or 109 => 0x777B7E,
        2 => 0x5F9B45,
        3 or 60 => 0x79553A,
        5 or 17 or 53 or 54 or 58 or 85 or 107 or 125 or 126 or 134 or 135 or 136 => 0x9A6A3A,
        7 => 0x343434,
        8 or 9 => 0x3465C5,
        10 or 11 => 0xF07018,
        12 or 24 or 128 => 0xD8C786,
        13 => 0x77736F,
        14 or 41 => 0xD8B13D,
        15 or 42 => 0xBBAA98,
        16 or 173 => 0x34383A,
        18 or 161 => 0x4D7F3B,
        20 or 95 or 102 or 160 => 0xB7D6DD,
        21 or 22 => 0x3457A4,
        30 => 0xD5D5D5,
        31 or 32 or 37 or 38 or 39 or 40 or 175 => 0x709E45,
        35 => WoolColor(metadata),
        45 or 108 => 0x9E503A,
        46 => 0xC94B3D,
        47 => 0xA48753,
        48 => 0x586A50,
        49 => 0x252034,
        50 or 76 or 89 or 124 => 0xE9C85A,
        52 => 0x38516A,
        56 or 57 => 0x4FC8C5,
        73 or 74 or 152 => 0x9F3430,
        78 or 80 => 0xF0F4F5,
        79 => 0x87B7D7,
        81 => 0x3C9A43,
        82 => 0xA7B0B7,
        86 or 91 => 0xC26D28,
        87 => 0x6F2D2B,
        88 => 0x5B4638,
        90 or 119 => 0x7546A6,
        99 or 100 => 0xA89579,
        103 => 0x9BB13C,
        110 => 0x6A526B,
        112 or 113 or 114 => 0x472126,
        121 => 0xD6D29A,
        129 or 133 => 0x35B86B,
        153 => 0x7A4741,
        159 or 172 => TerracottaColor(metadata),
        _ => HashedBlockColor(blockId, metadata)
    };

    private static int WoolColor(int metadata) => (metadata & 15) switch
    {
        0 => 0xE7E7E7,
        1 => 0xD77F33,
        2 => 0xB350BC,
        3 => 0x6B8AC9,
        4 => 0xB1A627,
        5 => 0x41AE38,
        6 => 0xD08499,
        7 => 0x404040,
        8 => 0x9AA1A1,
        9 => 0x2E6E89,
        10 => 0x7E3DB5,
        11 => 0x2E388D,
        12 => 0x4F321F,
        13 => 0x35461B,
        14 => 0x963430,
        _ => 0x191919
    };

    private static int TerracottaColor(int metadata) => ShadeColor(WoolColor(metadata), 0.72);

    private static int HashedBlockColor(int blockId, int metadata)
    {
        var hash = unchecked((uint)(blockId * 1103515245 + metadata * 12345));
        var red = 70 + (int)(hash & 0x5F);
        var green = 70 + (int)((hash >> 7) & 0x5F);
        var blue = 70 + (int)((hash >> 14) & 0x5F);
        return red << 16 | green << 8 | blue;
    }

    private static int ReadNibble(IReadOnlyList<byte> values, int index) =>
        (values[index >> 1] >> ((index & 1) * 4)) & 0x0F;

    private static byte[] Decompress(byte[] source, byte compression, int maximumOutputBytes)
    {
        using var input = new MemoryStream(source, writable: false);
        using Stream decompressor = compression switch
        {
            1 => new GZipStream(input, CompressionMode.Decompress),
            2 => new ZLibStream(input, CompressionMode.Decompress),
            3 => input,
            _ => throw new InvalidDataException($"Unsupported region compression type {compression}.")
        };
        return ReadBounded(decompressor, maximumOutputBytes);
    }

    private static byte[] ReadCompressedFile(string path, int maximumOutputBytes)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.SequentialScan);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return ReadBounded(gzip, maximumOutputBytes);
    }

    private static byte[] ReadBounded(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The decompressed NBT payload exceeds the safe limit.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static string ReadLevelName(string propertiesPath)
    {
        if (!File.Exists(propertiesPath))
        {
            return "world";
        }

        var info = new FileInfo(propertiesPath);
        if (info.Length > 1024 * 1024)
        {
            throw new InvalidDataException("server.properties is too large to inspect safely.");
        }

        using var stream = new FileStream(
            propertiesPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.StartsWith('!'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0
                || !trimmed[..separator].Trim().Equals("level-name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(value)
                || Path.IsPathRooted(value)
                || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                throw new InvalidDataException("server.properties contains an invalid level-name value.");
            }

            return value;
        }

        return "world";
    }

    private static (int X, int Y, int Z) ReadSpawn(string levelPath)
    {
        if (!File.Exists(levelPath))
        {
            return (0, 64, 0);
        }

        try
        {
            var data = ReadCompressedFile(levelPath, 16 * 1024 * 1024);
            var spawnX = 0;
            var spawnY = 64;
            var spawnZ = 0;
            var reader = new MinecraftNbtReader(data);

            bool VisitData(string name, byte type, MinecraftNbtReader current, int depth)
            {
                if (name.Equals("SpawnX", StringComparison.Ordinal) && type == 3)
                {
                    spawnX = current.ReadInt32();
                    return true;
                }

                if (name.Equals("SpawnY", StringComparison.Ordinal) && type == 3)
                {
                    spawnY = current.ReadInt32();
                    return true;
                }

                if (name.Equals("SpawnZ", StringComparison.Ordinal) && type == 3)
                {
                    spawnZ = current.ReadInt32();
                    return true;
                }

                return false;
            }

            reader.ReadRootCompound((name, type, current, depth) =>
            {
                if (name.Equals("Data", StringComparison.Ordinal) && type == 10)
                {
                    current.ReadCompound(VisitData, depth + 1);
                    return true;
                }

                return VisitData(name, type, current, depth);
            });
            return (spawnX, spawnY, spawnZ);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            return (0, 64, 0);
        }
    }

    private static bool TryParseRegionCoordinates(string fileName, out int regionX, out int regionZ)
    {
        regionX = 0;
        regionZ = 0;
        var parts = fileName.Split('.');
        return parts.Length == 4
            && parts[0].Equals("r", StringComparison.OrdinalIgnoreCase)
            && parts[3].Equals("mca", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out regionX)
            && int.TryParse(parts[2], out regionZ);
    }

    private static uint ReadUInt32BigEndian(IReadOnlyList<byte> bytes, int offset) =>
        (uint)(bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3]);

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private static void CleanupOldMaps(string directory, string currentPath)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "map-*.bmp")
                         .Where(path => !path.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(4))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache cleanup must never make rendering fail.
        }
    }

    private void TrimChunkCache()
    {
        if (_chunkCache.Count <= MaximumCachedChunks)
        {
            return;
        }

        foreach (var key in _chunkCache.Keys.Take(_chunkCache.Count - MaximumCachedChunks).ToArray())
        {
            _chunkCache.Remove(key);
        }
    }

    private static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void EnsureWithinRoot(string root, string candidate)
    {
        if (!string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The configured world path is outside the selected server folder.");
        }
    }

    private static void EnsureNotLink(string path, string message)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && info.LinkTarget is not null)
        {
            throw new InvalidDataException(message);
        }
    }

    private static void EnsureContainedTreeHasNoLinks(string root, string candidate, string message)
    {
        var normalizedRoot = NormalizeDirectory(root);
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        while (true)
        {
            if (Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null)
            {
                throw new InvalidDataException(message);
            }

            if (current.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent)
                || !Path.GetFullPath(current).StartsWith(
                    normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The requested world path is outside the selected server folder.");
            }

            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(sanitized) ? "map" : sanitized;
    }

    private sealed record RegionSnapshot(
        string Path,
        int RegionX,
        int RegionZ,
        byte[] Header,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed record LegacySection(sbyte Y, byte[] Blocks, byte[]? Data, byte[]? Add);

    private sealed record LegacyChunkSurface(int ChunkX, int ChunkZ, int[] Colors, short[] Heights);

    private sealed record CachedChunk(uint Location, uint Timestamp, LegacyChunkSurface Surface);
}
