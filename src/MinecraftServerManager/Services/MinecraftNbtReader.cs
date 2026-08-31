using System.Buffers.Binary;
using System.Text;

namespace MinecraftServerManager.Services;

internal sealed class MinecraftNbtReader
{
    private const int MaximumDepth = 64;
    private const int MaximumCollectionLength = 16 * 1024 * 1024;
    private const int MaximumStringBytes = 65_535;

    private readonly byte[] _data;
    private int _position;

    public MinecraftNbtReader(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
    }

    public int Remaining => _data.Length - _position;

    public void ReadRootCompound(Func<string, byte, MinecraftNbtReader, int, bool> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        var type = ReadByte();
        if (type != 10)
        {
            throw new InvalidDataException("The NBT root tag is not a compound.");
        }

        _ = ReadString();
        ReadCompound(visitor, 1);
    }

    public void ReadCompound(
        Func<string, byte, MinecraftNbtReader, int, bool> visitor,
        int depth)
    {
        EnsureDepth(depth);
        while (true)
        {
            var type = ReadByte();
            if (type == 0)
            {
                return;
            }

            if (type > 12)
            {
                throw new InvalidDataException($"Unsupported NBT tag type {type}.");
            }

            var name = ReadString();
            if (!visitor(name, type, this, depth))
            {
                SkipPayload(type, depth + 1);
            }
        }
    }

    public (byte ElementType, int Count) ReadListHeader()
    {
        var elementType = ReadByte();
        if (elementType > 12)
        {
            throw new InvalidDataException($"Unsupported NBT list element type {elementType}.");
        }

        var count = ReadLength();
        if (elementType == 0 && count != 0)
        {
            throw new InvalidDataException("An NBT list with TAG_End elements must be empty.");
        }

        return (elementType, count);
    }

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _data[_position++];
    }

    public sbyte ReadSignedByte() => unchecked((sbyte)ReadByte());

    public short ReadInt16()
    {
        EnsureAvailable(2);
        var value = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(_position, 2));
        _position += 2;
        return value;
    }

    public int ReadInt32()
    {
        EnsureAvailable(4);
        var value = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    public long ReadInt64()
    {
        EnsureAvailable(8);
        var value = BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());

    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

    public string ReadString()
    {
        var length = unchecked((ushort)ReadInt16());
        if (length > MaximumStringBytes)
        {
            throw new InvalidDataException("An NBT string is too large.");
        }

        EnsureAvailable(length);
        var value = Encoding.UTF8.GetString(_data, _position, length);
        _position += length;
        return value;
    }

    public byte[] ReadByteArray(int maximumLength = MaximumCollectionLength)
    {
        var length = ReadLength(maximumLength);
        EnsureAvailable(length);
        var value = _data.AsSpan(_position, length).ToArray();
        _position += length;
        return value;
    }

    public int[] ReadIntArray(int maximumLength = MaximumCollectionLength / 4)
    {
        var length = ReadLength(maximumLength);
        var values = new int[length];
        for (var index = 0; index < length; index++)
        {
            values[index] = ReadInt32();
        }

        return values;
    }

    public long[] ReadLongArray(int maximumLength = MaximumCollectionLength / 8)
    {
        var length = ReadLength(maximumLength);
        var values = new long[length];
        for (var index = 0; index < length; index++)
        {
            values[index] = ReadInt64();
        }

        return values;
    }

    public void SkipPayload(byte type, int depth)
    {
        EnsureDepth(depth);
        switch (type)
        {
            case 0:
                return;
            case 1:
                Skip(1);
                return;
            case 2:
                Skip(2);
                return;
            case 3:
            case 5:
                Skip(4);
                return;
            case 4:
            case 6:
                Skip(8);
                return;
            case 7:
                Skip(ReadLength());
                return;
            case 8:
                _ = ReadString();
                return;
            case 9:
            {
                var (elementType, count) = ReadListHeader();
                for (var index = 0; index < count; index++)
                {
                    SkipPayload(elementType, depth + 1);
                }

                return;
            }
            case 10:
                ReadCompound(static (_, _, _, _) => false, depth + 1);
                return;
            case 11:
            {
                var length = ReadLength(MaximumCollectionLength / 4);
                Skip(checked(length * 4));
                return;
            }
            case 12:
            {
                var length = ReadLength(MaximumCollectionLength / 8);
                Skip(checked(length * 8));
                return;
            }
            default:
                throw new InvalidDataException($"Unsupported NBT tag type {type}.");
        }
    }

    private int ReadLength(int maximum = MaximumCollectionLength)
    {
        var length = ReadInt32();
        if (length < 0 || length > maximum)
        {
            throw new InvalidDataException($"NBT collection length {length:N0} is outside the safe limit.");
        }

        return length;
    }

    private void Skip(int count)
    {
        EnsureAvailable(count);
        _position += count;
    }

    private void EnsureAvailable(int count)
    {
        if (count < 0 || _position > _data.Length - count)
        {
            throw new EndOfStreamException("The NBT payload ended unexpectedly.");
        }
    }

    private static void EnsureDepth(int depth)
    {
        if (depth > MaximumDepth)
        {
            throw new InvalidDataException("The NBT nesting depth exceeds the safe limit.");
        }
    }
}
