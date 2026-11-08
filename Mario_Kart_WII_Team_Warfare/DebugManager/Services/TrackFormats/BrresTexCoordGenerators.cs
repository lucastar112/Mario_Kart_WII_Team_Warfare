using System.Buffers.Binary;
using System.Windows;

namespace DebugManager.Services.TrackFormats;

/// <summary>
/// Step 6c: resolves the GX TEV texcoord ID through the material's XF texgen
/// registers to the actual input UV channel. Keeps texture matrices available
/// for the later shader renderer; non-identity matrices are NOT guessed.
/// Layouts verified against BrawlLib MDL0Material, MDL0TexSRTData and XFData.
/// </summary>
internal static class BrresTexCoordGenerators
{
    internal sealed record Generator(int Id, int SourceRow, int InputUv, int Type,
        int Projection, int InputForm, bool Normalize, int DualMatrix,
        float ScaleU, float ScaleV, float Rotation, float TranslateU, float TranslateV,
        bool EffectIdentity, int MapMode)
    {
        // Non-UV and projected/emboss generators require normals, raster colors,
        // or other vertex attributes that our current GX decoder does not expose.
        internal bool DirectUv => Type == 0 && InputUv is >= 0 and <= 1 &&
            Projection == 0 && !Normalize && MapMode == 0;
        internal bool NeutralMatrix => EffectIdentity &&
            Math.Abs(ScaleU - 1) < 0.000001f && Math.Abs(ScaleV - 1) < 0.000001f &&
            Math.Abs(Rotation) < 0.000001f && Math.Abs(TranslateU) < 0.000001f &&
            Math.Abs(TranslateV) < 0.000001f;
    }

    internal sealed record Material(int Index, int LayerCount, IReadOnlyDictionary<int, Generator> Generators,
        string? Problem)
    {
        internal Generator? ForTexCoord(int coord) => Generators.GetValueOrDefault(coord);
    }

    internal static IReadOnlyDictionary<int, Material> Read(ReadOnlySpan<byte> bytes, int mdl, uint mdlLength)
    {
        Dictionary<int, Material> result = [];
        long end = (long)mdl + mdlLength;
        if (mdl < 0 || end > bytes.Length || mdlLength < 0x50) return result;
        int groupField = mdl + 0x10 + 8 * 4; // v11 material resource group
        int relative = I32(bytes, groupField);
        long group = (long)mdl + relative;
        if (relative <= 0 || group < mdl || group + 8 > end) return result;
        uint count = U32(bytes, (int)group + 4);
        if (count > 4096 || group + 8 + (count + 1) * 16L > end) return result;
        for (int index = 0; index < count; index++)
        {
            long entry = group + 8 + (index + 1) * 16L;
            long at = group + U32(bytes, (int)entry + 12);
            if (at < mdl || at + 0x40 > end) { result[index] = new(index, 0, new Dictionary<int, Generator>(), "invalid material offset"); continue; }
            int record = (int)at;
            int size = I32(bytes, record);
            int layers = I32(bytes, record + 0x2C);
            int refs = I32(bytes, record + 0x30);
            if (size < 0x400 || at + size > end || layers is < 0 or > 8 ||
                (layers > 0 && (refs <= 0 || (long)refs + layers * 0x34L > size)))
            {
                result[index] = new(index, 0, new Dictionary<int, Generator>(), "invalid material size or layer table");
                continue;
            }

            // v11: display-list pointer at +0x3C, XF stream at +0xE0 within it.
            int dl = I32(bytes, record + 0x3C);
            long xf = at + dl + 0xE0;
            Dictionary<int, uint> info = [], dual = [];
            if (dl > 0 && xf >= at && xf < at + size)
            {
                long p = xf;
                // XF command: 0x10, BE16 (count-1), BE16 address, BE32 values.
                for (int cmd = 0; cmd < 64 && p + 5 <= at + size && bytes[(int)p] == 0x10; cmd++)
                {
                    int n = U16(bytes, (int)p + 1) + 1;
                    int address = U16(bytes, (int)p + 3);
                    long next = p + 5 + n * 4L;
                    if (n > 16 || next > at + size) break;
                    for (int j = 0; j < n; j++)
                    {
                        int register = address + j;
                        uint value = U32(bytes, (int)p + 5 + j * 4);
                        if (register is >= 0x1040 and <= 0x1047) info[register - 0x1040] = value;
                        if (register is >= 0x1050 and <= 0x1057) dual[register - 0x1050] = value;
                    }
                    p = next;
                }
            }
            // BrawlLib: TexMatrices = material + 0x1A0 + (matRefOffset & 0xF).
            long srtBase = at + 0x1A0 + (refs == 0 ? 8 : refs & 0xF);
            Dictionary<int, Generator> generators = [];
            for (int id = 0; id < layers; id++)
            {
                if (!info.TryGetValue(id, out uint flags)) continue; // no guessing
                long srt = srtBase + 8 + id * 20L;
                long effect = srtBase + 168 + id * 52L;
                if (srt < at || srt + 20 > at + size || effect + 52 > at + size) continue;
                int sourceRow = (int)((flags >> 7) & 31);
                // XF SourceRow 5 = TexCoord0, 6 = TexCoord1, etc.
                int uv = sourceRow is >= 5 and <= 12 ? sourceRow - 5 : -1;
                uint dualFlags = dual.GetValueOrDefault(id);
                generators[id] = new Generator(id, sourceRow, uv,
                    (int)((flags >> 4) & 7), (int)((flags >> 1) & 1),
                    (int)((flags >> 2) & 3), ((dualFlags >> 8) & 0xFF) != 0,
                    (int)(dualFlags & 0xFF),
                    F32(bytes, (int)srt), F32(bytes, (int)srt + 4), F32(bytes, (int)srt + 8),
                    F32(bytes, (int)srt + 12), F32(bytes, (int)srt + 16),
                    bytes[(int)effect + 3] != 0, bytes[(int)effect + 2]);
            }
            result[index] = new Material(index, layers, generators,
                layers > 0 && generators.Count < layers ? $"XF texgens resolved {generators.Count}/{layers}" : null);
        }
        return result;
    }

