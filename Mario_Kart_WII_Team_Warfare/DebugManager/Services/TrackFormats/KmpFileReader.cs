using System.IO;
using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace DebugManager.Services.TrackFormats;

/// <summary>Reads the big-endian Mario Kart Wii RKMD/KMP container.</summary>
public static class KmpFileReader
{
    private static readonly IReadOnlyDictionary<string, int> FixedEntrySizes =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // Fixed-size KMP records. ENPT and CKPT are 0x14 bytes (not 0x1C / 0x0C).
            ["KTPT"] = 0x1C, ["ENPT"] = 0x14, ["ENPH"] = 0x10,
            ["ITPT"] = 0x14, ["ITPH"] = 0x10, ["CKPT"] = 0x14,
            ["CKPH"] = 0x10, ["GOBJ"] = 0x3C, ["AREA"] = 0x30,
            ["CAME"] = 0x48, ["JGPT"] = 0x1C, ["CNPT"] = 0x1C,
            ["MSPT"] = 0x1C, ["STGI"] = 0x0C
        };

    public static KmpDocument Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ReadOnlySpan<byte> bytes = data;
        if (bytes.Length < 0x10 || !bytes[..4].SequenceEqual("RKMD"u8))
            throw new InvalidDataException("Invalid KMP file: RKMD header not found.");

        uint declaredLength = ReadU32(bytes, 4);
        ushort sectionCount = ReadU16(bytes, 8);
        ushort headerLength = ReadU16(bytes, 10);
        uint version = ReadU32(bytes, 12);

        if (declaredLength > bytes.Length || declaredLength < 0x10)
            throw new InvalidDataException($"Invalid KMP length ({declaredLength}).");
        if (sectionCount == 0 || headerLength < 0x10 || headerLength > declaredLength ||
            0x10L + sectionCount * 4L > headerLength)
            throw new InvalidDataException("Invalid KMP section table.");

        var offsets = new uint[sectionCount];
        for (int i = 0; i < sectionCount; i++) offsets[i] = ReadU32(bytes, 0x10 + i * 4);
        var sections = new List<KmpSection>(sectionCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < sectionCount; i++)
        {
            uint relativeOffset = offsets[i];
            long absoluteOffsetLong = (long)headerLength + relativeOffset;
            if (absoluteOffsetLong < headerLength || absoluteOffsetLong + 8 > declaredLength)
                throw new InvalidDataException($"KMP section {i} points outside the file.");
            int offset = checked((int)absoluteOffsetLong);
            string magic = System.Text.Encoding.ASCII.GetString(bytes.Slice(offset, 4));
            ushort entryCount = ReadU16(bytes, offset + 4);
            ushort setting = ReadU16(bytes, offset + 6);
            int payloadOffset = offset + 8;
            uint nextRelativeOffset = offsets.Where(value => value > relativeOffset).DefaultIfEmpty(declaredLength - headerLength).Min();
            int nextOffset = checked((int)(headerLength + nextRelativeOffset));
            if (nextOffset < payloadOffset || nextOffset > declaredLength)
                throw new InvalidDataException($"Invalid boundary for KMP section {magic}.");
            if (!seen.Add(magic))
                throw new InvalidDataException($"Duplicate KMP section '{magic}'.");

            byte[] payload = bytes.Slice(payloadOffset, nextOffset - payloadOffset).ToArray();
            int? entrySize = FixedEntrySizes.TryGetValue(magic, out int knownSize) ? knownSize : null;
            IReadOnlyList<byte[]> entries = Array.Empty<byte[]>();
            if (entrySize.HasValue)
            {
                long expected = (long)entryCount * entrySize.Value;
                if (expected > payload.Length)
                    throw new InvalidDataException($"Section {magic}: {entryCount} entries require {expected} bytes, only {payload.Length} available.");
                var parsed = new List<byte[]>(entryCount);
                for (int entry = 0; entry < entryCount; entry++)
                    parsed.Add(payload.AsSpan(entry * entrySize.Value, entrySize.Value).ToArray());
                entries = new ReadOnlyCollection<byte[]>(parsed);
            }
            sections.Add(new KmpSection(magic, entryCount, setting, offset, entrySize, payload, entries));
        }

        return new KmpDocument(declaredLength, headerLength, version,
            new ReadOnlyCollection<KmpSection>(sections));
    }

    private static ushort ReadU16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.Slice(o, 2));
    private static uint ReadU32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o, 4));
}

public sealed record KmpDocument(uint FileLength, ushort HeaderLength, uint Version, IReadOnlyList<KmpSection> Sections)
{
    public KmpSection? FindSection(string magic) => Sections.FirstOrDefault(s => s.Magic == magic);
}

/// <summary>Payload remains available even for variable/undocumented section layouts.</summary>
public sealed record KmpSection(
    string Magic, ushort EntryCount, ushort Setting, int FileOffset,
    int? FixedEntrySize, byte[] RawPayload, IReadOnlyList<byte[]> Entries);

public static class KmpEntryDecoder
{
    public static Vector3 ReadVector3(byte[] entry, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (offset < 0 || offset + 12 > entry.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return new Vector3(ReadFloat(entry, offset), ReadFloat(entry, offset + 4), ReadFloat(entry, offset + 8));
    }

    public static float ReadFloat(byte[] entry, int offset)
    {
        if (offset < 0 || offset + 4 > entry.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        int bits = BinaryPrimitives.ReadInt32BigEndian(entry.AsSpan(offset, 4));
        return BitConverter.Int32BitsToSingle(bits);
    }

    public static short ReadInt16(byte[] entry, int offset) => BinaryPrimitives.ReadInt16BigEndian(entry.AsSpan(offset, 2));
    public static ushort ReadUInt16(byte[] entry, int offset) => BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(offset, 2));
}

public readonly record struct Vector3(float X, float Y, float Z);
