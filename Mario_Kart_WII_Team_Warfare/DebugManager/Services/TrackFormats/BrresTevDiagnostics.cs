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

    // GX konst colors are stored in each MDL0 v11 material's 0x180-byte
    // BP setup list, NOT in the shared TEV shader stage record.
    // Registers E0/E1 .. E6/E7 hold K0..K3 when bit 23 is set.
    internal sealed record MaterialKonst(float[] Rgba, bool[] Known)
    {
        internal bool HasChannel(int register, int channel) =>
            register is >= 0 and < 4 && channel is >= 0 and < 4 && Known[register * 4 + channel];
        internal float Channel(int register, int channel) => Rgba[register * 4 + channel];
    }

    internal static IReadOnlyDictionary<int, MaterialKonst> ReadMaterialKonst(ReadOnlySpan<byte> b, int mdl, uint mdlLength)
    {
        Dictionary<int, MaterialKonst> result = [];
        long end = (long)mdl + mdlLength;
        long sectionField = (long)mdl + 0x10 + 8 * 4;
        if (mdl < 0 || end > b.Length || sectionField + 4 > end) return result;
        int groupRelative = ReadI32(b, (int)sectionField);
        long group = (long)mdl + groupRelative;
        if (groupRelative <= 0 || group + 8 > end) return result;
        uint count = ReadU32(b, (int)group + 4);
        if (count > 4096 || group + 8 + (count + 1) * 16L > end) return result;
        for (int i = 0; i < count; i++)
        {
            long entry = group + 8 + (i + 1) * 16L;
            long material = group + ReadU32(b, (int)entry + 12);
            if (material < mdl || material + 0x40 > end) continue;
            uint materialSize = ReadU32(b, (int)material);
            int setupRelative = ReadI32(b, (int)material + 0x3C);
            long setup = material + setupRelative;
            if (materialSize < 0x180 || setupRelative <= 0 ||
                setup < material + 0x40 || setup + 0x180 > material + materialSize ||
                setup + 0x180 > end) continue;
            var rgba = new float[16];
            var known = new bool[16];
            // The constant-color BP commands occupy offsets 0x60..0x83 of
            // MDL0's 0x180-byte material setup block, exactly 8 x 5 bytes.
            for (int j = 0; j < 8; j++)
            {
                int at = checked((int)setup + 0x60 + 5 * j);
                byte reg = (byte)(0xE0 + j);
                if (b[at] != 0x61 || b[at + 1] != reg) continue;
                uint value = ((uint)b[at + 2] << 16) | ((uint)b[at + 3] << 8) | b[at + 4];
                if ((value & 0x800000) == 0) continue; // ordinary TEV reg, not K register
                int first = (int)(value & 0x7FF);
                int second = (int)((value >> 12) & 0x7FF);
                // GXSetTevKColor uses 8-bit channel values. Reject out-of-range
                // fields rather than reinterpret S10 register commands as colors.
                int index = (j / 2) * 4;
                int a = index + ((j & 1) == 0 ? 0 : 2); // even: R/A, odd: B/G
                int c = index + ((j & 1) == 0 ? 3 : 1);
                if (first <= 255) { rgba[a] = first / 255f; known[a] = true; }
                if (second <= 255) { rgba[c] = second / 255f; known[c] = true; }
            }
            result[i] = new MaterialKonst(rgba, known);
        }
        return result;
    }

    // Ordinary (non-konst) TEV output registers, decoded from the BP setup list.
    // Slots: PREV, REG0, REG1, REG2. Each has RGBA, with independent known flags.
    // Preserve signed S10 fields (the TEV registers are not 8-bit konst colors).
    internal sealed record InitialRegisters(float[] Rgba, bool[] Known)
    {
        internal int KnownChannels => Known.Count(x => x);
        internal bool Complete => Known.All(x => x);
    }

    internal static IReadOnlyDictionary<int, InitialRegisters> ReadInitialRegisters(
        ReadOnlySpan<byte> b, int mdl, uint mdlLength)
    {
        Dictionary<int, InitialRegisters> result = [];
        long end = (long)mdl + mdlLength;
        long sectionField = (long)mdl + 0x10 + 8 * 4;
        if (mdl < 0 || end > b.Length || sectionField + 4 > end) return result;
        int relative = ReadI32(b, (int)sectionField);
        long group = (long)mdl + relative;
        if (relative <= 0 || group + 8 > end) return result;
        uint count = ReadU32(b, (int)group + 4);
        if (count > 4096 || group + 8 + (count + 1) * 16L > end) return result;
        for (int i = 0; i < count; ++i)
        {
            long entry = group + 8 + (i + 1) * 16L;
            long material = group + ReadU32(b, (int)entry + 12);
            if (material < mdl || material + 0x40 > end) continue;
            uint length = ReadU32(b, (int)material);
            int off = ReadI32(b, (int)material + 0x3C);
            long setup = material + off;
            if (length < 0x180 || off <= 0 || setup < material + 0x40 ||
                setup + 0x180 > material + length || setup + 0x180 > end) continue;
            float[] values = new float[16];
            bool[] known = new bool[16];
            // Material setup BP color commands precede the konst block. The
            // first 12 x 5-byte commands at +0x20..+0x5B include repeated writes.
            // Process in order so the last write to a channel wins.
            for (int j = 0; j < 12; ++j)
            {
                int at = (int)setup + 0x20 + j * 5;
                if (b[at] != 0x61 || b[at + 1] is < 0xE0 or > 0xE7) continue;
                int reg = b[at + 1] - 0xE0;
                uint data = ((uint)b[at + 2] << 16) | ((uint)b[at + 3] << 8) | b[at + 4];
                if ((data & 0x800000) != 0) continue; // These are K-register writes.
                int rawLo = (int)(data & 0x7FF);
                int rawHi = (int)((data >> 12) & 0x7FF);
                static float DecodeS10(int v) => ((v & 0x400) != 0 ? v - 0x800 : v) / 255f;
                int slot = (reg / 2) * 4;
                // Even register: R,A; odd register: B,G (same BP channel
                // ordering as material KONST). Do not clamp signed TEV regs.
                int a = slot + ((reg & 1) == 0 ? 0 : 2);
                int c = slot + ((reg & 1) == 0 ? 3 : 1);
                values[a] = DecodeS10(rawLo);
                values[c] = DecodeS10(rawHi);
                known[a] = known[c] = true;
            }
            result[i] = new InitialRegisters(values, known);
        }
        return result;
    }

    // GX color-channel 0 state. MDL0 v11 stores its first channel near the
    // end of the material's fixed header. Values are big-endian RGBA8 and
    // XF-style channel controls; bit 0 selects the vertex material source,
    // bit 1 enables lighting. Decode but never manufacture missing light data.
    internal sealed record RasterChannel0(float[] MaterialRgba, float[] AmbientRgba,
        uint ColorControl, uint AlphaControl)
    {
        internal bool ColorLightingEnabled => (ColorControl & 2u) != 0;
        internal bool AlphaLightingEnabled => (AlphaControl & 2u) != 0;
        internal bool ColorMaterialFromVertex => (ColorControl & 1u) != 0;
        internal bool AlphaMaterialFromVertex => (AlphaControl & 1u) != 0;
        internal bool IsUnlit => !ColorLightingEnabled && !AlphaLightingEnabled;
        internal int MaterialRegisterComponents =>
            (ColorMaterialFromVertex ? 0 : 3) + (AlphaMaterialFromVertex ? 0 : 1);
    }

    internal static IReadOnlyDictionary<int, RasterChannel0> ReadRasterChannel0(
        ReadOnlySpan<byte> b, int mdl, uint mdlLength)
    {
        Dictionary<int, RasterChannel0> result = [];
        long end = (long)mdl + mdlLength;
        long sectionField = (long)mdl + 0x10 + 8 * 4;
        if (mdl < 0 || end > b.Length || sectionField + 4 > end) return result;
        int relative = ReadI32(b, (int)sectionField);
        long group = (long)mdl + relative;
        if (relative <= 0 || group + 8 > end) return result;
        uint count = ReadU32(b, (int)group + 4);
        if (count > 4096 || group + 8 + (count + 1) * 16L > end) return result;
        for (int i = 0; i < count; i++)
        {
            long entry = group + 8 + (i + 1) * 16L;
            long mat = group + ReadU32(b, (int)entry + 12);
            if (mat < mdl || mat + 0x404 > end) continue;
            uint size = ReadU32(b, (int)mat);
            if (size < 0x404 || mat + size > end) continue;
            int at = (int)mat;
            static float[] Unpack(uint rgba) => [
                ((rgba >> 24) & 255) / 255f, ((rgba >> 16) & 255) / 255f,
                ((rgba >> 8) & 255) / 255f, (rgba & 255) / 255f];
            result[i] = new RasterChannel0(
                Unpack(ReadU32(b, at + 0x3F4)),
                Unpack(ReadU32(b, at + 0x3F8)),
                ReadU32(b, at + 0x3FC), ReadU32(b, at + 0x400));
        }
        return result;
    }

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