    internal static IReadOnlyList<string> Describe(ReadOnlySpan<byte> b, int mdl, uint length)
    {
        var materials = Read(b, mdl, length);
        var shaders = BrresTevDiagnostics.Inspect(b, mdl, length);
        int selected = 0, direct = 0, neutral = 0, unresolved = 0;
        List<string> details = [];
        foreach (var shader in shaders)
        {
            if (!materials.TryGetValue(shader.MaterialIndex, out var mat)) continue;
            foreach (var stage in shader.Stages.Where(s => s.TextureEnabled))
            {
                selected++;
                var gen = mat.ForTexCoord(stage.TexCoord);
                if (gen is null) { unresolved++; details.Add($"mat{shader.MaterialIndex} stage{stage.Index}: coord{stage.TexCoord} has no parsed XF texgen"); continue; }
                if (gen.DirectUv)
                {
                    direct++;
                    if (gen.NeutralMatrix) neutral++;
                }
                else details.Add($"mat{shader.MaterialIndex} stage{stage.Index}: coord{stage.TexCoord} sourceRow={gen.SourceRow} type={gen.Type} projection={gen.Projection} mapMode={gen.MapMode} needs further vertex/shader support");
            }
        }
        List<string> lines = [$"Step 6c XF texgens: {materials.Count} materials; {selected} texture-enabled TEV stage selections; {direct} direct UV0/UV1 sources; {neutral} identity transforms; {unresolved} missing generators. Only supported map0 coordinates are used by the renderer; other cases retain first-layer UV0 fallback."];
        lines.AddRange(details.Take(12));
        return lines;
    }

    internal static IReadOnlyList<Point>? ResolveNeutralUvs(BrresModelReader.PolygonSurface surface,
        Material? material, int texCoord)
    {
        var gen = material?.ForTexCoord(texCoord);
        if (gen is null || !gen.DirectUv || !gen.NeutralMatrix) return null;
        var uvs = gen.InputUv == 0 ? surface.Uvs : surface.Uvs1;
        return uvs?.Count == surface.Corners.Count ? uvs : null;
    }

    // Resolve a *texture map* to the GX texcoord selected by its TEV stage. A TEV
    // stage index, texture map index and XF generator index are different things.
    // This is a conservative approximation for the existing single-layer WPF
    // renderer, not a replacement for full per-stage TEV sampling.
    internal static bool TryResolveFirstLayerUvs(
        BrresModelReader.PolygonSurface surface, Material? material,
        BrresTevDiagnostics.Shader? shader, out IReadOnlyList<Point>? output,
        out bool transformed)
    {
        output = null;
        transformed = false;
        if (material is null || shader is null || shader.Problem is not null) return false;

        // If multiple stages sample map 0 with different generated coordinates,
        // a single-layer renderer cannot represent them faithfully.
        var matches = shader.Stages.Where(stage => stage.TextureEnabled && stage.TextureMap == 0).ToArray();
        if (matches.Length == 0 || matches.Any(stage => stage.IndirectCommand != 0)) return false;
        int selected = matches[0].TexCoord;
        if (matches.Any(stage => stage.TexCoord != selected)) return false;
        Generator? gen = material.ForTexCoord(selected);
        if (gen is null || !gen.DirectUv || !gen.EffectIdentity || gen.DualMatrix != 0) return false;
        var source = gen.InputUv == 0 ? surface.Uvs : surface.Uvs1;
        if (source is null || source.Count != surface.Corners.Count) return false;
        if (!AreFinite(gen)) return false;
        if (gen.NeutralMatrix)
        {
            output = source;
            return true;
        }

        // GX texture SRT, rotating about the standard (0.5, 0.5) UV center.
        // External/effect matrices and projected texgens are excluded above.
        double radians = gen.Rotation * Math.PI / 180.0;
        double c = Math.Cos(radians), s = Math.Sin(radians);
        Point[] result = new Point[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            double u = (source[i].X - 0.5) * gen.ScaleU;
            double v = (source[i].Y - 0.5) * gen.ScaleV;
            Point point = new(u * c - v * s + 0.5 + gen.TranslateU,
                u * s + v * c + 0.5 + gen.TranslateV);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                Math.Abs(point.X) >= 100000 || Math.Abs(point.Y) >= 100000)
                return false;
            result[i] = point;
        }
        output = result;
        transformed = true;
        return true;
    }

    private static bool AreFinite(Generator g) =>
        float.IsFinite(g.ScaleU) && float.IsFinite(g.ScaleV) &&
        float.IsFinite(g.Rotation) && float.IsFinite(g.TranslateU) &&
        float.IsFinite(g.TranslateV);

    private static int I32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadInt32BigEndian(b.Slice(offset, 4));
    private static uint U32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(offset, 4));
    private static ushort U16(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt16BigEndian(b.Slice(offset, 2));
    private static float F32(ReadOnlySpan<byte> b, int offset) => BitConverter.Int32BitsToSingle(I32(b, offset));
}
