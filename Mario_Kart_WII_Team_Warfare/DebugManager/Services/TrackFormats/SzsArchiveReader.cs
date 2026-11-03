using System.IO;
using System.Buffers.Binary;
using System.Text;

namespace DebugManager.Services.TrackFormats;

/// <summary>Minimal read-only Yaz0 + Nintendo U8 reader, sufficient to load course.kmp/course.lex from .szs.</summary>
public sealed class SzsArchiveReader
{
    private readonly byte[] _u8;
    private readonly Dictionary<string, (int Offset, int Length)> _files;

    private SzsArchiveReader(byte[] u8)
    {
        _u8 = u8;
        _files = ParseU8(u8);
    }

    public static SzsArchiveReader Open(byte[] szsData)
    {
        ArgumentNullException.ThrowIfNull(szsData);
        byte[] archive = szsData.AsSpan().StartsWith("Yaz0"u8)
            ? DecompressYaz0(szsData)
            : szsData.ToArray();
        return new SzsArchiveReader(archive);
    }

    public IReadOnlyCollection<string> FileNames => _files.Keys;
    public bool ContainsFile(string path) => _files.ContainsKey(Normalize(path));

    public byte[] ReadFile(string path)
    {
        string key = Normalize(path);
        if (!_files.TryGetValue(key, out var entry)) throw new FileNotFoundException($"'{path}' was not found in the SZS archive.");
        return _u8.AsSpan(entry.Offset, entry.Length).ToArray();
    }

    public static byte[] DecompressYaz0(byte[] source)
    {
        if (source.Length < 16 || !source.AsSpan(0, 4).SequenceEqual("Yaz0"u8))
            throw new InvalidDataException("Invalid Yaz0 header.");
        uint outputLength = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(4, 4));
        if (outputLength > int.MaxValue) throw new InvalidDataException("Yaz0 output is too large.");
        byte[] output = new byte[(int)outputLength];
        int input = 16, written = 0;
        while (written < output.Length)
        {
            if (input >= source.Length) throw new InvalidDataException("Unexpected end of Yaz0 stream.");
            byte code = source[input++];
            for (int bit = 7; bit >= 0 && written < output.Length; bit--)
            {
                if ((code & (1 << bit)) != 0)
                {
                    if (input >= source.Length) throw new InvalidDataException("Unexpected end of Yaz0 literal.");
                    output[written++] = source[input++];
                    continue;
                }
                if (input + 2 > source.Length) throw new InvalidDataException("Unexpected end of Yaz0 back-reference.");
                byte first = source[input++], second = source[input++];
                int distance = ((first & 0x0F) << 8) | second;
                int count = first >> 4;
                if (count == 0)
                {
                    if (input >= source.Length) throw new InvalidDataException("Unexpected end of Yaz0 length.");
                    count = source[input++] + 0x12;
                }
                else count += 2;
                int copyFrom = written - distance - 1;
                if (copyFrom < 0 || written + count > output.Length)
                    throw new InvalidDataException("Invalid Yaz0 back-reference.");
                for (int i = 0; i < count; i++) output[written++] = output[copyFrom++];
            }
        }
        return output;
    }

    private static Dictionary<string, (int Offset, int Length)> ParseU8(byte[] data)
    {
        ReadOnlySpan<byte> b = data;
        if (b.Length < 0x20 || BinaryPrimitives.ReadUInt32BigEndian(b[..4]) != 0x55AA382D)
            throw new InvalidDataException("Invalid U8 archive header.");
        uint rootOffset = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4, 4));
        uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(12, 4));
        if (rootOffset + 12 > b.Length || dataOffset > b.Length) throw new InvalidDataException("Invalid U8 offsets.");
        uint rootNode = BinaryPrimitives.ReadUInt32BigEndian(b.Slice((int)rootOffset, 4));
        if ((rootNode >> 24) != 1) throw new InvalidDataException("U8 root node is not a directory.");
        uint nodeCount = BinaryPrimitives.ReadUInt32BigEndian(b.Slice((int)rootOffset + 8, 4));
        if (nodeCount == 0 || (long)rootOffset + nodeCount * 12L > b.Length) throw new InvalidDataException("Invalid U8 node table.");
        int stringTable = checked((int)(rootOffset + nodeCount * 12));
        var result = new Dictionary<string, (int Offset, int Length)>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<(uint EndIndex, string Path)>();
        directories.Push((nodeCount, string.Empty));

        for (uint index = 1; index < nodeCount; index++)
        {
            int nodeOffset = checked((int)(rootOffset + index * 12));
            uint typeAndName = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(nodeOffset, 4));
            uint nameOffset = typeAndName & 0x00FFFFFF;
            while (directories.Count > 0 && index >= directories.Peek().EndIndex) directories.Pop();
            string parent = directories.Count > 0 ? directories.Peek().Path : string.Empty;
            long namePosition = (long)stringTable + nameOffset;
            if (namePosition < stringTable || namePosition >= b.Length) throw new InvalidDataException("Invalid U8 name offset.");
            int nameStart = (int)namePosition;
            int nameEnd = Array.IndexOf(data, (byte)0, nameStart);
            if (nameEnd < 0) throw new InvalidDataException("Unterminated U8 filename.");
            string name = Encoding.UTF8.GetString(b.Slice(nameStart, nameEnd - nameStart));
            string path = name == "." ? parent : string.IsNullOrEmpty(parent) ? name : parent + "/" + name;

            if ((typeAndName >> 24) == 1)
            {
                uint endIndex = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(nodeOffset + 8, 4));
                if (endIndex <= index || endIndex > nodeCount) throw new InvalidDataException("Invalid U8 directory range.");
                directories.Push((endIndex, path));
            }
            else
            {
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(nodeOffset + 4, 4));
                uint length = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(nodeOffset + 8, 4));
                if ((long)offset + length > b.Length || offset < dataOffset)
                    throw new InvalidDataException($"U8 file '{path}' exceeds archive bounds.");
                result[path] = (checked((int)offset), checked((int)length));
            }
        }
        return result;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
