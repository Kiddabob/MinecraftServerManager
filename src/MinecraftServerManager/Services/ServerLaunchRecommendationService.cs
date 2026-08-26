using MinecraftServerManager.Models;
using System.Runtime.InteropServices;

namespace MinecraftServerManager.Services;

public sealed class ServerLaunchRecommendationService : IServerLaunchRecommendationService
{
    private readonly IJavaRuntimeService _javaRuntimeService;

    public ServerLaunchRecommendationService(IJavaRuntimeService javaRuntimeService)
    {
        _javaRuntimeService = javaRuntimeService;
    }

    public ServerLaunchRecommendation Recommend(
        string serverDirectory,
        string serverType,
        string minecraftVersion,
        int? detectedJavaMajorVersion = null)
    {
        var totalMemoryBytes = GetTotalPhysicalMemoryBytes();
        if (totalMemoryBytes <= 0)
        {
            totalMemoryBytes = 8L * 1024 * 1024 * 1024;
        }

        return Create(
            serverDirectory,
            serverType,
            minecraftVersion,
            totalMemoryBytes,
            Environment.ProcessorCount,
            _javaRuntimeService.GetRecommendedJavaMajor(minecraftVersion)
                ?? detectedJavaMajorVersion);
    }

    private static long GetTotalPhysicalMemoryBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhysicalMemory <= long.MaxValue)
            {
                return (long)status.TotalPhysicalMemory;
            }
        }

        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    internal static ServerLaunchRecommendation Create(
        string serverDirectory,
        string serverType,
        string minecraftVersion,
        long totalMemoryBytes,
        int logicalProcessorCount,
        int? javaMajorVersion)
    {
        var modCount = CountJarFiles(Path.Combine(serverDirectory, "mods"));
        var pluginCount = CountJarFiles(Path.Combine(serverDirectory, "plugins"));
        var totalMemoryMb = Math.Max(1024, totalMemoryBytes / (1024 * 1024));

        var isModded = serverType.Contains("Forge", StringComparison.OrdinalIgnoreCase)
            || serverType.Contains("Fabric", StringComparison.OrdinalIgnoreCase)
            || serverType.Contains("Quilt", StringComparison.OrdinalIgnoreCase)
            || serverType.Contains("Hybrid", StringComparison.OrdinalIgnoreCase)
            || modCount > 0;
        var requestedMaximum = isModded ? 4096 : 3072;
        requestedMaximum += modCount switch
        {
            > 150 => 6144,
            > 75 => 4096,
            > 25 => 2048,
            > 0 => 1024,
            _ => 0
        };
        requestedMaximum += pluginCount switch
        {
            > 75 => 2048,
            > 25 => 1024,
            > 0 => 512,
            _ => 0
        };

        var reserveMemory = Math.Max(2048L, Math.Min(6144L, totalMemoryMb / 4));
        var safeMaximum = Math.Max(1024L, totalMemoryMb - reserveMemory);
        var maximum = RoundDownTo512((int)Math.Min(requestedMaximum, safeMaximum));
        maximum = Math.Max(1024, maximum);
        var initial = maximum >= 4096 ? 2048 : 1024;

        return new ServerLaunchRecommendation(
            initial,
            maximum,
            javaMajorVersion,
            modCount,
            pluginCount,
            totalMemoryMb,
            Math.Max(1, logicalProcessorCount));
    }

    private static int CountJarFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return 0;
        }
    }

    private static int RoundDownTo512(int value) => Math.Max(512, value / 512 * 512);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtualMemory;
        public ulong AvailableVirtualMemory;
        public ulong AvailableExtendedVirtualMemory;
    }
}
