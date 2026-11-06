using System.Buffers.Binary;
using System.Numerics;
using System.Windows.Media.Media3D;
using System.IO;

namespace DebugManager.Services.TrackFormats;

/// <summary>
/// Reads position geometry from a Mario Kart Wii MDL0 v11 model.
/// GX vertex stride is calculated from VCD and VAT registers; it is never guessed.
/// Non-position attributes are skipped using their declared formats.
/// </summary>
public static class BrresModelReader
{
    private const int MaxPolygonCount = 512;

    public sealed record DecodeResult(
        MeshGeometry3D Geometry,
        int PolygonCount,
        int DecodedPolygonCount,
        int DisplayVertices,
        int TriangleCount,
        int SkippedPolygonCount,
        IReadOnlyList<string> Warnings,
        Point3D Minimum,
        Point3D Maximum)
    {
        public string Summary =>
            $"BRRES mesh · {DecodedPolygonCount}/{PolygonCount} polygons · " +
            $"{DisplayVertices:N0} display vertices · {TriangleCount:N0} triangles" +
            (SkippedPolygonCount > 0 ? $" · {SkippedPolygonCount} skipped" : "");
    }

    private sealed record PositionGroup(float[] Values, int Count);
    private sealed record Polygon(int Offset, int Vertices, int Faces, int PositionGroup,
        uint VcdLow, uint VcdHigh, int DisplayOffset, int DisplayLength, Dictionary<byte, uint> Cp);
    private sealed record Primitive(int Topology, int Vat, int Count, int PayloadOffset, int Stride, int PositionOffset, int PositionWidth);

