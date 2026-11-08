using System.Buffers.Binary;

namespace DebugManager.Services.TrackFormats;

/// <summary>
/// Read-only MDL0 TEV stage inspection. Offsets and BP command layouts follow the
/// BrawlCrate MDL0Material / MDL0Shader / StageGroup and Wii BP structures.
/// This class does NOT approximate a TEV shader or change how polygons render.
/// </summary>
internal static class BrresTevDiagnostics
{
    internal sealed record Stage(int Index, int TextureMap, int TexCoord, bool TextureEnabled,
        int RasterChannel, int ColorA, int ColorB, int ColorC, int ColorD,
        int AlphaA, int AlphaB, int AlphaC, int AlphaD,
        int ColorBias, bool ColorSubtract, bool ColorClamp, int ColorScale, int ColorDestination,
        int AlphaBias, bool AlphaSubtract, bool AlphaClamp, int AlphaScale, int AlphaDestination,
        int KonstColor, int KonstAlpha, int RasterSwap, int TextureSwap, int IndirectCommand);

    internal sealed record Shader(int MaterialIndex, int ShaderOffset, int DeclaredStages, IReadOnlyList<Stage> Stages,
        string? Problem);

    // Report is intentionally limited in the UI; full stage-level data is available to
    // follow-on rendering code via Inspect().
    internal static IReadOnlyList<string> Describe(ReadOnlySpan<byte> bytes, int mdl, uint mdlLength)
    {
        var shaders = Inspect(bytes, mdl, mdlLength);
        if (shaders.Count == 0) return ["TEV: no material shader records found."];
        int parsed = shaders.Count(s => s.Problem is null);
        int multi = shaders.Count(s => s.Stages.Count > 1);
        int usingAdditionalMaps = shaders.Count(s => s.Stages.Any(t => t.TextureEnabled && t.TextureMap > 0 && t.TextureMap < 8));
        int errors = shaders.Count - parsed;
        List<string> lines =
        [
            $"TEV: {parsed}/{shaders.Count} material shaders inspected; {multi} multi-stage; {usingAdditionalMaps} use texture maps beyond map 0; {errors} parsing warnings.",
            "TEV is diagnostic-only in this step; the renderer still uses the first texture layer."
        ];
        foreach (var shader in shaders.Where(s => s.Problem is not null).Take(6))
            lines.Add($"TEV material {shader.MaterialIndex}: {shader.Problem}");
        foreach (var shader in shaders.Where(s => s.Problem is null && (s.Stages.Count > 1 || s.Stages.Any(t => t.TextureMap > 0 && t.TextureEnabled))).Take(12))
        {
            string stages = string.Join("; ", shader.Stages.Select(s =>
                $"s{s.Index}: {(s.TextureEnabled ? $"map{s.TextureMap}/UV{s.TexCoord}" : "no texture")}, " +
                $"RGB[{s.ColorA},{s.ColorB},{s.ColorC},{s.ColorD}] {(s.ColorSubtract ? "sub" : "add")} → r{s.ColorDestination}, " +
                $"A[{s.AlphaA},{s.AlphaB},{s.AlphaC},{s.AlphaD}] → r{s.AlphaDestination}"));
            lines.Add($"TEV material {shader.MaterialIndex}: {stages}");
        }
        return lines;
    }

