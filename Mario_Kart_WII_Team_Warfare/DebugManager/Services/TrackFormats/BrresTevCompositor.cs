using System.Windows;

namespace DebugManager.Services.TrackFormats;

/// <summary>
/// Step 6d (first pass): conservative, CPU-side TEV texture baking for shaders
/// whose texture reads share one generated UV mapping. Unsupported GX inputs
/// never get approximated; callers retain the tested Step 6c rendering path.
/// The bake is deliberately independent of WPF so it can be unit tested.
/// </summary>
internal static class BrresTevCompositor
{
    internal sealed record Layer(string TextureName, int WrapU, int WrapV);
    internal sealed record Baked(BrresMaterialDiagnostics.TexturePixels Texture, string Reason);

    private readonly record struct C(double R, double G, double B, double A)
    {
        internal static C Zero => new(0, 0, 0, 0);
        internal static C One => new(1, 1, 1, 1);
    }

    // A material is eligible only if every texture-enabled stage addresses
    // the same generator as the first layer currently rendered by Step 6c.
    // Different source UVs, effect matrices and indirect stages require a real
    // per-fragment renderer and are intentionally deferred.
    internal static bool TryBake(
        BrresTevDiagnostics.Shader? shader,
        IReadOnlyList<Layer>? layers,
        IReadOnlyDictionary<string, BrresMaterialDiagnostics.TexturePixels> textures,
        int referenceTexCoord,
        out BrresMaterialDiagnostics.TexturePixels? output,
        out string reason)
    {
        output = null;
        reason = "";
        if (shader is null || shader.Problem is not null || shader.Stages.Count == 0)
            return Fail("shader unavailable", out reason);
        if (layers is null || layers.Count == 0)
            return Fail("texture layers unavailable", out reason);
        if (shader.Stages.Count > 16) return Fail("too many stages", out reason);

        foreach (var stage in shader.Stages)
        {
            if (stage.IndirectCommand != 0) return Fail("indirect TEV", out reason);
            if (stage.TextureEnabled)
            {
                if (stage.TexCoord != referenceTexCoord) return Fail("multiple TEV coordinate generators", out reason);
                if (stage.TextureMap < 0 || stage.TextureMap >= layers.Count ||
                    !textures.ContainsKey(layers[stage.TextureMap].TextureName))
                    return Fail("unavailable TEV texture map", out reason);
            }
            if (stage.RasterSwap != 0 || stage.TextureSwap != 0)
                return Fail("TEV channel swap", out reason);
            if (stage.ColorBias == 3 || stage.AlphaBias == 3)
                return Fail("TEV compare operation", out reason);
            if (stage.ColorScale is < 0 or > 3 || stage.AlphaScale is < 0 or > 3)
                return Fail("invalid TEV scale", out reason);
        }

        // Check input dependencies before allocating any image data. GX regs
        // are undefined here until the BP material register initializer is
        // decoded; consuming them would silently corrupt the entire material.
        bool[] rgbWritten = new bool[4], alphaWritten = new bool[4];
        foreach (var stage in shader.Stages)
        {
            bool hasTexture = stage.TextureEnabled;
            foreach (int arg in new[] {stage.ColorA, stage.ColorB, stage.ColorC, stage.ColorD})
                if (!ValidColorArgument(arg, hasTexture, rgbWritten, alphaWritten))
                    return Fail($"uninitialized/unsupported color input {arg} at stage {stage.Index}", out reason);
            foreach (int arg in new[] {stage.AlphaA, stage.AlphaB, stage.AlphaC, stage.AlphaD})
                if (!ValidAlphaArgument(arg, hasTexture, alphaWritten))
                    return Fail($"uninitialized/unsupported alpha input {arg} at stage {stage.Index}", out reason);
            rgbWritten[stage.ColorDestination] = true;
            alphaWritten[stage.AlphaDestination] = true;
        }
        if (!rgbWritten[0] || !alphaWritten[0])
            return Fail("final TEV output is not in prev register", out reason);

        // The compositor uses the first-layer coordinate as its texture-space
        // domain. Keep bake sizes bounded to avoid excessive memory use.
        var first = layers[0];
        if (!textures.TryGetValue(first.TextureName, out var source))
            return Fail("first texture missing", out reason);
        int width = Math.Min(source.Width, 1024), height = Math.Min(source.Height, 1024);
        if (width <= 0 || height <= 0) return Fail("invalid output dimensions", out reason);
        byte[] rgba = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double u = (x + 0.5) / width, v = (y + 0.5) / height;
            C[] regs = new C[4]; // prev, reg0, reg1, reg2
            foreach (var stage in shader.Stages)
            {
                C tex = C.Zero;
                if (stage.TextureEnabled)
                {
                    Layer layer = layers[stage.TextureMap];
                    tex = Sample(textures[layer.TextureName], u, v, layer.WrapU, layer.WrapV);
                }
                C col = new(
                    EvalColor(stage.ColorA, stage.ColorB, stage.ColorC, stage.ColorD, 0, tex, regs, stage),
                    EvalColor(stage.ColorA, stage.ColorB, stage.ColorC, stage.ColorD, 1, tex, regs, stage),
                    EvalColor(stage.ColorA, stage.ColorB, stage.ColorC, stage.ColorD, 2, tex, regs, stage), 0);
                double alpha = EvalAlpha(stage, tex, regs);
                var cd = stage.ColorDestination;
                var ad = stage.AlphaDestination;
                regs[cd] = regs[cd] with { R = col.R, G = col.G, B = col.B };
                regs[ad] = regs[ad] with { A = alpha };
            }
            int at = (y * width + x) * 4;
            rgba[at] = Byte(regs[0].R);
            rgba[at + 1] = Byte(regs[0].G);
            rgba[at + 2] = Byte(regs[0].B);
            rgba[at + 3] = Byte(regs[0].A);
        }
        output = new BrresMaterialDiagnostics.TexturePixels(width, height, rgba);
        reason = "supported shared-UV TEV";
        return true;
    }

    private static bool Fail(string why, out string reason) { reason = why; return false; }
    private static bool ValidColorArgument(int arg, bool tex, bool[] rgb, bool[] a) => arg switch
    {
        0 => rgb[0], 1 => a[0], 2 => rgb[1], 3 => a[1],
        4 => rgb[2], 5 => a[2], 6 => rgb[3], 7 => a[3],
        8 or 9 => tex,
        12 or 13 or 15 => true, // ONE, HALF, ZERO
        _ => false // raster (10/11) and KONST (14) not yet available
    };
    private static bool ValidAlphaArgument(int arg, bool tex, bool[] a) => arg switch
    {
        0 => a[0], 1 => a[1], 2 => a[2], 3 => a[3],
        4 => tex, 7 => true,
        _ => false // raster alpha, KONST
    };

    private static double ColorArg(int id, int ch, C tex, C[] regs) => id switch
    {
        0 or 2 or 4 or 6 => Channel(regs[id / 2], ch),
        1 or 3 or 5 or 7 => regs[id / 2].A,
        8 => Channel(tex, ch), 9 => tex.A,
        12 => 1, 13 => 0.5, 15 => 0,
        _ => 0
    };
    private static double AlphaArg(int id, C tex, C[] regs) => id switch
    {
        0 => regs[0].A, 1 => regs[1].A, 2 => regs[2].A,
        3 => regs[3].A, 4 => tex.A, 7 => 0,
        _ => 0
    };
    private static double Channel(C c, int ch) => ch switch { 0 => c.R, 1 => c.G, _ => c.B };

    private static double EvalColor(int a, int b, int c, int d, int ch,
        C texture, C[] regs, BrresTevDiagnostics.Stage stage) =>
        Combine(ColorArg(a, ch, texture, regs), ColorArg(b, ch, texture, regs),
            ColorArg(c, ch, texture, regs), ColorArg(d, ch, texture, regs),
            stage.ColorBias, stage.ColorSubtract, stage.ColorScale, stage.ColorClamp);
    private static double EvalAlpha(BrresTevDiagnostics.Stage stage, C texture, C[] regs) =>
        Combine(AlphaArg(stage.AlphaA, texture, regs), AlphaArg(stage.AlphaB, texture, regs),
            AlphaArg(stage.AlphaC, texture, regs), AlphaArg(stage.AlphaD, texture, regs),
            stage.AlphaBias, stage.AlphaSubtract, stage.AlphaScale, stage.AlphaClamp);

    private static double Combine(double a, double b, double c, double d,
        int bias, bool subtract, int scale, bool clamp)
    {
        double interp = a * (1 - c) + b * c;
        double value = d + (subtract ? -interp : interp);
        value += bias switch { 1 => 0.5, 2 => -0.5, _ => 0 };
        value *= scale switch { 1 => 2, 2 => 4, 3 => 0.5, _ => 1 };
        return Math.Clamp(value, clamp ? 0 : -4, clamp ? 1 : 4);
    }

    private static C Sample(BrresMaterialDiagnostics.TexturePixels tex,
        double u, double v, int wrapU, int wrapV)
    {
        u = Wrap(u, wrapU); v = Wrap(v, wrapV);
        int x = Math.Clamp((int)(u * tex.Width), 0, tex.Width - 1);
        int y = Math.Clamp((int)(v * tex.Height), 0, tex.Height - 1);
        int i = (y * tex.Width + x) * 4;
        return new C(tex.Rgba[i] / 255.0, tex.Rgba[i + 1] / 255.0,
            tex.Rgba[i + 2] / 255.0, tex.Rgba[i + 3] / 255.0);
    }
    private static double Wrap(double v, int mode)
    {
        if (mode == 0) return Math.Clamp(v, 0, 1);
        double tile = Math.Floor(v);
        double fract = v - tile;
        return mode == 2 && Math.Abs(tile % 2) > 0.5 ? 1 - fract : fract;
    }
    private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
}
