using System.Buffers.Binary;
using System.IO;

namespace DebugManager.Services.TrackFormats;

/// <summary>
/// Validates the BRRES container and inspects the MDL0 v11 section index groups.
/// This is a metadata reader; it does not yet decode GX display lists into triangles.
/// </summary>
public static class BrresModelReader
{
    private static readonly string[] SectionNames =
    [
        "Draw lists", "Bones", "Vertices", "Normals", "Colors",
        "Texture coordinates", "Section 6", "Section 7", "Materials",
        "TEV data", "Polygon objects", "Texture links", "Section 12", "Section 13"
    ];

    public sealed record ModelSummary(
        int FileLength,
        int ModelOffset,
        int ModelLength,
        uint Version,
        IReadOnlyDictionary<string, int> SectionCounts)
    {
        public override string ToString()
        {
            int vertices = SectionCounts.GetValueOrDefault("Vertices");
            int polygons = SectionCounts.GetValueOrDefault("Polygon objects");
            int materials = SectionCounts.GetValueOrDefault("Materials");
            return $"BRRES · MDL0 v{Version} · {vertices:N0} vertex groups · {polygons} polygon objects · {materials} materials";
        }
    }

    public static ModelSummary Inspect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ReadOnlySpan<byte> bytes = data;

        if (bytes.Length < 0x10 || !bytes[..4].SequenceEqual("bres"u8))
            throw new InvalidDataException("Invalid BRRES header.");

        ushort byteOrder = ReadUInt16(bytes, 0x04);
        if (byteOrder != 0xFEFF)
            throw new InvalidDataException($"Unsupported BRRES byte order 0x{byteOrder:X4}.");

        uint declaredLength = ReadUInt32(bytes, 0x08);
        uint rootOffset = ReadUInt16(bytes, 0x0C);
        ushort sectionCount = ReadUInt16(bytes, 0x0E);

        if (declaredLength > bytes.Length || declaredLength < 0x10)
            throw new InvalidDataException("BRRES declared file length is outside the supplied data.");
        if (rootOffset >= declaredLength || sectionCount == 0)
            throw new InvalidDataException("BRRES root offset or section count is invalid.");

        int modelOffset = FindMagic(bytes[..(int)declaredLength], "MDL0"u8);
        if (modelOffset < 0)
            throw new InvalidDataException("The BRRES archive does not contain an MDL0 model.");

        if (modelOffset + 0x4C > declaredLength)
            throw new InvalidDataException("The MDL0 header is truncated.");

        uint modelLength = ReadUInt32(bytes, modelOffset + 0x04);
        uint version = ReadUInt32(bytes, modelOffset + 0x08);
        if (version != 11)
            throw new InvalidDataException($"Unsupported MDL0 version {version}; this reader expects version 11.");
        if (modelLength < 0x4C || (long)modelOffset + modelLength > declaredLength)
            throw new InvalidDataException("The MDL0 model length exceeds the BRRES file.");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int section = 0; section < SectionNames.Length; section++)
        {
            uint relativeOffset = ReadUInt32(bytes, modelOffset + 0x10 + section * 4);
            if (relativeOffset == 0) continue;

            if (relativeOffset + 8 > modelLength)
                throw new InvalidDataException($"MDL0 {SectionNames[section]} section offset is invalid.");

            int groupOffset = checked(modelOffset + (int)relativeOffset);
            uint groupLength = ReadUInt32(bytes, groupOffset);
            uint groupCount = ReadUInt32(bytes, groupOffset + 4);
            if (groupLength < 8 || (ulong)relativeOffset + groupLength > modelLength)
                throw new InvalidDataException($"MDL0 {SectionNames[section]} index group is outside the model.");
            if (groupCount > 4096)
                throw new InvalidDataException($"MDL0 {SectionNames[section]} group count is implausible.");

            counts[SectionNames[section]] = checked((int)groupCount);
        }

        return new ModelSummary(
            checked((int)declaredLength),
            modelOffset,
            checked((int)modelLength),
            version,
            counts);
    }

    private static int FindMagic(ReadOnlySpan<byte> data, ReadOnlySpan<byte> magic)
    {
        for (int i = 0; i <= data.Length - magic.Length; i++)
            if (data.Slice(i, magic.Length).SequenceEqual(magic))
                return i;
        return -1;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
}
