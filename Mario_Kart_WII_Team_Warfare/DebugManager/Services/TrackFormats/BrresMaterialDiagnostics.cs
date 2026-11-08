using System.Buffers.Binary;
using System.Text;
using System.IO;
namespace DebugManager.Services.TrackFormats;

/// <summary>
/// Inspects the MDL0 material -> layer -> TEX0 relationships independently from
/// geometry decoding. It deliberately reports unsupported texture formats rather
/// than treating them as successfully decoded.
/// </summary>
public static class BrresMaterialDiagnostics
{
    public sealed record Report(
        int PolygonCount,
        int AssignedPolygons,
        int MaterialCount,
        int TextureCount,
        int DecodedTextures,
        int MissingMaterialReferences,
        int MissingTextureReferences,
        IReadOnlyList<string> Warnings);

    public sealed record SurfaceMaterial(int ObjectIndex, int MaterialIndex, string? TextureName, int WrapU, int WrapV);
    public sealed record TexturePixels(int Width, int Height, byte[] Rgba);

    private sealed record TextureInfo(string Name, int Offset, int Length, int Width, int Height, int Format, bool Decoded);
    private sealed record MaterialInfo(string Name, int Offset, int LayerCount, string? FirstTexture);

    // Diagnostic-only inspection of the complete material layer table. Does not alter rendering.
    internal static IReadOnlyList<string> DescribeTextureLayers(ReadOnlySpan<byte> b, int mdl, uint mdlLength)
    {
        List<string> lines = [];
        List<string> warnings = [];
        var materials = ReadMaterials(b, mdl, mdlLength, warnings);
        int withLayers = 0, multiLayer = 0;
        foreach (var material in materials)
        {
            if (material.LayerCount == 0) continue;
            withLayers++;
            if (material.LayerCount <= 1) continue;
            multiLayer++;
            int start = checked(material.Offset + I32(b, material.Offset + 0x30));
            List<string> layers = [];
            for (int i = 0; i < material.LayerCount; i++)
            {
                int layer = checked(start + i * 0x34);
                // ReadMaterials already validated the layer table against the material record.
                string name = ReadRelativeString(b, layer, b.Length);
                int u = checked((int)U32(b, layer + 0x18));
                int v = checked((int)U32(b, layer + 0x1C));
                layers.Add($"{i}: '{name}' (wrap U={u}, V={v})");
            }
            lines.Add($"Material '{material.Name}': {string.Join("; ", layers)}");
        }
        lines.Insert(0, $"Texture layers: {withLayers}/{materials.Count} materials have layers; {multiLayer} use multiple layers (renderer currently displays layer 0 only).");
        if (multiLayer > 20)
        {
            lines.RemoveRange(21, lines.Count - 21);
            lines.Add($"... {multiLayer - 20} additional multi-layer materials omitted from tooltip.");
        }
        lines.AddRange(BrresTevDiagnostics.Describe(b, mdl, mdlLength));
        lines.AddRange(BrresTexCoordGenerators.Describe(b, mdl, mdlLength));
        return lines;
    }

    // Step 6d: preserve all material texture maps for TEV texture-map IDs.
    // The first-layer material association remains unchanged.
    internal static IReadOnlyDictionary<int, IReadOnlyList<BrresTevCompositor.Layer>> GetTevLayers(
        ReadOnlySpan<byte> b, int mdl, uint mdlLength)
    {
        Dictionary<int, IReadOnlyList<BrresTevCompositor.Layer>> result = [];
        List<string> warnings = [];
        var materials = ReadMaterials(b, mdl, mdlLength, warnings);
        for (int index = 0; index < materials.Count; index++)
        {
            var material = materials[index];
            List<BrresTevCompositor.Layer> layers = [];
            if (material.LayerCount > 0)
            {
                int baseOffset = checked(material.Offset + I32(b, material.Offset + 0x30));
                for (int i = 0; i < material.LayerCount; i++)
                {
                    int at = checked(baseOffset + i * 0x34);
                    string texture = ReadRelativeString(b, at, b.Length);
                    int wrapU = checked((int)U32(b, at + 0x18));
                    int wrapV = checked((int)U32(b, at + 0x1C));
                    if (string.IsNullOrEmpty(texture) || wrapU is < 0 or > 2 || wrapV is < 0 or > 2)
                    {
                        layers.Clear();
                        break;
                    }
                    layers.Add(new BrresTevCompositor.Layer(texture, wrapU, wrapV));
                }
            }
            result[index] = layers;
        }
        return result;
    }

