using System.Buffers.Binary;

namespace DebugManager.Services.TrackFormats;

/// <summary>
/// Read-only GX indirect-texture commands found in MDL0 v11 material/shader records.
/// This is source data for a future GPU implementation, not an indirect shader emulator.
/// Raw BP payloads are preserved for comparison with BrawlCrate/Dolphin.
/// </summary>
internal static class BrresIndirectTevDiagnostics
{
    internal sealed record Source(int Stage, int TextureMap, int TexCoord);
    internal sealed record Scale(int Stage, int SExponent, int TExponent)
    {
        internal float SFactor => 1f / (1 << SExponent);
        internal float TFactor => 1f / (1 << TExponent);
    }
    internal sealed record MatrixCommand(int Register, uint Raw, int Coefficient0, int Coefficient1, int ExponentBits);
    internal sealed record Matrix(int Index, IReadOnlyList<MatrixCommand> Commands)
    {
        internal bool Complete => Commands.Count == 3;
    }
    internal sealed record Stage(int Index, uint Raw, int IndirectStage, int Format,
        int BiasSelection, int AlphaSelection, int MatrixSelection, int WrapS,
        int WrapT, bool UseOriginalLod, bool AddPrevious)
    {
        internal bool Active => Raw != 0;
    }
    internal sealed record Material(int MaterialIndex, uint? ReferenceRaw,
        IReadOnlyList<Source> Sources, IReadOnlyList<Scale> Scales,
        IReadOnlyList<Matrix> Matrices, IReadOnlyList<Stage> Stages,
        IReadOnlyList<string> Warnings)
    {
        internal bool UsesIndirect => Stages.Any(s => s.Active);
    }

    internal static IReadOnlyDictionary<int, Material> Read(
        ReadOnlySpan<byte> data, int mdl, uint mdlLength,
        IReadOnlyDictionary<int, BrresTevDiagnostics.Shader> shaders)
    {
        var result = new Dictionary<int, Material>();
        long end = (long)mdl + mdlLength;
        long sectionField = (long)mdl + 0x10 + 8 * 4;
        if (mdl < 0 || end > data.Length || sectionField + 4 > end) return result;
        int rel = I32(data, (int)sectionField);
        long group = (long)mdl + rel;
        if (rel <= 0 || group + 8 > end) return result;
        uint count = U32(data, (int)group + 4);
        if (count > 4096 || group + 8 + (count + 1) * 16L > end) return result;

        for (int id = 0; id < count; id++)
        {
            long entry = group + 8 + (id + 1) * 16L;
            long mat = group + U32(data, (int)entry + 12);
            if (mat < mdl || mat + 0x40 > end) continue;
            uint size = U32(data, (int)mat);
            int setupRel = I32(data, (int)mat + 0x3C);
            long setup = mat + setupRel;
            if (size < 0x180 || setupRel <= 0 || setup < mat + 0x40 ||
                setup + 0x180 > mat + size || setup + 0x180 > end) continue;

            List<string> warnings = [];
            List<Scale> scales = [];
            // MDL0 material's BP display list: RAS1_SS0/SS1 at +0xA0/+0xA5.
            for (int pair = 0; pair < 2; pair++)
            {
                int at = (int)setup + 0xA0 + pair * 5;
                if (!TryBp(data, at, (byte)(0x25 + pair), out uint raw))
                {
                    warnings.Add($"missing RAS1_SS{pair}");
                    continue;
                }
                scales.Add(new Scale(pair * 2, (int)(raw & 15), (int)((raw >> 4) & 15)));
                scales.Add(new Scale(pair * 2 + 1, (int)((raw >> 8) & 15), (int)((raw >> 12) & 15)));
            }

            // The material ind-matrix block follows the two scale commands.
            // A register is 'known' ONLY if its BP opcode and address match.
            // Do not turn absent matrices into invented zero/identity matrices.
            List<Matrix> matrices = [];
            for (int matrix = 0; matrix < 3; matrix++)
            {
                List<MatrixCommand> commands = [];
                for (int part = 0; part < 3; part++)
                {
                    int reg = 0x06 + matrix * 3 + part;
                    // MDL0 often omits unused matrix groups, so locate by address
                    // within the fixed indirect matrix setup region (5-byte aligned).
                    for (int at = (int)setup + 0xAA; at + 5 <= setup + 0x180; at += 5)
                    {
                        if (!TryBp(data, at, (byte)reg, out uint raw)) continue;
                        int a = Sign11((int)(raw & 0x7FF));
                        int b = Sign11((int)((raw >> 11) & 0x7FF));
                        commands.Add(new MatrixCommand(reg, raw, a, b, (int)((raw >> 22) & 3)));
                        break;
                    }
                }
                if (commands.Count > 0)
                {
                    if (commands.Count != 3) warnings.Add($"incomplete IND_MTX{matrix} ({commands.Count}/3 commands)");
                    matrices.Add(new Matrix(matrix, commands));
                }
            }

            uint? reference = null;
            List<Source> sources = [];
            List<Stage> stages = [];
            if (shaders.TryGetValue(id, out var shader))
            {
                long sh = shader.ShaderOffset;
                // MDL0Shader stores RAS1_IREF at offset +0x70, after swap commands.
                if (sh >= mdl && sh + 0x75 <= end &&
                    TryBp(data, (int)sh + 0x70, 0x27, out uint iref))
                {
                    reference = iref;
                    for (int i = 0; i < 4; i++)
                        sources.Add(new Source(i, (int)((iref >> (i * 6)) & 7),
                            (int)((iref >> (i * 6 + 3)) & 7)));
                }
                foreach (var s in shader.Stages)
                {
                    uint raw = (uint)s.IndirectCommand & 0xFFFFFFu;
                    stages.Add(new Stage(s.Index, raw, (int)(raw & 3),
                        (int)((raw >> 2) & 3), (int)((raw >> 4) & 7),
                        (int)((raw >> 7) & 3), (int)((raw >> 9) & 15),
                        (int)((raw >> 13) & 7), (int)((raw >> 16) & 7),
                        ((raw >> 19) & 1) != 0, ((raw >> 20) & 1) != 0));
                }
                if (stages.Any(s => s.Active) && reference is null)
                    warnings.Add("active indirect command without a valid RAS1_IREF");
                foreach (var s in stages.Where(x => x.Active))
                {
                    if (reference is null) continue;
                    var src = sources[s.IndirectStage];
                    if (src.TextureMap > 7 || src.TexCoord > 7)
                        warnings.Add($"stage {s.Index} has invalid indirect source");
                }
            }
            result[id] = new Material(id, reference, sources, scales, matrices, stages, warnings);
        }
        return result;
    }

    private static int Sign11(int v) => (v & 0x400) != 0 ? v - 0x800 : v;
    private static bool TryBp(ReadOnlySpan<byte> b, int at, byte register, out uint payload)
    {
        payload = 0;
        if (at < 0 || at + 5 > b.Length || b[at] != 0x61 || b[at + 1] != register)
            return false;
        payload = ((uint)b[at + 2] << 16) | ((uint)b[at + 3] << 8) | b[at + 4];
        return true;
    }
    private static int I32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32BigEndian(b.Slice(at, 4));
    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(at, 4));
}
