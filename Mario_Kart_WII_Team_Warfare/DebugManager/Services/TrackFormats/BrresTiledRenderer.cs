using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DebugManager.Services.TrackFormats;

/// <summary>Produces WPF meshes clipped to UV tile boundaries. Never changes BRRES geometry.</summary>
public static class BrresTiledRenderer
{
    private readonly record struct Corner(Point3D P, Point UV);
    public sealed record Result(Model3DGroup Models, int TexturedPolygons, int FallbackPolygons, int TileTriangles,
        int GeneratorPolygons, int SrtPolygons, int LegacyUvPolygons,
        int TevBakedPolygons, int TevFallbackPolygons, IReadOnlyList<string> TevReasons);
    private const int MaxTilesPerTriangle = 256;
    private const int MaxOutputTriangles = 200000;

    public static Result Build(BrresModelReader.DecodeResult source)
    {
        Model3DGroup group = new();
        Dictionary<(string Name, int U, int V), MeshGeometry3D> textured = [];
        MeshGeometry3D fallback = new();
        Dictionary<string, BrresMaterialDiagnostics.TexturePixels> bakedTextures = [];
        Dictionary<int, (BrresMaterialDiagnostics.TexturePixels? Texture, string Reason)> bakeCache = [];
        Dictionary<string, int> tevReasons = [];
        int tevBakedPolygons = 0, tevFallbackPolygons = 0;
        int texturedPolygons = 0, fallbackPolygons = 0, tiledTriangles = 0;
        int generatorPolygons = 0, srtPolygons = 0, legacyUvPolygons = 0;
        foreach (var polygon in source.PolygonSurfaces)
        {
            bool valid = polygon.Uvs is { } uvs && uvs.Count == polygon.Corners.Count &&
                source.SurfaceMaterials.TryGetValue(polygon.ObjectIndex, out var mat) &&
                mat.TextureName is { } name && source.TexturePixels.ContainsKey(name) &&
                mat.WrapU is >= 0 and <= 2 && mat.WrapV is >= 0 and <= 2;
            if (!valid || tiledTriangles >= MaxOutputTriangles)
            {
                AddFallback(fallback, polygon.Corners);
                fallbackPolygons++;
                continue;
            }
            var material = source.SurfaceMaterials[polygon.ObjectIndex];
            bool resolved = BrresTexCoordGenerators.TryResolveFirstLayerUvs(
                polygon,
                source.TexCoordGenerators.GetValueOrDefault(material.MaterialIndex),
                source.TevShaders.GetValueOrDefault(material.MaterialIndex),
                out var resolvedUvs, out bool hasSrt);
            // Keep the known-working first-layer UV0 fallback if GX evaluation
            // is unsupported. Never alter geometry or the UV array in DecodeResult.
            var activeUvs = resolved && resolvedUvs is not null ? resolvedUvs : polygon.Uvs!;
            string activeTextureName = material.TextureName!;
            // Step 6d first pass: precombine only TEV stages that can share the
            // first layer's exact generated UVs. No changes to vertex geometry.
            if (resolved && source.TevShaders.TryGetValue(material.MaterialIndex, out var tevShader)
                && tevShader.Stages.Count > 1)
            {
                if (!bakeCache.TryGetValue(material.MaterialIndex, out var cached))
                {
                    var mapZero = tevShader.Stages.FirstOrDefault(stage => stage.TextureEnabled && stage.TextureMap == 0);
                    if (mapZero is null)
                        cached = (null, "map 0 is not sampled by any TEV stage");
                    else
                    {
                        bool ok = BrresTevCompositor.TryBake(tevShader,
                            source.TevLayers.GetValueOrDefault(material.MaterialIndex),
                            source.TexturePixels, mapZero.TexCoord, out var baked, out string reason);
                        cached = (ok ? baked : null, reason);
                    }
                    bakeCache[material.MaterialIndex] = cached;
                }
                if (cached.Texture is not null)
                {
                    activeTextureName = $"#tev-material-{material.MaterialIndex}";
                    bakedTextures[activeTextureName] = cached.Texture;
                    tevBakedPolygons++;
                }
                else
                {
                    tevFallbackPolygons++;
                    tevReasons[cached.Reason] = tevReasons.GetValueOrDefault(cached.Reason) + 1;
                }
            }
            var key = (activeTextureName, material.WrapU, material.WrapV);
            if (!textured.TryGetValue(key, out var mesh)) textured[key] = mesh = new MeshGeometry3D();
            int originalCount = mesh.TriangleIndices.Count;
            bool success = true;
            for (int i = 0; i + 2 < polygon.Corners.Count; i += 3)
            {
                Corner a = new(polygon.Corners[i], activeUvs[i]);
                Corner b = new(polygon.Corners[i + 1], activeUvs[i + 1]);
                Corner c = new(polygon.Corners[i + 2], activeUvs[i + 2]);
                if (!AddTiledTriangle(mesh, a, b, c, material.WrapU, material.WrapV, ref tiledTriangles))
                {
                    success = false;
                    break;
                }
            }
            if (!success)
            {
                // Roll back partial output and preserve the complete grey polygon.
                while (mesh.TriangleIndices.Count > originalCount) mesh.TriangleIndices.RemoveAt(mesh.TriangleIndices.Count - 1);
                int vertices = originalCount; // each output corner is a unique mesh vertex
                while (mesh.Positions.Count > vertices) mesh.Positions.RemoveAt(mesh.Positions.Count - 1);
                while (mesh.TextureCoordinates.Count > vertices) mesh.TextureCoordinates.RemoveAt(mesh.TextureCoordinates.Count - 1);
                AddFallback(fallback, polygon.Corners);
                fallbackPolygons++;
            }
            else
            {
                texturedPolygons++;
                if (resolved) { generatorPolygons++; if (hasSrt) srtPolygons++; }
                else legacyUvPolygons++;
            }
        }
        foreach (var pair in textured)
        {
            if (pair.Value.TriangleIndices.Count == 0) continue;
            var pixels = bakedTextures.TryGetValue(pair.Key.Name, out var baked)
                ? baked : source.TexturePixels[pair.Key.Name];
            byte[] bgra = new byte[pixels.Rgba.Length];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = pixels.Rgba[i + 2]; bgra[i + 1] = pixels.Rgba[i + 1];
                bgra[i + 2] = pixels.Rgba[i]; bgra[i + 3] = pixels.Rgba[i + 3];
            }
            var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96,
                PixelFormats.Bgra32, null, bgra, pixels.Width * 4);
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            brush.Freeze();
            Material material = new DiffuseMaterial(brush);
            pair.Value.Freeze();
            group.Children.Add(new GeometryModel3D(pair.Value, material) { BackMaterial = material });
        }
        if (fallback.TriangleIndices.Count > 0)
        {
            fallback.Freeze();
            Material grey = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(155, 163, 168)));
            group.Children.Add(new GeometryModel3D(fallback, grey) { BackMaterial = grey });
        }
        return new Result(group, texturedPolygons, fallbackPolygons, tiledTriangles,
            generatorPolygons, srtPolygons, legacyUvPolygons,
            tevBakedPolygons, tevFallbackPolygons,
            tevReasons.OrderByDescending(p => p.Value).Take(6)
                .Select(p => $"{p.Key}: {p.Value} polygons").ToArray());
    }

    private static void AddFallback(MeshGeometry3D mesh, IReadOnlyList<Point3D> corners)
    {
        foreach (var p in corners)
        {
            int index = mesh.Positions.Count;
            mesh.Positions.Add(p);
            mesh.TriangleIndices.Add(index);
        }
    }

    private static bool AddTiledTriangle(MeshGeometry3D mesh, Corner a, Corner b, Corner c,
        int wrapU, int wrapV, ref int total)
    {
        if (!Finite(a.UV) || !Finite(b.UV) || !Finite(c.UV)) return false;
        double minU = Math.Min(a.UV.X, Math.Min(b.UV.X, c.UV.X));
        double maxU = Math.Max(a.UV.X, Math.Max(b.UV.X, c.UV.X));
        double minV = Math.Min(a.UV.Y, Math.Min(b.UV.Y, c.UV.Y));
        double maxV = Math.Max(a.UV.Y, Math.Max(b.UV.Y, c.UV.Y));
        int u0 = wrapU == 0 ? 0 : (int)Math.Floor(minU);
        int u1 = wrapU == 0 ? 0 : (int)Math.Floor(maxU);
        int v0 = wrapV == 0 ? 0 : (int)Math.Floor(minV);
        int v1 = wrapV == 0 ? 0 : (int)Math.Floor(maxV);
        if ((long)(u1 - u0 + 1) * (v1 - v0 + 1) > MaxTilesPerTriangle) return false;
        for (int v = v0; v <= v1; v++)
        for (int u = u0; u <= u1; u++)
        {
            List<Corner> clipped = [a, b, c];
            if (wrapU != 0) { clipped = Clip(clipped, 0, u, true); clipped = Clip(clipped, 0, u + 1, false); }
            if (wrapV != 0) { clipped = Clip(clipped, 1, v, true); clipped = Clip(clipped, 1, v + 1, false); }
            for (int i = 1; i + 1 < clipped.Count; i++)
            {
                if (total >= MaxOutputTriangles) return false;
                Emit(mesh, clipped[0], u, v, wrapU, wrapV);
                Emit(mesh, clipped[i], u, v, wrapU, wrapV);
                Emit(mesh, clipped[i + 1], u, v, wrapU, wrapV);
                total++;
            }
        }
        return true;
    }

    private static bool Finite(Point p) => double.IsFinite(p.X) && double.IsFinite(p.Y) &&
        Math.Abs(p.X) < 100000 && Math.Abs(p.Y) < 100000;

    private static List<Corner> Clip(List<Corner> input, int axis, double edge, bool greater)
    {
        List<Corner> output = [];
        if (input.Count == 0) return output;
        Corner previous = input[^1];
        bool previousInside = Inside(previous);
        foreach (Corner current in input)
        {
            bool currentInside = Inside(current);
            if (currentInside != previousInside)
            {
                double before = Value(previous), after = Value(current);
                double t = (edge - before) / (after - before);
                output.Add(new Corner(new Point3D(
                    previous.P.X + (current.P.X - previous.P.X) * t,
                    previous.P.Y + (current.P.Y - previous.P.Y) * t,
                    previous.P.Z + (current.P.Z - previous.P.Z) * t),
                    new Point(previous.UV.X + (current.UV.X - previous.UV.X) * t,
                              previous.UV.Y + (current.UV.Y - previous.UV.Y) * t)));
            }
            if (currentInside) output.Add(current);
            previous = current;
            previousInside = currentInside;
        }
        return output;
        double Value(Corner p) => axis == 0 ? p.UV.X : p.UV.Y;
        bool Inside(Corner p) => greater ? Value(p) >= edge - 1e-9 : Value(p) <= edge + 1e-9;
    }

    private static void Emit(MeshGeometry3D mesh, Corner corner, int u, int v, int wrapU, int wrapV)
    {
        double x = wrapU == 0 ? Math.Clamp(corner.UV.X, 0, 1) : corner.UV.X - u;
        double y = wrapV == 0 ? Math.Clamp(corner.UV.Y, 0, 1) : corner.UV.Y - v;
        if (wrapU == 2 && (u & 1) != 0) x = 1 - x;
        if (wrapV == 2 && (v & 1) != 0) y = 1 - y;
        int index = mesh.Positions.Count;
        mesh.Positions.Add(corner.P);
        mesh.TextureCoordinates.Add(new Point(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
        mesh.TriangleIndices.Add(index);
    }
}