    internal static IReadOnlyDictionary<int, SurfaceMaterial> GetSurfaceMaterials(ReadOnlySpan<byte> b, int mdl, uint mdlLength)
    {
        List<string> warnings = [];
        var assignments = ReadDrawListAssignments(b, mdl, mdlLength, warnings);
        var materials = ReadMaterials(b, mdl, mdlLength, warnings);
        Dictionary<int, SurfaceMaterial> result = [];
        foreach (var pair in assignments)
        {
            if ((uint)pair.Value >= (uint)materials.Count) continue;
            var material = materials[pair.Value];
            int u = 0, v = 0;
            if (material.LayerCount > 0)
            {
                int layer = checked(material.Offset + (int)I32(b, material.Offset + 0x30));
                if (layer >= 0 && layer + 0x20 <= b.Length)
                {
                    u = (int)U32(b, layer + 0x18);
                    v = (int)U32(b, layer + 0x1C);
                }
            }
            result[pair.Key] = new SurfaceMaterial(pair.Key, pair.Value, material.FirstTexture, u, v);
        }
        return result;
    }

    internal static IReadOnlyDictionary<string, TexturePixels> GetTexturePixels(ReadOnlySpan<byte> b)
    {
        Dictionary<string, TexturePixels> result = new(StringComparer.Ordinal);
        int cursor = 0;
        while (cursor <= b.Length - 0x40)
        {
            int found = FindMagic(b, "TEX0"u8, cursor);
            if (found < 0) break;
            cursor = found + 4;
            try
            {
                uint length = U32(b, found + 4);
                if (U32(b, found + 8) is not (1 or 3) || length < 0x40 || (long)found + length > b.Length) continue;
                string name = ReadStringAt(b, (long)found + I32(b, found + 0x14), b.Length);
                int width = U16(b, found + 0x1C), height = U16(b, found + 0x1E);
                int format = checked((int)U32(b, found + 0x20));
                int image = checked(found + I32(b, found + 0x10));
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || result.ContainsKey(name)) continue;
                if (TryDecodePixels(b, image, checked(found + (int)length), width, height, format, out byte[] rgba))
                    result[name] = new TexturePixels(width, height, rgba);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentOutOfRangeException) { }
        }
        return result;
    }

    internal static Report Analyze(ReadOnlySpan<byte> data, int mdl, uint mdlLength,
        IReadOnlyList<BrresModelReader.Polygon> polygons)
    {
        List<string> warnings = [];
        Dictionary<string, TextureInfo> textures = ReadTextures(data, warnings);
        List<MaterialInfo> materials = ReadMaterials(data, mdl, mdlLength, warnings);
        int assigned = 0, missingMaterial = 0, missingTexture = 0;
        Dictionary<int, int> drawMaterials = ReadDrawListAssignments(data, mdl, mdlLength, warnings);

        foreach (BrresModelReader.Polygon polygon in polygons)
        {
            if (polygon.Vertices == 0 && polygon.Faces == 0) continue;
            // Polygon +0x3C is its object index, NOT a material index.
            // Section 0 draw-list opcode 0x04 supplies the actual association.
            if (!drawMaterials.TryGetValue(polygon.MaterialIndex, out int materialIndex))
            {
                missingMaterial++;
                warnings.Add($"Object {polygon.MaterialIndex} (0x{polygon.Offset:X}): no material assignment in parsed MDL0 draw lists.");
                continue;
            }
            if ((uint)materialIndex >= (uint)materials.Count)
            {
                missingMaterial++;
                warnings.Add($"Object {polygon.MaterialIndex}: draw list references material {materialIndex}, outside {materials.Count} material records.");
                continue;
            }

            assigned++;
            MaterialInfo material = materials[materialIndex];
            if (material.LayerCount == 0)
            {
                warnings.Add($"Material {materialIndex} '{material.Name}' has no texture layers; geometry will use the neutral fallback.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(material.FirstTexture) || !textures.ContainsKey(material.FirstTexture))
            {
                missingTexture++;
                warnings.Add($"Material {materialIndex} '{material.Name}': first texture layer '{material.FirstTexture ?? "<unresolved>"}' has no matching TEX0 resource.");
            }
        }

        int decoded = textures.Values.Count(t => t.Decoded);
        foreach (TextureInfo texture in textures.Values.Where(t => !t.Decoded))
            warnings.Add($"TEX0 '{texture.Name}' ({texture.Width}x{texture.Height}, format 0x{texture.Format:X}): decoding is not supported or its image data is invalid.");

        return new Report(polygons.Count, assigned, materials.Count, textures.Count, decoded,
            missingMaterial, missingTexture, warnings);
    }

    // MDL0 section 0 is the authoritative polygon -> material mapping.
    // The index stored at polygon +0x3C identifies an object, not a material.
    private static Dictionary<int, int> ReadDrawListAssignments(ReadOnlySpan<byte> b, int mdl,
        uint mdlLength, List<string> warnings)
    {
        Dictionary<int, int> assignments = [];
        int group = GetSection(b, mdl, mdlLength, 0);
        if (group == 0)
        {
            warnings.Add("MDL0 has no valid section 0 draw-list group; material assignments are unknown.");
            return assignments;
        }
        int count = checked((int)U32(b, group + 4));
        for (int i = 0; i < count; i++)
        {
            int entry = checked(group + 8 + (i + 1) * 16);
            EnsureModel(b, entry, 16, mdl, mdlLength);
            int start = checked(group + (int)U32(b, entry + 12));
            if (start < mdl || start >= (long)mdl + mdlLength)
            {
                warnings.Add($"Draw list {i}: invalid start offset 0x{start:X}.");
                continue;
            }
            int p = start;
            int limit = checked((int)Math.Min((long)mdl + mdlLength, (long)start + 1024 * 1024));
            bool terminated = false;
            while (p < limit)
            {
                byte op = b[p++];
                if (op == 0x00) continue;
                if (op == 0x01) { terminated = true; break; }
                int bytes = op switch { 0x02 => 4, 0x04 => 7, 0x05 => 4, 0x06 => 4, _ => -1 };
                if (bytes < 0)
                {
                    // Opcode 0x03 has variable length; don't guess its extent.
                    warnings.Add($"Draw list {i}: unsupported opcode 0x{op:X2} at 0x{p - 1:X}; remaining assignments in this list are unknown.");
                    break;
                }
                if (p + bytes > limit)
                {
                    warnings.Add($"Draw list {i}: truncated opcode 0x{op:X2}.");
                    break;
                }
                if (op == 0x04)
                {
                    int material = U16(b, p);
                    int obj = U16(b, p + 2);
                    if (assignments.TryGetValue(obj, out int previous) && previous != material)
                        warnings.Add($"Object {obj} uses multiple materials ({previous}, {material}); the first assignment is retained for diagnostics.");
                    else assignments.TryAdd(obj, material);
                }
                p += bytes;
            }
            if (!terminated)
                warnings.Add($"Draw list {i}: no end command was reached.");
        }
        return assignments;
    }

    private static List<MaterialInfo> ReadMaterials(ReadOnlySpan<byte> b, int mdl, uint mdlLength, List<string> warnings)
    {
        List<MaterialInfo> result = [];
        int group = GetSection(b, mdl, mdlLength, 8);
        if (group == 0) return result;
        int count = checked((int)U32(b, group + 4));
        if (count > 4096) throw new InvalidDataException("MDL0 material group has an unreasonable entry count.");

        for (int i = 0; i < count; i++)
        {
            int entry = checked(group + 8 + (i + 1) * 16);
            Ensure(b, entry, 16);
            int record = checked(group + (int)U32(b, entry + 12));
            EnsureModel(b, record, 0x40, mdl, mdlLength);
            uint recordLength = U32(b, record);
            if (recordLength < 0x418 || (long)record + recordLength > (long)mdl + mdlLength)
            {
                warnings.Add($"Material {i}: invalid MDL0 material record length 0x{recordLength:X}.");
                result.Add(new MaterialInfo($"material_{i}", record, 0, null));
                continue;
            }

            string name = ReadRelativeString(b, record + 8, b.Length);
            int layerCount = checked((int)U32(b, record + 0x2C));
            int layerOffset = checked(record + (int)I32(b, record + 0x30));
            if (layerCount < 0 || layerCount > 8 || (layerCount > 0 && (long)layerOffset + layerCount * 0x34L > (long)record + recordLength))
            {
                warnings.Add($"Material {i} '{name}': invalid texture layer table (count={layerCount}, offset=0x{layerOffset:X}).");
                result.Add(new MaterialInfo(name, record, 0, null));
                continue;
            }

            string? firstTexture = null;
            if (layerCount > 0)
                firstTexture = ReadRelativeString(b, layerOffset, b.Length);
            result.Add(new MaterialInfo(name, record, layerCount, firstTexture));
        }
        return result;
    }

    private static Dictionary<string, TextureInfo> ReadTextures(ReadOnlySpan<byte> b, List<string> warnings)
    {
        Dictionary<string, TextureInfo> result = new(StringComparer.Ordinal);
        int cursor = 0;
        while (cursor <= b.Length - 0x40)
        {
            int found = FindMagic(b, "TEX0"u8, cursor);
            if (found < 0) break;
            cursor = found + 4;
            try
            {
                uint length = U32(b, found + 4);
                uint version = U32(b, found + 8);
                if (version is not (1 or 3) || length < 0x40 || (ulong)found + length > (ulong)b.Length)
                    continue;

                int dataRelative = I32(b, found + 0x10);
                int nameRelative = I32(b, found + 0x14);
                long nameTarget = (long)found + nameRelative;
                string name = ReadStringAt(b, nameTarget, b.Length);
                int header = found + 0x18;
                int width = U16(b, header + 4);
                int height = U16(b, header + 6);
                int format = checked((int)U32(b, header + 8));
                int imageOffset = checked(found + dataRelative);
                bool decoded = width > 0 && height > 0 && width <= 8192 && height <= 8192 &&
                    TryValidateAndDecode(b, imageOffset, checked((int)((long)found + length)), width, height, format);

                TextureInfo info = new(name, found, checked((int)length), width, height, format, decoded);
                if (!string.IsNullOrEmpty(name))
                {
                    if (!result.TryAdd(name, info))
                        warnings.Add($"Duplicate TEX0 resource name '{name}'. The first resource is used for diagnostics.");
                }
                else warnings.Add($"TEX0 at 0x{found:X} has an unresolved resource name.");
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
            {
                warnings.Add($"TEX0 at 0x{found:X}: {ex.Message}");
            }
        }
        return result;
    }

    // Validation includes the tiled image payload size. Supported formats are decoded
    // into RGBA bytes so the success count means the pixel stream was actually read.
    private static bool TryValidateAndDecode(ReadOnlySpan<byte> b, int image, int end, int width, int height, int format)
    {
        return TryDecodePixels(b, image, end, width, height, format, out _);
    }

    private static bool TryDecodePixels(ReadOnlySpan<byte> b, int image, int end, int width, int height, int format, out byte[] rgba)
    {
        rgba = [];
        (int bw, int bh, int bytes) = format switch
        {
            0 => (8, 8, 32),       // I4
            1 or 2 => (8, 4, 32),  // I8 / IA4
            3 or 4 or 5 => (4, 4, 32), // IA8 / RGB565 / RGB5A3
            6 => (4, 4, 64),       // RGBA8
            14 => (8, 8, 32),     // CMPR (size validated; decoder added separately)
            _ => (0, 0, 0)
        };
        if (bw == 0) return false;
        long needed = (long)((width + bw - 1) / bw) * ((height + bh - 1) / bh) * bytes;
        if (image < 0 || (long)image + needed > end) return false;
        // Decode the tiled formats to ensure their block layout and pixel streams are valid.
        rgba = new byte[checked(width * height * 4)];
        int blocksX = (width + bw - 1) / bw;
        for (int by = 0; by < (height + bh - 1) / bh; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            int block = image + (by * blocksX + bx) * bytes;
            if (format == 14)
            {
                DecodeCmprBlock(b, block, width, height, bx * 8, by * 8, rgba);
                continue;
            }
            for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                int px = bx * bw + x, py = by * bh + y;
                if (px >= width || py >= height) continue;
                byte r, g, bl, a = 255;
                int pixel = y * bw + x;
                switch (format)
                {
                    case 0: { byte n = b[block + pixel / 2]; byte v = (byte)(((pixel & 1) == 0 ? n >> 4 : n & 15) * 17); r = g = bl = v; break; }
                    case 1: { r = g = bl = b[block + pixel]; break; }
                    case 2: { byte v = b[block + pixel]; a = (byte)((v >> 4) * 17); r = g = bl = (byte)((v & 15) * 17); break; }
                    case 3: { int q = block + pixel * 2; ushort v = U16(b, q); a = (byte)((v >> 8) & 255); r = g = bl = (byte)(v & 255); break; }
                    case 4: { ushort v = U16(b, block + pixel * 2); r = Expand5((v >> 11) & 31); g = Expand6((v >> 5) & 63); bl = Expand5(v & 31); break; }
                    case 5: { ushort v = U16(b, block + pixel * 2); if ((v & 0x8000) != 0) { r = Expand5((v >> 10) & 31); g = Expand5((v >> 5) & 31); bl = Expand5(v & 31); } else { a = (byte)(((v >> 12) & 7) * 255 / 7); r = (byte)(((v >> 8) & 15) * 17); g = (byte)(((v >> 4) & 15) * 17); bl = (byte)((v & 15) * 17); } break; }
                    case 6: { int ar = block + pixel * 2; int gb = block + 32 + pixel * 2; a = b[ar]; r = b[ar + 1]; g = b[gb]; bl = b[gb + 1]; break; }
                    default: return false;
                }
                int dst = (py * width + px) * 4; rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = bl; rgba[dst + 3] = a;
            }
        }
        return rgba.Length == checked(width * height * 4);
    }

    private static void DecodeCmprBlock(ReadOnlySpan<byte> b, int block, int width, int height, int originX, int originY, byte[] output)
    {
        // GX CMPR stores four DXT1-style 4x4 blocks in TL, BL, TR, BR order.
        ReadOnlySpan<(int X, int Y)> positions = [(0, 0), (4, 0), (0, 4), (4, 4)];
        for (int sub = 0; sub < 4; sub++)
        {
            int p = block + sub * 8;
            ushort c0 = U16(b, p), c1 = U16(b, p + 2);
            Span<byte> palette = stackalloc byte[16];
            Decode565(c0, palette.Slice(0, 4));
            Decode565(c1, palette.Slice(4, 4));
            if (c0 > c1)
            {
                for (int k = 0; k < 3; k++)
                {
                    palette[8 + k] = (byte)((2 * palette[k] + palette[4 + k]) / 3);
                    palette[12 + k] = (byte)((palette[k] + 2 * palette[4 + k]) / 3);
                }
                palette[11] = palette[15] = 255;
            }
            else
            {
                for (int k = 0; k < 3; k++) palette[8 + k] = (byte)((palette[k] + palette[4 + k]) / 2);
                palette[11] = 255;
                palette[12] = palette[13] = palette[14] = 0; palette[15] = 0;
            }

            uint indices = U32(b, p + 4);
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int index = (int)((indices >> (30 - (y * 4 + x) * 2)) & 3);
                int px = originX + positions[sub].X + x, py = originY + positions[sub].Y + y;
                if (px >= width || py >= height) continue;
                int dst = (py * width + px) * 4, src = index * 4;
                output[dst] = palette[src]; output[dst + 1] = palette[src + 1];
                output[dst + 2] = palette[src + 2]; output[dst + 3] = palette[src + 3];
            }
        }
    }

    private static void Decode565(ushort value, Span<byte> color)
    {
        color[0] = Expand5((value >> 11) & 31);
        color[1] = Expand6((value >> 5) & 63);
        color[2] = Expand5(value & 31);
        color[3] = 255;
    }

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
    private static byte Expand6(int v) => (byte)((v << 2) | (v >> 4));

    private static int GetSection(ReadOnlySpan<byte> b, int mdl, uint mdlLength, int section)
    {
        uint rel = U32(b, mdl + 0x10 + section * 4);
        if (rel == 0 || (ulong)rel + 8 > mdlLength) return 0;
        int p = checked(mdl + (int)rel);
        uint size = U32(b, p), count = U32(b, p + 4);
        if (size < 8 || (ulong)rel + size > mdlLength || count > 4096) return 0;
        return p;
    }

    private static string ReadRelativeString(ReadOnlySpan<byte> b, int relativeField, int limit) =>
        ReadStringAt(b, (long)relativeField + I32(b, relativeField), limit);

    private static string ReadStringAt(ReadOnlySpan<byte> b, long target, int limit)
    {
        if (target < 0 || target >= limit) return string.Empty;
        int start = (int)target, end = start;
        while (end < limit && b[end] != 0 && end - start < 256) end++;
        if (end == limit || end - start >= 256) return string.Empty;
        return Encoding.ASCII.GetString(b.Slice(start, end - start));
    }

    private static int FindMagic(ReadOnlySpan<byte> b, ReadOnlySpan<byte> magic, int start)
    {
        for (int i = start; i <= b.Length - magic.Length; i++) if (b.Slice(i, magic.Length).SequenceEqual(magic)) return i;
        return -1;
    }
    private static void Ensure(ReadOnlySpan<byte> b, int p, int n) { if (p < 0 || n < 0 || (long)p + n > b.Length) throw new InvalidDataException("Material record is outside the BRRES file."); }
    private static void EnsureModel(ReadOnlySpan<byte> b, int p, int n, int mdl, uint len) { Ensure(b, p, n); if (p < mdl || (long)p + n > (long)mdl + len) throw new InvalidDataException("Material record is outside MDL0."); }
    private static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.Slice(p, 2));
    private static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(p, 4));
    private static int I32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadInt32BigEndian(b.Slice(p, 4));
}