    internal static IReadOnlyList<Shader> Inspect(ReadOnlySpan<byte> b, int mdl, uint mdlLength)
    {
        List<Shader> shaders = [];
        long end = (long)mdl + mdlLength;
        if (mdl < 0 || end > b.Length || mdlLength < 0x40) return shaders;
        // MDL0 section-offset array: v11 material section is index 8.
        long sectionField = (long)mdl + 0x10 + 8 * 4;
        if (sectionField + 4 > end) return shaders;
        int groupRelative = ReadI32(b, (int)sectionField);
        long group = (long)mdl + groupRelative;
        if (groupRelative <= 0 || group + 8 > end) return shaders;
        uint count = ReadU32(b, (int)group + 4);
        if (count > 4096 || group + 8 + (count + 1) * 16L > end) return shaders;
        for (int i = 0; i < count; i++)
        {
            long entry = group + 8 + (i + 1) * 16L;
            long material = group + ReadU32(b, (int)entry + 12);
            if (material < mdl || material + 0x2C > end)
            {
                shaders.Add(new Shader(i, 0, 0, [], "invalid material record offset"));
                continue;
            }
            int shaderRelative = ReadI32(b, (int)material + 0x28);
            if (shaderRelative == 0)
            {
                shaders.Add(new Shader(i, 0, 0, [], "material has no shader pointer"));
                continue;
            }
            long shader = material + shaderRelative;
            if (shader < mdl || shader + 0x80 > end)
            {
                shaders.Add(new Shader(i, 0, 0, [], $"shader pointer 0x{shader:X} is outside MDL0"));
                continue;
            }
            int declared = b[(int)shader + 0x0C];
            if (declared is < 1 or > 16)
            {
                shaders.Add(new Shader(i, (int)shader, declared, [], "invalid TEV stage count"));
                continue;
            }
            List<Stage> stages = [];
            string? problem = null;
            for (int index = 0; index < declared; index++)
            {
                long groupOffset = shader + 0x80 + (index / 2) * 0x30L;
                if (groupOffset + 0x30 > end)
                {
                    problem = $"stage {index} is outside MDL0 bounds";
                    break;
                }
                // BPCommand = 0x61, register byte, 24-bit big-endian data.
                // StageGroup contains nine commands, each 5 bytes, with 3 padding bytes.
                if (!TryCommand(b, (int)groupOffset + 10, out uint tref) ||
                    !TryCommand(b, (int)groupOffset + 5, out uint ksel))
                {
                    problem = $"stage {index} has a missing texture-reference or konst-selection BP command";
                    break;
                }
                bool odd = (index & 1) != 0;
                int colorOffset = (int)groupOffset + (odd ? 20 : 15);
                int alphaOffset = (int)groupOffset + (odd ? 30 : 25);
                int indirectOffset = (int)groupOffset + (odd ? 40 : 35);
                if (!TryCommand(b, colorOffset, out uint color) ||
                    !TryCommand(b, alphaOffset, out uint alpha) ||
                    !TryCommand(b, indirectOffset, out uint indirect))
                {
                    problem = $"stage {index} has missing color/alpha/indirect BP commands";
                    break;
                }
                int shift = odd ? 12 : 0;
                int kshift = odd ? 14 : 4;
                stages.Add(new Stage(index,
                    (int)((tref >> shift) & 7), (int)((tref >> (shift + 3)) & 7),
                    ((tref >> (shift + 6)) & 1) != 0, (int)((tref >> (shift + 7)) & 7),
                    (int)((color >> 12) & 15), (int)((color >> 8) & 15), (int)((color >> 4) & 15), (int)(color & 15),
                    (int)((alpha >> 13) & 7), (int)((alpha >> 10) & 7), (int)((alpha >> 7) & 7), (int)((alpha >> 4) & 7),
                    (int)((color >> 16) & 3), ((color >> 18) & 1) != 0, ((color >> 19) & 1) != 0,
                    (int)((color >> 20) & 3), (int)((color >> 22) & 3),
                    (int)((alpha >> 16) & 3), ((alpha >> 18) & 1) != 0, ((alpha >> 19) & 1) != 0,
                    (int)((alpha >> 20) & 3), (int)((alpha >> 22) & 3),
                    (int)((ksel >> kshift) & 31), (int)((ksel >> (kshift + 5)) & 31),
                    (int)(alpha & 3), (int)((alpha >> 2) & 3), (int)indirect));
            }
            shaders.Add(new Shader(i, (int)shader, declared, stages, problem));
        }
        return shaders;
    }

    private static bool TryCommand(ReadOnlySpan<byte> b, int at, out uint data)
    {
        data = 0;
        if (at < 0 || at + 5 > b.Length || b[at] != 0x61) return false;
        data = ((uint)b[at + 2] << 16) | ((uint)b[at + 3] << 8) | b[at + 4];
        return true;
    }

    private static int ReadI32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32BigEndian(b.Slice(at, 4));
    private static uint ReadU32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(at, 4));
}
