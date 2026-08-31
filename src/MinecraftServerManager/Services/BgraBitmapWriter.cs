using System.Buffers.Binary;

namespace MinecraftServerManager.Services;

internal static class BgraBitmapWriter
{
    public static async Task WriteAsync(
        string path,
        int width,
        int height,
        ReadOnlyMemory<byte> bgraPixels,
        CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var expectedLength = checked(width * height * 4);
        if (bgraPixels.Length != expectedLength)
        {
            throw new ArgumentException("The BGRA buffer does not match the bitmap dimensions.", nameof(bgraPixels));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var header = new byte[54];
            header[0] = (byte)'B';
            header[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2, 4), checked(54 + expectedLength));
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10, 4), 54);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22, 4), -height);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(26, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(28, 2), 32);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(34, 4), expectedLength);

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(bgraPixels, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
