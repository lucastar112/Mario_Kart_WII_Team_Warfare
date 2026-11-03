using System.IO;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace DebugManager.Services.TrackFormats;

/// <summary>Reads LE-CODE's big-endian course.lex section chain.</summary>
public static class LexFileReader
{
    private static readonly HashSet<string> KnownSections = new(StringComparer.Ordinal)
        { "----", "FEAT", "SET1", "CANN", "CTDN", "HIPT", "RITP", "TEST", "DEV1" };

    public static LexDocument Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ReadOnlySpan<byte> bytes = data;
        if (bytes.Length < 0x10 || !bytes[..4].SequenceEqual("LE-X"u8))
            throw new InvalidDataException("Invalid LEX file: LE-X header not found.");

        ushort major = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(4, 2));
        ushort minor = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(6, 2));
        uint declaredSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4));
        uint firstSectionOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(12, 4));
        if (declaredSize < 0x10 || declaredSize > bytes.Length)
            throw new InvalidDataException($"Invalid LEX size ({declaredSize}).");
        if (firstSectionOffset < 0x10 || firstSectionOffset > declaredSize)
            throw new InvalidDataException("Invalid LEX first-section offset.");

        var sections = new List<LexSection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int position = checked((int)firstSectionOffset);
        bool terminated = false;
        while (position + 8 <= declaredSize)
        {
            string magic = Encoding.ASCII.GetString(bytes.Slice(position, 4));
            uint sectionSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position + 4, 4));
            if (magic == "\0\0\0\0" && sectionSize == 0)
            {
                terminated = true;
                position += 8;
                break;
            }
            if (sectionSize > int.MaxValue || (long)position + 8 + sectionSize > declaredSize)
                throw new InvalidDataException($"LEX section '{magic}' exceeds the file boundary.");
            if (!seen.Add(magic))
                throw new InvalidDataException($"Duplicate LEX section '{magic}'.");

            byte[] payload = bytes.Slice(position + 8, (int)sectionSize).ToArray();
            sections.Add(new LexSection(magic, payload, KnownSections.Contains(magic), position));
            long next = (long)position + 8 + sectionSize;
            long aligned = (next + 3) & ~3L;
            if (aligned > declaredSize) throw new InvalidDataException("LEX alignment exceeds file boundary.");
            position = checked((int)aligned);
        }
        if (!terminated)
            throw new InvalidDataException("LEX section chain has no terminator.");

        return new LexDocument(major, minor, declaredSize,
            new ReadOnlyCollection<LexSection>(sections));
    }
}

public sealed record LexDocument(ushort MajorVersion, ushort MinorVersion, uint FileLength, IReadOnlyList<LexSection> Sections)
{
    public LexSection? FindSection(string magic) => Sections.FirstOrDefault(s => s.Magic == magic);
}

/// <summary>Unknown sections are deliberately retained instead of discarded.</summary>
public sealed record LexSection(string Magic, byte[] RawPayload, bool IsKnown, int FileOffset);