    public static DecodeResult Decode(byte[] brres)
    {
        ArgumentNullException.ThrowIfNull(brres);
        ReadOnlySpan<byte> b = brres;
        if (b.Length < 0x20 || !b[..4].SequenceEqual("bres"u8))
            throw new InvalidDataException("Invalid BRRES header.");

        uint fileLength = U32(b, 8);
        if (fileLength < 0x20 || fileLength > b.Length)
            throw new InvalidDataException("Invalid BRRES declared length.");

        int mdl = FindMagic(b[..(int)fileLength], "MDL0"u8);
        if (mdl < 0 || mdl + 0x4C > fileLength)
            throw new InvalidDataException("No valid MDL0 model was found in BRRES.");

        uint mdlLength = U32(b, mdl + 4);
        uint version = U32(b, mdl + 8);
        if (version != 11 || mdlLength < 0x4C || (long)mdl + mdlLength > fileLength)
            throw new InvalidDataException($"Unsupported MDL0 version {version} or invalid model length.");

        int vertexGroupOffset = GetSection(b, mdl, mdlLength, 2);
        int polygonGroupOffset = GetSection(b, mdl, mdlLength, 10);
        PositionGroup[] groups = ReadPositionGroups(b, mdl, mdlLength, vertexGroupOffset);
        List<Polygon> polygons = ReadPolygons(b, mdl, mdlLength, polygonGroupOffset);

        MeshGeometry3D mesh = new();
        Dictionary<(int Group, int Index), int> remap = [];
        List<string> warnings = [];
        Point3D min = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        Point3D max = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        int displayVertices = 0, triangles = 0, decoded = 0;

        foreach (Polygon polygon in polygons)
        {
            // Some course models contain explicit empty polygon placeholders. They are
            // represented by an empty position group and zero vertex/face counts.
            if (polygon.Vertices == 0 && polygon.Faces == 0)
                continue;

            try
            {
                if (polygon.Vertices == 0 || polygon.Faces == 0)
                    throw new InvalidDataException($"inconsistent empty polygon (vertices={polygon.Vertices}, faces={polygon.Faces}).");

                if ((uint)polygon.PositionGroup >= (uint)groups.Length)
                    throw new InvalidDataException($"position group {polygon.PositionGroup} does not exist.");

                List<int> corners = DecodeDisplayList(b.Slice(polygon.DisplayOffset, polygon.DisplayLength),
                    polygon, out int consumedVertices);
                if (consumedVertices != polygon.Vertices)
                    throw new InvalidDataException($"display list has {consumedVertices} vertices; header declares {polygon.Vertices}.");

                PositionGroup group = groups[polygon.PositionGroup];
                int firstIndex = mesh.TriangleIndices.Count;
                foreach (int sourceIndex in corners)
                {
                    if ((uint)sourceIndex >= (uint)group.Count)
                        throw new InvalidDataException($"position index {sourceIndex} is outside group {polygon.PositionGroup} ({group.Count} vertices).");

                    var key = (polygon.PositionGroup, sourceIndex);
                    if (!remap.TryGetValue(key, out int target))
                    {
                        int p = sourceIndex * 3;
                        Point3D point = new(group.Values[p], group.Values[p + 1], group.Values[p + 2]);
                        target = mesh.Positions.Count;
                        mesh.Positions.Add(point);
                        remap.Add(key, target);
                        min = new(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
                        max = new(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
                    }
                    mesh.TriangleIndices.Add(target);
                }

                int generatedTriangles = (mesh.TriangleIndices.Count - firstIndex) / 3;
                if ((mesh.TriangleIndices.Count - firstIndex) % 3 != 0)
                    throw new InvalidDataException("primitive expansion produced an incomplete triangle.");
                if (generatedTriangles != polygon.Faces)
                    throw new InvalidDataException($"header declares {polygon.Faces} faces, GX stream produces {generatedTriangles}.");
                displayVertices += consumedVertices;
                triangles += generatedTriangles;
                decoded++;
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
            {
                // A bad polygon must not prevent unrelated track meshes from being displayed.
                warnings.Add($"Polygon 0x{polygon.Offset:X}: {ex.Message}");
            }
        }

        if (mesh.TriangleIndices.Count == 0)
            throw new InvalidDataException("No polygon produced valid track geometry. " +
                string.Join(" | ", warnings.Take(4)));

        mesh.Freeze();
        return new DecodeResult(mesh, polygons.Count, decoded, displayVertices, triangles,
            polygons.Count - decoded, warnings, min, max);
    }

    private static PositionGroup[] ReadPositionGroups(ReadOnlySpan<byte> b, int mdl, uint mdlLength, int group)
    {
        int count = checked((int)U32(b, group + 4));
        if (count <= 0 || count > 1024) throw new InvalidDataException($"Invalid position-group count {count}.");
        PositionGroup[] result = new PositionGroup[count];

        for (int i = 0; i < count; i++)
        {
            int entry = checked(group + 8 + (i + 1) * 16); // first index-group entry is the sentinel
            EnsureRange(b, entry, 16);
            int record = checked(group + (int)U32(b, entry + 12));
            EnsureModelRange(record, 0x40, mdl, mdlLength, b.Length);

            uint dataRelative = U32(b, record + 8);
            uint componentCode = U32(b, record + 0x14);
            uint format = U32(b, record + 0x18);
            int divisor = b[record + 0x1C];
            int stride = b[record + 0x1D];
            int vertexCount = U16(b, record + 0x1E);

            // MDL0: component code 0 = XY, 1 = XYZ.
            int componentCount = componentCode switch
            {
                0 => 2,
                1 => 3,
                _ => throw new InvalidDataException($"Position group {i} has invalid component code {componentCode}.")
            };
            int scalarWidth = format switch
            {
                0 or 1 => 1, // UInt8 / Int8
                2 or 3 => 2, // UInt16 / Int16
                4 => 4,      // Float32
                _ => throw new InvalidDataException($"Position group {i} has unknown scalar format {format}.")
            };
            int packedWidth = checked(componentCount * scalarWidth);
            // Empty vertex groups are legal MDL0 placeholders. Their matching polygon
            // entries can have zero vertices and zero faces; do not reject the whole model.
            if (stride < packedWidth || divisor > 31)
                throw new InvalidDataException(
                    $"Position group {i} has invalid storage (components={componentCode}, format={format}, divisor={divisor}, stride={stride}).");

            int data = checked(record + (int)dataRelative);
            EnsureModelRange(data, checked(vertexCount * stride), mdl, mdlLength, b.Length);
            float[] values = new float[checked(vertexCount * 3)];
            float fixedPointScale = 1.0f / (1u << divisor);
            for (int v = 0; v < vertexCount; v++)
            {
                int p = data + v * stride;
                for (int component = 0; component < componentCount; component++)
                {
                    int scalarOffset = p + component * scalarWidth;
                    float value = format switch
                    {
                        0 => b[scalarOffset],
                        1 => unchecked((sbyte)b[scalarOffset]),
                        2 => U16(b, scalarOffset),
                        3 => BinaryPrimitives.ReadInt16BigEndian(b.Slice(scalarOffset, 2)),
                        4 => F32(b, scalarOffset),
                        _ => throw new InvalidDataException($"Unsupported position format {format}.")
                    };

                    if (format != 4)
                        value *= fixedPointScale;

                    values[v * 3 + component] = value;
                    if (!float.IsFinite(value))
                        throw new InvalidDataException($"Position group {i} contains non-finite coordinates.");
                }

                // XY-only groups are placed on the local Z=0 plane.
                if (componentCount == 2)
                    values[v * 3 + 2] = 0;
            }
            result[i] = new PositionGroup(values, vertexCount);
        }
        return result;
    }

    private static List<Polygon> ReadPolygons(ReadOnlySpan<byte> b, int mdl, uint mdlLength, int group)
    {
        int count = checked((int)U32(b, group + 4));
        if (count <= 0 || count > MaxPolygonCount) throw new InvalidDataException($"Invalid polygon count {count}.");
        List<Polygon> result = new(count);

        for (int i = 0; i < count; i++)
        {
            int entry = checked(group + 8 + (i + 1) * 16);
            EnsureRange(b, entry, 16);
            int obj = checked(group + (int)U32(b, entry + 12));
            EnsureModelRange(obj, 0x68, mdl, mdlLength, b.Length);

            uint recordLength = U32(b, obj);
            int vertices = checked((int)U32(b, obj + 0x40));
            int faces = checked((int)U32(b, obj + 0x44));

            // Empty polygon placeholders have no GX declaration to decode. Some older
            // exporters leave non-command bytes in the padded declaration area.
            // Filter these entries before reading CP/BP/XF declaration commands.
            if (vertices == 0 && faces == 0)
                continue;
            if (vertices == 0 || faces == 0)
                throw new InvalidDataException($"Polygon {i} has inconsistent empty counts (vertices={vertices}, faces={faces}).");

            uint declarationSize = U32(b, obj + 0x18);
            uint declarationRelative = U32(b, obj + 0x20);
            uint displaySize = U32(b, obj + 0x24);
            uint displayRelative = U32(b, obj + 0x2C);
            if (recordLength < 0x68 || (long)obj + recordLength > (long)mdl + mdlLength)
                throw new InvalidDataException($"Polygon {i} has invalid record length.");

            int declaration = checked(obj + 0x20 + (int)declarationRelative);
            int display = checked(obj + 0x24 + (int)displayRelative);
            EnsureModelRange(declaration, checked((int)declarationSize), mdl, mdlLength, b.Length);
            EnsureModelRange(display, checked((int)displaySize), mdl, mdlLength, b.Length);

            uint vcdLow = U32(b, obj + 0x0C);
            uint vcdHigh = U32(b, obj + 0x10);
            Dictionary<byte, uint> cp = ReadDeclaration(b.Slice(declaration, (int)declarationSize));
            // The polygon header is authoritative; CP declaration copies must agree when present.
            if (cp.TryGetValue(0x50, out uint lo) && lo != vcdLow)
                throw new InvalidDataException($"Polygon {i} VCD_LO disagrees with its CP declaration.");
            if (cp.TryGetValue(0x60, out uint hi) && hi != vcdHigh)
                throw new InvalidDataException($"Polygon {i} VCD_HI disagrees with its CP declaration.");

            int positionGroup = U16(b, obj + 0x48);

            result.Add(new Polygon(obj, vertices, faces, positionGroup, vcdLow, vcdHigh,
                display, checked((int)displaySize), cp));
        }
        return result;
    }

    private static Dictionary<byte, uint> ReadDeclaration(ReadOnlySpan<byte> declaration)
    {
        Dictionary<byte, uint> cp = [];
        for (int i = 0; i < declaration.Length;)
        {
            byte op = declaration[i];
            if (op == 0) { i++; continue; }
            if (op == 0x08) // GX_LOAD_CP_REG
            {
                if (i + 6 > declaration.Length) throw new InvalidDataException("Truncated CP command in vertex declaration.");
                cp[declaration[i + 1]] = U32(declaration, i + 2);
                i += 6;
                continue;
            }
            if (op == 0x61) // GX_LOAD_BP_REG
            {
                if (i + 5 > declaration.Length) throw new InvalidDataException("Truncated BP command in vertex declaration.");
                i += 5;
                continue;
            }
            if (op == 0x10) // GX_LOAD_XF_REG
            {
                if (i + 5 > declaration.Length) throw new InvalidDataException("Truncated XF command in vertex declaration.");
                int words = U16(declaration, i + 1) + 1;
                int length = checked(5 + words * 4);
                if (i + length > declaration.Length) throw new InvalidDataException("XF payload exceeds vertex declaration.");
                i += length;
                continue;
            }
            // Some exporters include initial draw commands in the declaration byte range.
            if ((op & 0xF8) is 0x80 or 0x90 or 0x98 or 0xA0) break;
            throw new InvalidDataException($"Unsupported declaration opcode 0x{op:X2} at +0x{i:X}.");
        }
        return cp;
    }

    private static List<int> DecodeDisplayList(ReadOnlySpan<byte> dl, Polygon polygon, out int consumedVertices)
    {
        List<int> triangles = [];
        int cursor = 0;
        consumedVertices = 0;

        while (cursor < dl.Length)
        {
            byte opcode = dl[cursor];
            if (opcode == 0x00) { cursor++; continue; }

            int primitive = opcode & 0xF8;
            if (primitive is 0x80 or 0x90 or 0x98 or 0xA0)
            {
                if (cursor + 3 > dl.Length) throw new InvalidDataException("Truncated GX primitive header.");
                int count = U16(dl, cursor + 1);
                int vat = opcode & 0x07;
                cursor += 3;
                if (count <= 0) throw new InvalidDataException("GX primitive has zero vertices.");

                VertexLayout layout = BuildVertexLayout(polygon, vat);
                int payloadBytes = checked(count * layout.Stride);
                if (cursor + payloadBytes > dl.Length) throw new InvalidDataException("GX primitive payload exceeds display-list bounds.");

                int[] indices = new int[count];
                for (int i = 0; i < count; i++)
                {
                    int p = cursor + i * layout.Stride + layout.PositionOffset;
                    indices[i] = layout.PositionWidth == 1 ? dl[p] : U16(dl, p);
                }
                cursor += payloadBytes;
                consumedVertices += count;
                ExpandPrimitive(triangles, primitive, indices);
                continue;
            }

            if (opcode == 0x61) // BP register write
            {
                if (cursor + 5 > dl.Length) throw new InvalidDataException("Truncated GX BP command.");
                cursor += 5;
                continue;
            }
            if (opcode == 0x08) // CP register write
            {
                if (cursor + 6 > dl.Length) throw new InvalidDataException("Truncated GX CP command.");
                cursor += 6;
                continue;
            }
            if (opcode == 0x10) // XF register write
            {
                if (cursor + 5 > dl.Length) throw new InvalidDataException("Truncated GX XF command.");
                int words = U16(dl, cursor + 1) + 1;
                int length = checked(5 + words * 4);
                if (cursor + length > dl.Length) throw new InvalidDataException("GX XF payload exceeds display-list bounds.");
                cursor += length;
                continue;
            }
            throw new InvalidDataException($"Unsupported GX display-list opcode 0x{opcode:X2} at +0x{cursor:X}.");
        }
        return triangles;
    }

    private sealed record VertexLayout(int Stride, int PositionOffset, int PositionWidth);

    private static VertexLayout BuildVertexLayout(Polygon polygon, int vat)
    {
        if (!polygon.Cp.TryGetValue(0x50, out uint vcdLow) ||
            !polygon.Cp.TryGetValue(0x60, out uint vcdHigh))
        {
            vcdLow = polygon.VcdLow;
            vcdHigh = polygon.VcdHigh;
        }

        if (!polygon.Cp.TryGetValue((byte)(0x70 + vat * 4), out uint vatA) ||
            !polygon.Cp.TryGetValue((byte)(0x80 + vat * 4), out uint vatB) ||
            !polygon.Cp.TryGetValue((byte)(0x90 + vat * 4), out uint vatC))
            throw new InvalidDataException($"VAT{vat} is not fully defined by the polygon declaration.");

        int stride = 0;
        // Matrix-index attributes are one byte each and precede position.
        int matrixMask = (int)(vcdLow & 0x1FF);
        stride += BitOperations.PopCount((uint)matrixMask);
        int positionOffset = stride;

        int posDescriptor = Field(vcdLow, 9);
        if (posDescriptor is not (2 or 3))
            throw new InvalidDataException("Position attribute is not indexed.");
        int positionWidth = posDescriptor == 2 ? 1 : 2;
        int posComponents = Bits(vatA, 0, 2) == 0 ? 2 : 3;
        int posFormat = Bits(vatA, 2, 3);
        stride += AttributeSize(posDescriptor, positionWidth, posComponents, posFormat);

        // Normal attribute: VCD_LO bits 11..12; VAT A bit 9 selects NBT (9 components).
        stride += AttributeSize(Field(vcdLow, 11), positionWidth,
            ((vatA >> 9) & 1) != 0 ? 9 : 3, Bits(vatA, 10, 3));

        // Two colour attributes, each with fixed byte size selected by its VAT format.
        stride += ColorAttributeSize(Field(vcdLow, 13), Bits(vatA, 15, 3));
        stride += ColorAttributeSize(Field(vcdLow, 15), Bits(vatA, 20, 3));

        // Texture coordinates 0..3 are in VCD_LO; 4..7 are in VCD_HI.
        for (int tex = 0; tex < 8; tex++)
        {
            int descriptor = tex < 4 ? Field(vcdLow, 17 + tex * 2) : Field(vcdHigh, (tex - 4) * 2);
            (int count, int format) = TextureVat(vatA, vatB, vatC, tex);
            stride += AttributeSize(descriptor, positionWidth, count == 0 ? 1 : 2, format);
        }

        if (stride <= positionOffset + positionWidth || stride > 128)
            throw new InvalidDataException($"Calculated GX vertex stride {stride} is invalid.");
        return new VertexLayout(stride, positionOffset, positionWidth);
    }

    private static (int Count, int Format) TextureVat(uint a, uint b, uint c, int tex)
    {
        return tex switch
        {
            0 => (Bits(a, 23, 2), Bits(a, 25, 3)),
            1 => (Bits(b, 0, 2), Bits(b, 2, 3)),
            2 => (Bits(b, 9, 2), Bits(b, 11, 3)),
            3 => (Bits(b, 18, 2), Bits(b, 20, 3)),
            4 => (Bits(b, 27, 2), Bits(b, 29, 3)),
            5 => (Bits(c, 0, 2), Bits(c, 2, 3)),
            6 => (Bits(c, 9, 2), Bits(c, 11, 3)),
            _ => (Bits(c, 18, 2), Bits(c, 20, 3))
        };
    }

    private static int AttributeSize(int descriptor, int indexWidth, int components, int format)
    {
        if (descriptor == 0) return 0;
        if (descriptor == 2) return indexWidth;
        if (descriptor == 3) return 2;
        if (descriptor != 1) throw new InvalidDataException($"Unknown GX attribute descriptor {descriptor}.");
        int scalar = format switch
        {
            0 or 1 => 1,
            2 or 3 => 2,
            4 => 4,
            _ => throw new InvalidDataException($"Unsupported GX scalar format {format}.")
        };
        return checked(components * scalar);
    }

    private static int ColorAttributeSize(int descriptor, int format)
    {
        if (descriptor == 0) return 0;
        if (descriptor == 2) return 1;
        if (descriptor == 3) return 2;
        if (descriptor != 1) throw new InvalidDataException($"Unknown GX colour descriptor {descriptor}.");
        return format switch
        {
            0 or 3 => 2,       // RGB565 / RGBA4
            1 or 4 => 3,       // RGB8 / RGBA6
            2 or 5 => 4,       // RGBX8 / RGBA8
            _ => throw new InvalidDataException($"Unsupported GX colour format {format}.")
        };
    }

    private static void ExpandPrimitive(List<int> output, int primitive, int[] ix)
    {
        switch (primitive)
        {
            case 0x90: // triangles
                if (ix.Length % 3 != 0) throw new InvalidDataException("Triangle-list vertex count is not divisible by three.");
                for (int i = 0; i < ix.Length; i += 3) AddTriangle(output, ix[i], ix[i + 1], ix[i + 2]);
                break;
            case 0x98: // triangle strip
                for (int i = 0; i + 2 < ix.Length; i++)
                {
                    if ((i & 1) == 0) AddTriangle(output, ix[i], ix[i + 1], ix[i + 2]);
                    else AddTriangle(output, ix[i + 1], ix[i], ix[i + 2]);
                }
                break;
            case 0xA0: // triangle fan
                for (int i = 1; i + 1 < ix.Length; i++) AddTriangle(output, ix[0], ix[i], ix[i + 1]);
                break;
            case 0x80: // quads
                if (ix.Length % 4 != 0) throw new InvalidDataException("Quad vertex count is not divisible by four.");
                for (int i = 0; i < ix.Length; i += 4)
                {
                    AddTriangle(output, ix[i], ix[i + 1], ix[i + 2]);
                    AddTriangle(output, ix[i], ix[i + 2], ix[i + 3]);
                }
                break;
        }
    }

    private static void AddTriangle(List<int> output, int a, int b, int c)
    {
        output.Add(a); output.Add(b); output.Add(c);
    }

    private static int Field(uint value, int shift) => Bits(value, shift, 2);
    private static int Bits(uint value, int shift, int width) => (int)((value >> shift) & ((1u << width) - 1));

    private static int GetSection(ReadOnlySpan<byte> b, int mdl, uint mdlLength, int section)
    {
        uint relative = U32(b, mdl + 0x10 + section * 4);
        if (relative == 0 || (ulong)relative + 8 > mdlLength)
            throw new InvalidDataException($"MDL0 section {section} is missing or invalid.");
        int offset = checked(mdl + (int)relative);
        uint size = U32(b, offset), count = U32(b, offset + 4);
        if (size < 8 || (ulong)relative + size > mdlLength || count > 4096)
            throw new InvalidDataException($"MDL0 section {section} index group is invalid.");
        return offset;
    }

    private static void EnsureModelRange(int offset, int length, int mdl, uint mdlLength, int fileLength)
    {
        if (length < 0 || offset < mdl || (long)offset + length > (long)mdl + mdlLength || (long)offset + length > fileLength)
            throw new InvalidDataException("MDL0 data range is outside the model.");
    }

    private static void EnsureRange(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > bytes.Length)
            throw new InvalidDataException("BRRES data range is outside the file.");
    }

    private static int FindMagic(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> magic)
    {
        for (int i = 0; i <= bytes.Length - magic.Length; i++)
            if (bytes.Slice(i, magic.Length).SequenceEqual(magic)) return i;
        return -1;
    }

    private static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.Slice(p, 2));
    private static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(p, 4));
    private static float F32(ReadOnlySpan<byte> b, int p) => BitConverter.Int32BitsToSingle(unchecked((int)U32(b, p)));
}
