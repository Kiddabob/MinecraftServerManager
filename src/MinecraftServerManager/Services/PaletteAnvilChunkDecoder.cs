namespace MinecraftServerManager.Services;

internal static class PaletteAnvilChunkDecoder
{
    private const int FlatteningDataVersion = 1_519;
    private const int PaddedBlockStatesDataVersion = 2_529;
    private const int MaximumSections = 64;
    private const int MaximumPaletteEntries = 4_096;
    private const int MaximumBlockStateLongs = 4_096;

    public static PaletteChunkSurface? TryDecode(
        byte[] nbt,
        int surfaceMaximumY)
    {
        ArgumentNullException.ThrowIfNull(nbt);
        var dataVersion = ReadDataVersion(nbt);
        var sections = ReadSections(nbt);
        if (sections.Count == 0
            || (dataVersion is > 0 and < FlatteningDataVersion
                && sections.All(section => section.Palette.Count == 0)))
        {
            return null;
        }

        var colors = new int[256];
        var heights = new short[256];
        Array.Fill(heights, short.MinValue);
        foreach (var section in sections.OrderByDescending(section => section.Y))
        {
            if (section.Palette.Count == 0)
            {
                continue;
            }

            var sectionBaseY = section.Y * 16;
            if (sectionBaseY > surfaceMaximumY)
            {
                continue;
            }

            var maximumLocalY = Math.Min(15, surfaceMaximumY - sectionBaseY);
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var columnIndex = z * 16 + x;
                    if (heights[columnIndex] != short.MinValue)
                    {
                        continue;
                    }

                    for (var y = maximumLocalY; y >= 0; y--)
                    {
                        var blockIndex = y * 256 + z * 16 + x;
                        var paletteIndex = ReadPaletteIndex(
                            section,
                            blockIndex,
                            dataVersion);
                        if (paletteIndex < 0 || paletteIndex >= section.Palette.Count)
                        {
                            continue;
                        }

                        var blockName = section.Palette[paletteIndex];
                        if (IsAir(blockName))
                        {
                            continue;
                        }

                        colors[columnIndex] = BlockColor(blockName);
                        heights[columnIndex] = checked((short)(sectionBaseY + y));
                        break;
                    }
                }
            }
        }

        return heights.All(height => height == short.MinValue)
            ? null
            : new PaletteChunkSurface(colors, heights, dataVersion);
    }

    private static int ReadDataVersion(byte[] nbt)
    {
        var dataVersion = 0;
        var reader = new MinecraftNbtReader(nbt);
        reader.ReadRootCompound((name, type, current, _) =>
        {
            if (name.Equals("DataVersion", StringComparison.Ordinal) && type == 3)
            {
                dataVersion = current.ReadInt32();
                return true;
            }

            return false;
        });
        return dataVersion;
    }

    private static IReadOnlyList<PaletteSection> ReadSections(byte[] nbt)
    {
        var sections = new List<PaletteSection>();
        var reader = new MinecraftNbtReader(nbt);

        bool VisitChunk(string name, byte type, MinecraftNbtReader current, int depth)
        {
            if ((name.Equals("Sections", StringComparison.Ordinal)
                    || name.Equals("sections", StringComparison.Ordinal))
                && type == 9)
            {
                ReadSectionList(current, depth + 1, sections);
                return true;
            }

            return false;
        }

        reader.ReadRootCompound((name, type, current, depth) =>
        {
            if (name.Equals("Level", StringComparison.Ordinal) && type == 10)
            {
                current.ReadCompound(VisitChunk, depth + 1);
                return true;
            }

            return VisitChunk(name, type, current, depth);
        });
        return sections;
    }

    private static void ReadSectionList(
        MinecraftNbtReader reader,
        int depth,
        ICollection<PaletteSection> target)
    {
        var (elementType, count) = reader.ReadListHeader();
        if (elementType != 10 || count > MaximumSections)
        {
            for (var index = 0; index < count; index++)
            {
                reader.SkipPayload(elementType, depth + 1);
            }

            return;
        }

        for (var index = 0; index < count; index++)
        {
            var sectionY = int.MinValue;
            IReadOnlyList<string> palette = [];
            long[]? blockStates = null;
            reader.ReadCompound((name, type, current, sectionDepth) =>
            {
                if (name.Equals("Y", StringComparison.Ordinal) && type == 1)
                {
                    sectionY = current.ReadSignedByte();
                    return true;
                }

                if (name.Equals("Y", StringComparison.Ordinal) && type == 3)
                {
                    sectionY = current.ReadInt32();
                    return true;
                }

                if (name.Equals("Palette", StringComparison.Ordinal) && type == 9)
                {
                    palette = ReadPalette(current, sectionDepth + 1);
                    return true;
                }

                if (name.Equals("BlockStates", StringComparison.Ordinal) && type == 12)
                {
                    blockStates = current.ReadLongArray(MaximumBlockStateLongs);
                    return true;
                }

                if (name.Equals("block_states", StringComparison.Ordinal) && type == 10)
                {
                    current.ReadCompound((stateName, stateType, stateReader, stateDepth) =>
                    {
                        if (stateName.Equals("palette", StringComparison.Ordinal) && stateType == 9)
                        {
                            palette = ReadPalette(stateReader, stateDepth + 1);
                            return true;
                        }

                        if (stateName.Equals("data", StringComparison.Ordinal) && stateType == 12)
                        {
                            blockStates = stateReader.ReadLongArray(MaximumBlockStateLongs);
                            return true;
                        }

                        return false;
                    }, sectionDepth + 1);
                    return true;
                }

                return false;
            }, depth + 1);

            if (sectionY is >= sbyte.MinValue and <= sbyte.MaxValue && palette.Count > 0)
            {
                target.Add(new PaletteSection((sbyte)sectionY, palette, blockStates));
            }
        }
    }

    private static IReadOnlyList<string> ReadPalette(MinecraftNbtReader reader, int depth)
    {
        var (elementType, count) = reader.ReadListHeader();
        if (elementType != 10 || count > MaximumPaletteEntries)
        {
            for (var index = 0; index < count; index++)
            {
                reader.SkipPayload(elementType, depth + 1);
            }

            return [];
        }

        var palette = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var blockName = string.Empty;
            reader.ReadCompound((name, type, current, _) =>
            {
                if (name.Equals("Name", StringComparison.Ordinal) && type == 8)
                {
                    blockName = current.ReadString();
                    return true;
                }

                return false;
            }, depth + 1);
            palette.Add(string.IsNullOrWhiteSpace(blockName) ? "minecraft:air" : blockName);
        }

        return palette;
    }

    private static int ReadPaletteIndex(PaletteSection section, int blockIndex, int dataVersion)
    {
        if (section.Palette.Count == 1)
        {
            return 0;
        }

        var data = section.BlockStates;
        if (data is null || data.Length == 0)
        {
            return -1;
        }

        var bitsPerBlock = Math.Max(4, CeilingLog2(section.Palette.Count));
        if (bitsPerBlock > 16)
        {
            return -1;
        }

        var mask = (1UL << bitsPerBlock) - 1UL;
        var valuesPerPaddedLong = 64 / bitsPerBlock;
        var compactLength = (4_096 * bitsPerBlock + 63) / 64;
        var paddedLength = (4_096 + valuesPerPaddedLong - 1) / valuesPerPaddedLong;
        var usePadded = dataVersion >= PaddedBlockStatesDataVersion;
        if (usePadded && data.Length < paddedLength && data.Length >= compactLength)
        {
            usePadded = false;
        }
        else if (!usePadded && data.Length < compactLength && data.Length >= paddedLength)
        {
            usePadded = true;
        }

        if (usePadded)
        {
            var longIndex = blockIndex / valuesPerPaddedLong;
            var bitOffset = blockIndex % valuesPerPaddedLong * bitsPerBlock;
            return longIndex < data.Length
                ? (int)(unchecked((ulong)data[longIndex]) >> bitOffset & mask)
                : -1;
        }

        var absoluteBit = blockIndex * bitsPerBlock;
        var compactLongIndex = absoluteBit / 64;
        var compactBitOffset = absoluteBit & 63;
        if (compactLongIndex >= data.Length)
        {
            return -1;
        }

        var value = unchecked((ulong)data[compactLongIndex]) >> compactBitOffset;
        var bitsInFirstLong = 64 - compactBitOffset;
        if (bitsInFirstLong < bitsPerBlock)
        {
            if (++compactLongIndex >= data.Length)
            {
                return -1;
            }

            value |= unchecked((ulong)data[compactLongIndex]) << bitsInFirstLong;
        }

        return (int)(value & mask);
    }

    private static int CeilingLog2(int value)
    {
        var bits = 0;
        var remaining = value - 1;
        while (remaining > 0)
        {
            remaining >>= 1;
            bits++;
        }

        return bits;
    }

    private static bool IsAir(string blockName)
    {
        var path = BlockPath(blockName);
        return path is "air" or "cave_air" or "void_air";
    }

    private static int BlockColor(string blockName)
    {
        var path = BlockPath(blockName);
        if (TryColoredBlock(path, out var dyedColor))
        {
            return dyedColor;
        }

        if (path.Contains("water", StringComparison.Ordinal)
            || path is "bubble_column")
        {
            return 0x356FC0;
        }

        if (path.Contains("lava", StringComparison.Ordinal)
            || path.Contains("magma", StringComparison.Ordinal)
            || path.Contains("fire", StringComparison.Ordinal))
        {
            return 0xEA6B19;
        }

        if (path.Contains("grass", StringComparison.Ordinal)
            || path.Contains("leaves", StringComparison.Ordinal)
            || path.Contains("moss", StringComparison.Ordinal)
            || path.Contains("fern", StringComparison.Ordinal)
            || path.Contains("vine", StringComparison.Ordinal))
        {
            return 0x5E9648;
        }

        if (path.Contains("snow", StringComparison.Ordinal)
            || path.Contains("ice", StringComparison.Ordinal)
            || path.Contains("quartz", StringComparison.Ordinal)
            || path is "calcite")
        {
            return path.Contains("ice", StringComparison.Ordinal) ? 0x92C5DF : 0xE5E8E7;
        }

        if (path.Contains("sand", StringComparison.Ordinal)
            || path.Contains("end_stone", StringComparison.Ordinal))
        {
            return path.Contains("red_sand", StringComparison.Ordinal) ? 0xB96837 : 0xD6C486;
        }

        if (path.Contains("dirt", StringComparison.Ordinal)
            || path.Contains("mud", StringComparison.Ordinal)
            || path.Contains("podzol", StringComparison.Ordinal)
            || path.Contains("farmland", StringComparison.Ordinal))
        {
            return 0x79553A;
        }

        if (path.Contains("log", StringComparison.Ordinal)
            || path.Contains("wood", StringComparison.Ordinal)
            || path.Contains("planks", StringComparison.Ordinal)
            || path.Contains("stem", StringComparison.Ordinal)
            || path.Contains("hyphae", StringComparison.Ordinal))
        {
            return 0x93663B;
        }

        if (path.Contains("deepslate", StringComparison.Ordinal)
            || path.Contains("blackstone", StringComparison.Ordinal)
            || path.Contains("bedrock", StringComparison.Ordinal)
            || path.Contains("obsidian", StringComparison.Ordinal))
        {
            return 0x38383D;
        }

        if (path.Contains("stone", StringComparison.Ordinal)
            || path.Contains("cobble", StringComparison.Ordinal)
            || path.Contains("andesite", StringComparison.Ordinal)
            || path.Contains("diorite", StringComparison.Ordinal)
            || path.Contains("granite", StringComparison.Ordinal)
            || path.Contains("ore", StringComparison.Ordinal))
        {
            return 0x777B7E;
        }

        if (path.Contains("netherrack", StringComparison.Ordinal)
            || path.Contains("nether_wart", StringComparison.Ordinal))
        {
            return 0x6F3030;
        }

        if (path.Contains("soul_sand", StringComparison.Ordinal)
            || path.Contains("soul_soil", StringComparison.Ordinal))
        {
            return 0x5B4638;
        }

        return HashedBlockColor(blockName);
    }

    private static bool TryColoredBlock(string path, out int color)
    {
        color = 0;
        if (!(path.Contains("wool", StringComparison.Ordinal)
                || path.Contains("concrete", StringComparison.Ordinal)
                || path.Contains("terracotta", StringComparison.Ordinal)
                || path.Contains("stained_glass", StringComparison.Ordinal)
                || path.Contains("shulker_box", StringComparison.Ordinal)))
        {
            return false;
        }

        color = path.Split('_')[0] switch
        {
            "white" => 0xE7E7E7,
            "orange" => 0xD77F33,
            "magenta" => 0xB350BC,
            "light" when path.StartsWith("light_blue_", StringComparison.Ordinal) => 0x6B8AC9,
            "yellow" => 0xB1A627,
            "lime" => 0x41AE38,
            "pink" => 0xD08499,
            "gray" => 0x404040,
            "cyan" => 0x2E6E89,
            "purple" => 0x7E3DB5,
            "blue" => 0x2E388D,
            "brown" => 0x4F321F,
            "green" => 0x35461B,
            "red" => 0x963430,
            "black" => 0x191919,
            _ when path.StartsWith("light_gray_", StringComparison.Ordinal) => 0x9AA1A1,
            _ => 0
        };
        return color != 0;
    }

    private static string BlockPath(string blockName)
    {
        var separator = blockName.IndexOf(':');
        return (separator >= 0 ? blockName[(separator + 1)..] : blockName).ToLowerInvariant();
    }

    private static int HashedBlockColor(string blockName)
    {
        var hash = 2_166_136_261u;
        foreach (var character in blockName)
        {
            hash ^= character;
            hash *= 16_777_619u;
        }

        var red = 70 + (int)(hash & 0x5F);
        var green = 70 + (int)((hash >> 8) & 0x5F);
        var blue = 70 + (int)((hash >> 16) & 0x5F);
        return red << 16 | green << 8 | blue;
    }

    private sealed record PaletteSection(
        sbyte Y,
        IReadOnlyList<string> Palette,
        long[]? BlockStates);
}

internal sealed record PaletteChunkSurface(
    int[] Colors,
    short[] Heights,
    int DataVersion);
