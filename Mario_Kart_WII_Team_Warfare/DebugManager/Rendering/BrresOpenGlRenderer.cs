using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using DebugManager.Services.TrackFormats;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using GlVector3 = OpenTK.Mathematics.Vector3;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace DebugManager.Rendering;

/// <summary>
/// First OpenGL milestone: GPU geometry and first-layer textures. No TEV is approximated.
/// All GL operations run while GLControl owns the current context.
/// </summary>
internal sealed class BrresOpenGlRenderer
{
    // GameManager may launch DebugManager without a separate console.
    // Emit to the parent console when attached and retain a file log either way.
    private static void WriteDiagnostic(string message)
    {
        try { Console.WriteLine("[DebugManager] " + message); } catch { }
        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "DebugManager-OpenGL-startup.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    
    private sealed record GpuBatch(int Vao, int Vbo, int Count, int Texture, bool Translucent, GlVector3 Center);
    private readonly record struct UvCorner(Point3D Position, System.Windows.Point Uv);
    private const int MaxTilesPerTriangle = 256;
    private const int MaxOutputTrianglesPerSurface = 200000;
    private BrresModelReader.DecodeResult? _requested;
    private BrresModelReader.DecodeResult? _uploaded;
    private readonly List<GpuBatch> _batches = [];
    private readonly Dictionary<(string, int, int), int> _textures = [];
    private int _program;
    private int _matrixLocation;
    private int _samplerLocation;
    private int _texturedLocation;
    private int _whiteTexture;
    // Diagnostic parity mode. Uses the same already-proven mesh and ImageBrush
    // output as the WPF viewer, then renders it with OpenGL. Disable this after
    // comparing the two views and implementing shared graphics-neutral batches.
    private const bool UseWpfParitySource = true;

    // F8: 0 = verified baseline, 1/2 = raw stage input, 3 = experimental two-stage TEV equations.
    // Mode 3 still approximates raster lighting and GX post-texture matrices.
    internal int TevInputPreviewMode { get; set; }
    private sealed record TevInputBatch(int Vao, int Vbo, int VertexCount,
        int Texture0, int Texture1, int MaterialIndex, int Stage0, int Stage1,
        BrresTevDiagnostics.Stage Equation0, BrresTevDiagnostics.Stage Equation1,
        BrresTevDiagnostics.MaterialKonst? Constants,
        BrresTevDiagnostics.InitialRegisters? InitialRegisters,
        BrresTevDiagnostics.RasterChannel0? RasterChannel,
        bool HasVertexRaster, bool EquationSupported);
    private readonly List<TevInputBatch> _tevInputBatches = [];
    private int _tevInputProgram;
    private int _tevInputMatrix;
    private int _tevInputMode;
    private int _tevInputSampler0;
    private int _tevInputSampler1;
    private readonly int[] _tevColorArgs = new int[2];
    private readonly int[] _tevAlphaArgs = new int[2];
    private readonly int[] _tevColorSettings = new int[2];
    private readonly int[] _tevAlphaSettings = new int[2];
    private readonly int[] _tevColorDest = new int[2];
    private readonly int[] _tevAlphaDest = new int[2];
    private readonly int[] _tevKonstColorFraction = new int[2];
    private readonly int[] _tevKonstAlphaFraction = new int[2];
    private readonly int[] _tevKonstRegisters = new int[4];
    private readonly int[] _tevInitialRegisters = new int[4];
    private int _tevUseVertexRaster;
    private int _tevRasterMaterialColor;
    private int _tevRasterSourceFlags;



    public void SetModel(BrresModelReader.DecodeResult? model) => _requested = model;

    public void Draw(BrresModelReader.DecodeResult? model, int width, int height,
        double yawDegrees, double pitchDegrees, double distance, Point3D target)
    {
        if (width <= 0 || height <= 0) return;
        if (!ReferenceEquals(_requested, model)) _requested = model;
        if (_program == 0) Initialize();
        if (!ReferenceEquals(_uploaded, _requested))
        {
            ReleaseModel();
            if (_requested is not null)
            {
                Upload(_requested); // Verified first-layer baseline stays unchanged.
                UploadTevInputBatches(_requested);
            }
            _uploaded = _requested;
        }

        GL.Viewport(0, 0, width, height);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Disable(EnableCap.CullFace); // GX double-sided materials until culling states are parsed
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.ClearColor(0.075f, 0.1f, 0.13f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (_requested is null) return;

        float yaw = MathHelper.DegreesToRadians((float)yawDegrees);
        float pitch = MathHelper.DegreesToRadians((float)Math.Clamp(pitchDegrees, -80, 80));
        float d = (float)distance;
        var center = new GlVector3((float)target.X, (float)target.Y, (float)target.Z);
        var camera = center + new GlVector3(d * MathF.Cos(pitch) * MathF.Sin(yaw),
            d * MathF.Sin(pitch), d * MathF.Cos(pitch) * MathF.Cos(yaw));
        Matrix4 view = Matrix4.LookAt(camera, center, GlVector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f),
            Math.Max(0.01f, (float)width / height), 0.01f, Math.Max(10000, d * 100));
        // OpenTK uses row-vector matrices: view * projection transforms a row position.
        // GLSL uses column vectors, so upload the transposed matrix representation.
        // OpenTK stores matrices row-major; transpose=false makes OpenGL interpret
        // these bytes as column-major, yielding (view * projection)^T in GLSL.
        Matrix4 combined = view * projection;
        GL.UseProgram(_program);
        GL.UniformMatrix4(_matrixLocation, false, ref combined);
        GL.Uniform1(_samplerLocation, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        // Opaque and alpha-cutout geometry writes depth as usual.
        // Partially transparent geometry must be drawn back-to-front with
        // depth writes disabled or its invisible portions occlude the scene.
        GL.DepthMask(true);
        foreach (GpuBatch batch in _batches)
            if (!batch.Translucent) DrawBatch(batch);

        GL.DepthMask(false);
        try
        {
            foreach (GpuBatch batch in _batches.Where(b => b.Translucent)
                .OrderByDescending(b => (b.Center - camera).LengthSquared))
                DrawBatch(batch);
        }
        finally
        {
            GL.DepthMask(true);
            GL.BindVertexArray(0);
        }
        if (TevInputPreviewMode != 0)
            DrawTevInputPreview(combined);
    }

    private void DrawBatch(GpuBatch batch)
    {
        GL.Uniform1(_texturedLocation, batch.Texture != _whiteTexture ? 1 : 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, batch.Texture);
        GL.BindVertexArray(batch.Vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, batch.Count);
    }

    private void Upload(BrresModelReader.DecodeResult model)
    {
        if (UseWpfParitySource)
        {
            UploadFromWpfParitySource(model);
            return;
        }
        double cx = (model.Minimum.X + model.Maximum.X) / 2;
        double cy = (model.Minimum.Y + model.Maximum.Y) / 2;
        double cz = (model.Minimum.Z + model.Maximum.Z) / 2;
        foreach (var surface in model.PolygonSurfaces)
        {
            model.SurfaceMaterials.TryGetValue(surface.ObjectIndex, out var material);
            bool usable = material?.TextureName is string name && model.TexturePixels.ContainsKey(name) &&
                material.WrapU is >= 0 and <= 2 && material.WrapV is >= 0 and <= 2;
            IReadOnlyList<System.Windows.Point>? uv = surface.Uvs;
            if (usable && material is not null)
            {
                bool resolved = BrresTexCoordGenerators.TryResolveFirstLayerUvs(surface,
                    model.TexCoordGenerators.GetValueOrDefault(material.MaterialIndex),
                    model.TevShaders.GetValueOrDefault(material.MaterialIndex), out var generated, out _);
                if (resolved && generated is not null) uv = generated;
            }
            if (uv?.Count != surface.Corners.Count) usable = false;
            // WPF clips each original triangle to UV tile boundaries. Mirror that
            // behaviour here so the first-layer OpenGL baseline matches Step 6c.
            // In particular, a triangle crossing a repeat/mirror boundary must
            // not interpolate across the entire tile range on the GPU.
            var vertices = new List<float>(surface.Corners.Count * 5);
            bool tiled = usable && uv is not null;
            if (tiled)
            {
                for (int i = 0; i + 2 < surface.Corners.Count; i += 3)
                {
                    if (!AppendTiledTriangle(vertices,
                        new UvCorner(surface.Corners[i], uv![i]),
                        new UvCorner(surface.Corners[i + 1], uv[i + 1]),
                        new UvCorner(surface.Corners[i + 2], uv[i + 2]),
                        material!.WrapU, material.WrapV, cx, cy, cz))
                    {
                        tiled = false;
                        break;
                    }
                }
            }
            // Unsupported/oversized UV ranges retain the previous OpenGL path.
            // Avoid partially clipping any individual surface.
            if (!tiled)
            {
                vertices.Clear();
                for (int i = 0; i < surface.Corners.Count; i++)
                    EmitVertex(vertices, surface.Corners[i],
                        usable ? uv![i] : default, cx, cy, cz);
            }
            var vertexData = vertices.ToArray();
            // Clipped UVs have already been normalized to [0,1]. Clamp those
            // textures to prevent seams at the tile boundary. Non-clipped
            // surfaces still use the original Wii wrapping mode.
            int texture = usable && material is not null
                ? GetTexture(model, material.TextureName!,
                    tiled ? 0 : material.WrapU, tiled ? 0 : material.WrapV)
                : _whiteTexture;
            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertexData.Length * sizeof(float), vertexData, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float));
            bool translucent = usable && material is not null &&
                HasPartialAlpha(model.TexturePixels[material.TextureName!].Rgba);
            // Object-local batch center in the same scaled space as the VBO.
            var batchCenter = surface.Corners.Count > 0 ? new GlVector3(
                (float)((surface.Corners.Average(p => p.X) - cx) * 0.001),
                (float)((surface.Corners.Average(p => p.Y) - cy) * 0.001),
                (float)((surface.Corners.Average(p => p.Z) - cz) * 0.001)) : GlVector3.Zero;
            _batches.Add(new GpuBatch(vao, vbo, vertexData.Length / 5, texture, translucent, batchCenter));
        }
        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Temporary reference renderer: take precisely the geometry, texture
    /// coordinates, texture pixels, and first-layer/TEV fallback chosen by the
    /// known-working WPF renderer. Only projection and sampling remain OpenGL.
    /// This deliberately leaves BRRES parsing and future GX/TEV parsing intact.
    /// </summary>
    private void UploadFromWpfParitySource(BrresModelReader.DecodeResult model)
    {
        var reference = BrresTiledRenderer.Build(model);
        double cx = (model.Minimum.X + model.Maximum.X) * 0.5;
        double cy = (model.Minimum.Y + model.Maximum.Y) * 0.5;
        double cz = (model.Minimum.Z + model.Maximum.Z) * 0.5;
        int batchCount = 0;

        foreach (Model3D child in reference.Models.Children)
        {
            if (child is not GeometryModel3D geometryModel ||
                geometryModel.Geometry is not MeshGeometry3D mesh ||
                mesh.TriangleIndices.Count == 0)
                continue;

            int texture = _whiteTexture;
            bool translucent = false;
            if (geometryModel.Material is DiffuseMaterial { Brush: ImageBrush imageBrush } &&
                imageBrush.ImageSource is BitmapSource bitmap)
            {
                // WPF's Brush bitmap is BGRA32 in top-down row order. Upload
                // these very same bytes to OpenGL, avoiding a second conversion
                // or a second texture-name / pixel-array lookup.
                BitmapSource bgraSource = bitmap.Format == PixelFormats.Bgra32
                    ? bitmap
                    : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int width = bgraSource.PixelWidth;
                int height = bgraSource.PixelHeight;
                int stride = checked(width * 4);
                byte[] bgra = new byte[checked(stride * height)];
                bgraSource.CopyPixels(bgra, stride, 0);
                for (int a = 3; a < bgra.Length; a += 4)
                    if (bgra[a] is > 0 and < 255)
                    {
                        translucent = true;
                        break;
                    }

                texture = GL.GenTexture();
                // Explicit texture unit: independent of context state.
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                    width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, bgra);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                    (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                    (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                    (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                    (int)TextureWrapMode.ClampToEdge);
                _textures.Add(($"#wpf-parity-{batchCount}", 0, 0), texture);
            }

            // WPF meshes use shared vertex arrays and indexed triangles. Expand
            // the indices into the simple xyzuv format supported by this GL VBO.
            var vertices = new float[checked(mesh.TriangleIndices.Count * 5)];
            int n = 0;
            foreach (int index in mesh.TriangleIndices)
            {
                Point3D p = mesh.Positions[index];
                System.Windows.Point uv = mesh.TextureCoordinates.Count > index
                    ? mesh.TextureCoordinates[index] : default;
                vertices[n++] = (float)((p.X - cx) * 0.001);
                vertices[n++] = (float)((p.Y - cy) * 0.001);
                vertices[n++] = (float)((p.Z - cz) * 0.001);
                vertices[n++] = (float)uv.X;
                vertices[n++] = (float)uv.Y;
            }
            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer,
                vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float,
                false, 5 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float,
                false, 5 * sizeof(float), 3 * sizeof(float));

            var center = new GlVector3(
                (float)((mesh.Positions.Average(p => p.X) - cx) * 0.001),
                (float)((mesh.Positions.Average(p => p.Y) - cy) * 0.001),
                (float)((mesh.Positions.Average(p => p.Z) - cz) * 0.001));
            _batches.Add(new GpuBatch(vao, vbo, mesh.TriangleIndices.Count,
                texture, translucent, center));
            batchCount++;
        }
        GL.BindVertexArray(0);
        WriteDiagnostic($"OpenGL WPF parity: {batchCount} batches; " +
            $"{reference.TexturedPolygons} textured polygons; " +
            $"{reference.FallbackPolygons} fallbacks; " +
            $"{reference.TileTriangles} output triangles.");
    }

    /// <summary>
    /// Step 6d.1: an independently sampled pair of TEV texture inputs.
    /// This deliberately does not combine colors: the F8 preview selects
    /// stage 0 or stage 1 to validate resource binding and separate texgens.
    /// Non-direct generators and absent UV1 are skipped, not guessed.
    /// </summary>
    private void UploadTevInputBatches(BrresModelReader.DecodeResult model)
    {
        int eligible = 0, skipped = 0, approximatePostMatrix3 = 0, equationsSupported = 0;
        var equationReject = new Dictionary<string, int>(StringComparer.Ordinal);
        var rejectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rejectionDetails = new List<string>();

        void Reject(int objectIndex, int materialIndex, string reason)
        {
            skipped++;
            string category = reason.StartsWith("stage ", StringComparison.Ordinal) && reason.Contains(" UV rejected", StringComparison.Ordinal)
                ? "stage UV generator rejected" : reason;
            rejectCounts[category] = rejectCounts.GetValueOrDefault(category) + 1;
            // One example per rejected surface, bounded to keep the parent console readable.
            if (rejectionDetails.Count < 32)
                rejectionDetails.Add($"object={objectIndex}, material={materialIndex}: {reason}");
        }

        // Explain WHY a generator was rejected. Matching UV array lengths only
        // confirms that the raw channel exists; it does not validate GX flags.
        static string DescribeTexGen(BrresModelReader.PolygonSurface surface,
            BrresTexCoordGenerators.Material material, int texCoord)
        {
            var gen = material.ForTexCoord(texCoord);
            if (gen is null)
                return $"coord={texCoord}: XF generator missing (available=[{string.Join(",", material.Generators.Keys.OrderBy(x => x))}])";

            var input = gen.InputUv == 0 ? surface.Uvs : surface.Uvs1;
            string reason = gen.DirectUv ? "direct" : "non-direct";
            if (gen.Type != 0) reason += "/type";
            if (gen.InputUv is < 0 or > 1) reason += "/source";
            if (gen.Projection != 0) reason += "/projection";
            if (gen.Normalize) reason += "/normalize";
            if (gen.MapMode != 0) reason += "/mapMode";
            if (!gen.EffectIdentity) reason += "/effect";
            if (gen.DualMatrix != 0) reason += "/dualMatrix";
            if (input is null || input.Count != surface.Corners.Count) reason += "/missingInput";
            return $"coord={texCoord}, generator={gen.Id}, row={gen.SourceRow}, inputUV={gen.InputUv}, " +
                $"type={gen.Type}, projection={gen.Projection}, inputForm={gen.InputForm}, " +
                $"normalize={gen.Normalize}, mapMode={gen.MapMode}, dualMatrix={gen.DualMatrix}, " +
                $"effectIdentity={gen.EffectIdentity}, " +
                $"SRT=({gen.ScaleU:G6},{gen.ScaleV:G6},{gen.Rotation:G6},{gen.TranslateU:G6},{gen.TranslateV:G6}), " +
                $"sourceCount={input?.Count ?? 0}, corners={surface.Corners.Count}, check={reason}";
        }

        double cx = (model.Minimum.X + model.Maximum.X) * 0.5;
        double cy = (model.Minimum.Y + model.Maximum.Y) * 0.5;
        double cz = (model.Minimum.Z + model.Maximum.Z) * 0.5;
        foreach (var surface in model.PolygonSurfaces)
        {
            if (!model.SurfaceMaterials.TryGetValue(surface.ObjectIndex, out var material))
            {
                Reject(surface.ObjectIndex, -1, "missing surface material");
                continue;
            }
            int mid = material.MaterialIndex;
            if (!model.TevShaders.TryGetValue(mid, out var shader))
            {
                Reject(surface.ObjectIndex, mid, "missing TEV shader");
                continue;
            }
            if (shader.Problem is not null)
            {
                Reject(surface.ObjectIndex, mid, "shader error: " + shader.Problem);
                continue;
            }
            if (!model.TevLayers.TryGetValue(mid, out var layers))
            {
                Reject(surface.ObjectIndex, mid, "missing TEV layer table");
                continue;
            }
            if (!model.TexCoordGenerators.TryGetValue(mid, out var generators))
            {
                Reject(surface.ObjectIndex, mid, "missing XF texgen material");
                continue;
            }

            // Stage index is not the texture map index; sample in TEV execution order.
            var sampled = shader.Stages.Where(s => s.TextureEnabled).Take(2).ToArray();
            if (sampled.Length != 2)
            {
                Reject(surface.ObjectIndex, mid, $"{sampled.Length} texture-enabled TEV stages (requires two)");
                continue;
            }
            if (sampled.Any(s => s.IndirectCommand != 0))
            {
                Reject(surface.ObjectIndex, mid, "indirect TEV stage");
                continue;
            }
            if (sampled.Any(s => s.TextureMap < 0 || s.TextureMap >= layers.Count))
            {
                Reject(surface.ObjectIndex, mid, $"texture map out of range (maps={sampled[0].TextureMap},{sampled[1].TextureMap}, layers={layers.Count})");
                continue;
            }
            if (!BrresTexCoordGenerators.TryResolveStageUvs(surface, generators, sampled[0].TexCoord, out var uv0, allowUnresolvedPostMatrix3ForPreview: true) || uv0 is null)
            {
                Reject(surface.ObjectIndex, mid, $"stage {sampled[0].Index} UV rejected ({DescribeTexGen(surface, generators, sampled[0].TexCoord)})");
                continue;
            }
            if (!BrresTexCoordGenerators.TryResolveStageUvs(surface, generators, sampled[1].TexCoord, out var uv1, allowUnresolvedPostMatrix3ForPreview: true) || uv1 is null)
            {
                Reject(surface.ObjectIndex, mid, $"stage {sampled[1].Index} UV rejected ({DescribeTexGen(surface, generators, sampled[1].TexCoord)})");
                continue;
            }
            var layer0 = layers[sampled[0].TextureMap];
            var layer1 = layers[sampled[1].TextureMap];
            if (!model.TexturePixels.ContainsKey(layer0.TextureName) ||
                !model.TexturePixels.ContainsKey(layer1.TextureName))
            {
                Reject(surface.ObjectIndex, mid, $"missing decoded TEX0 ({layer0.TextureName}, {layer1.TextureName})");
                continue;
            }
            if (surface.Corners.Count != uv0.Count || surface.Corners.Count != uv1.Count)
            {
                Reject(surface.ObjectIndex, mid, $"UV/corner count mismatch ({surface.Corners.Count}/{uv0.Count}/{uv1.Count})");
                continue;
            }
            if (layer0.WrapU is < 0 or > 2 || layer0.WrapV is < 0 or > 2 ||
                layer1.WrapU is < 0 or > 2 || layer1.WrapV is < 0 or > 2)
            {
                Reject(surface.ObjectIndex, mid, "unsupported layer texture wrapping");
                continue;
            }

            // Per-corner GX color0 is separate from the independently generated UVs.
            bool hasVertexRaster = surface.Colors0?.Count == surface.Corners.Count;
            var vertices = new float[surface.Corners.Count * 11];
            bool finite = true;
            for (int i = 0, j = 0; i < surface.Corners.Count; i++)
            {
                var p = surface.Corners[i];
                if (!double.IsFinite(uv0[i].X) || !double.IsFinite(uv0[i].Y) ||
                    !double.IsFinite(uv1[i].X) || !double.IsFinite(uv1[i].Y) ||
                    Math.Max(Math.Abs(uv0[i].X), Math.Abs(uv0[i].Y)) > 100000 ||
                    Math.Max(Math.Abs(uv1[i].X), Math.Abs(uv1[i].Y)) > 100000)
                { finite = false; break; }
                vertices[j++] = (float)((p.X - cx) * 0.001);
                vertices[j++] = (float)((p.Y - cy) * 0.001);
                vertices[j++] = (float)((p.Z - cz) * 0.001);
                vertices[j++] = (float)uv0[i].X;
                vertices[j++] = (float)uv0[i].Y;
                vertices[j++] = (float)uv1[i].X;
                vertices[j++] = (float)uv1[i].Y;
                var raster = hasVertexRaster ? surface.Colors0![i] : System.Windows.Media.Colors.White;
                vertices[j++] = raster.R / 255f;
                vertices[j++] = raster.G / 255f;
                vertices[j++] = raster.B / 255f;
                vertices[j++] = raster.A / 255f;
            }
            if (!finite)
            {
                Reject(surface.ObjectIndex, mid, "non-finite or oversized generated UV");
                continue;
            }
            int texture0 = GetTexture(model, layer0.TextureName, layer0.WrapU, layer0.WrapV);
            int texture1 = GetTexture(model, layer1.TextureName, layer1.WrapU, layer1.WrapV);
            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices,
                BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 11 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 11 * sizeof(float), 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 11 * sizeof(float), 5 * sizeof(float));
            GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, 11 * sizeof(float), 7 * sizeof(float));
            GL.EnableVertexAttribArray(3);
            GL.EnableVertexAttribArray(2);
            // GX color and alpha equations are evaluated only for configurations
            // whose two texture-enabled stages are the entire TEV program.
            // Other stage types and unsupported selectors retain F8 input previews.
            model.TevKonst.TryGetValue(mid, out var konst);
            model.TevInitialRegisters.TryGetValue(mid, out var initial);
            model.TevRasterChannels.TryGetValue(mid, out var rasterChannel);
            string equationReason = ValidateTwoStageEquations(shader, konst);
            bool equationSupported = equationReason.Length == 0;
            if (equationSupported) equationsSupported++;
            else equationReject[equationReason] = equationReject.GetValueOrDefault(equationReason) + 1;
            _tevInputBatches.Add(new TevInputBatch(vao, vbo, surface.Corners.Count,
                texture0, texture1, mid, sampled[0].Index, sampled[1].Index,
                sampled[0], sampled[1], konst, initial, rasterChannel, hasVertexRaster, equationSupported));
            eligible++;
            if (sampled.Any(s => generators.ForTexCoord(s.TexCoord)?.DualMatrix == 3))
                approximatePostMatrix3++;
        }
        GL.BindVertexArray(0);
        WriteDiagnostic($"Step 6d.1 GPU TEV inputs: {eligible} eligible; {skipped} skipped. " +
            "F8: baseline -> stage0 -> stage1 -> experimental TEV -> baseline.");
        int unlitChannel = _tevInputBatches.Count(b => b.EquationSupported && b.RasterChannel?.IsUnlit == true);
        int lightingPending = _tevInputBatches.Count(b => b.EquationSupported &&
            b.RasterChannel is { IsUnlit: false });
        int missingChannel = _tevInputBatches.Count(b => b.EquationSupported && b.RasterChannel is null);
        int registerSourced = _tevInputBatches.Count(b => b.EquationSupported &&
            b.RasterChannel is { IsUnlit: true, MaterialRegisterComponents: > 0 });
        WriteDiagnostic($"Step 6d.3 Pass 3 GX raster channel0: {unlitChannel} equation-ready unlit channels " +
            $"(of which {registerSourced} use material register components), " +
            $"{lightingPending} require GX light evaluation, {missingChannel} lack channel state. " +
            "Ambient registers decoded; enabled lighting retains Color0 approximation.");
        int rasterReady = _tevInputBatches.Count(b => b.EquationSupported && b.HasVertexRaster);
        WriteDiagnostic($"Step 6d.3 Pass 2 GX color0: {rasterReady} equation-ready surfaces with per-corner colors; " +
            $"{equationsSupported - rasterReady} still use WHITE raster approximation. " +
            "Lighting and GX channel material/ambient sources not yet evaluated.");
        int initialKnown = _tevInputBatches.Where(b => b.EquationSupported)
            .Sum(b => b.InitialRegisters?.KnownChannels ?? 0);
        int initialMissing = equationsSupported * 16 - initialKnown;
        WriteDiagnostic($"Step 6d.3 TEV register channels: {initialKnown} decoded / " +
            $"{equationsSupported * 16} across {equationsSupported} equation-ready surfaces; " +
            $"{initialMissing} unset channels use zero as an EXPLICIT approximation.");
        WriteDiagnostic($"Step 6d.2 GLSL TEV equations: {equationsSupported} preview candidates / {eligible} input-ready surfaces; " +
            "decoded initial TEV registers where available; WHITE raster inputs and unresolved post-matrix 3 remain APPROXIMATIONS; baseline unchanged.");
        foreach (var r in equationReject.OrderByDescending(p => p.Value))
            WriteDiagnostic($"Step 6d.2 equation fallback {r.Value}x: {r.Key}");
        WriteDiagnostic($"Step 6d.1 preview approximation: {approximatePostMatrix3} eligible surfaces " +
            "use unresolved GX DUALTEX post-matrix index 3 (raw UV/SRT shown; not exact GX coordinates).");
        foreach (var pair in rejectCounts.OrderByDescending(p => p.Value))
            WriteDiagnostic($"Step 6d.1 reject {pair.Value}x: {pair.Key}");
        foreach (var detail in rejectionDetails)
            WriteDiagnostic("Step 6d.1 detail: " + detail);
    }

    // Checks selector bounds and structural capabilities. Missing BP material
    // initial TEV register values are not fabricated as faithful output: mode 3
    // deliberately uses zero-initialized registers and is labeled experimental.
    private static string ValidateTwoStageEquations(BrresTevDiagnostics.Shader shader,
        BrresTevDiagnostics.MaterialKonst? konst)
    {
        if (shader.Stages.Count != 2 || shader.Stages.Any(s => !s.TextureEnabled))
            return "requires exactly two texture-enabled stages";
        foreach (var s in shader.Stages)
        {
            if (s.IndirectCommand != 0) return "indirect TEV";
            if (s.RasterSwap != 0 || s.TextureSwap != 0) return "TEV swap tables unresolved";
            if (s.ColorBias == 3 || s.AlphaBias == 3) return "compare-mode combiner";
            if (s.ColorScale is < 0 or > 3 || s.AlphaScale is < 0 or > 3) return "invalid combiner scale";
            if (s.ColorDestination is < 0 or > 3 || s.AlphaDestination is < 0 or > 3)
                return "invalid TEV register destination";
            // GX_CC_KONST (14) and GX_CA_KONST (6) are valid input selectors.
            // Only fixed fractions (KSEL 0..7) are supported in this preview.
            // K0..K3-backed selections require actual material register decoding.
            if (new[] { s.ColorA, s.ColorB, s.ColorC, s.ColorD }.Any(a => a is < 0 or > 15))
                return "invalid TEV color selector";
            if (new[] { s.AlphaA, s.AlphaB, s.AlphaC, s.AlphaD }.Any(a => a is < 0 or > 7))
                return "invalid TEV alpha selector";
            if (new[] { s.ColorA, s.ColorB, s.ColorC, s.ColorD }.Contains(14) &&
                !HasKonstColor(s.KonstColor, konst))
                return $"missing material KONST color (KSEL={s.KonstColor})";
            if (new[] { s.AlphaA, s.AlphaB, s.AlphaC, s.AlphaD }.Contains(6) &&
                !HasKonstAlpha(s.KonstAlpha, konst))
                return $"missing material KONST alpha (KASEL={s.KonstAlpha})";
        }
        if (shader.Stages[^1].ColorDestination != 0 || shader.Stages[^1].AlphaDestination != 0)
            return "final color/alpha not written to PREV";
        return "";
    }

    private static bool HasKonstAlpha(int sel, BrresTevDiagnostics.MaterialKonst? k)
    {
        if (sel is >= 0 and <= 7) return true;
        return sel is >= 0x10 and <= 0x1F && k is not null &&
            k.HasChannel(sel & 3, (sel - 0x10) / 4);
    }

    private static bool HasKonstColor(int sel, BrresTevDiagnostics.MaterialKonst? k)
    {
        if (sel is >= 0 and <= 7) return true;
        if (k is null) return false;
        if (sel is >= 0x0C and <= 0x0F)
            return Enumerable.Range(0, 3).All(c => k.HasChannel(sel & 3, c));
        if (sel is >= 0x10 and <= 0x1F)
            return k.HasChannel(sel & 3, (sel - 0x10) / 4);
        return false;
    }

    private void DrawTevInputPreview(Matrix4 viewProjection)
    {
        if (_tevInputBatches.Count == 0) return;
        if (_tevInputProgram == 0) InitializeTevInputProgram();
        GL.UseProgram(_tevInputProgram);
        GL.UniformMatrix4(_tevInputMatrix, false, ref viewProjection);
        GL.Uniform1(_tevInputSampler0, 0);
        GL.Uniform1(_tevInputSampler1, 1);
        GL.Uniform1(_tevInputMode, TevInputPreviewMode);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(false); // preview never modifies baseline depth buffer
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(-1f, -1f);
        try
        {
            foreach (var batch in _tevInputBatches)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, batch.Texture0);
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, batch.Texture1);
                if (TevInputPreviewMode == 3 && !batch.EquationSupported) continue;
                if (TevInputPreviewMode == 3)
                {
                    for (int k = 0; k < 4; ++k)
                    {
                        var colors = batch.Constants?.Rgba;
                        int offset = k * 4;
                        GL.Uniform4(_tevKonstRegisters[k],
                            colors is null ? 0f : colors[offset],
                            colors is null ? 0f : colors[offset + 1],
                            colors is null ? 0f : colors[offset + 2],
                            colors is null ? 0f : colors[offset + 3]);
                    }
                    for (int k = 0; k < 4; ++k)
                    {
                        var initial = batch.InitialRegisters?.Rgba;
                        int offset = k * 4;
                        GL.Uniform4(_tevInitialRegisters[k],
                            initial is null ? 0f : initial[offset],
                            initial is null ? 0f : initial[offset + 1],
                            initial is null ? 0f : initial[offset + 2],
                            initial is null ? 0f : initial[offset + 3]);
                    }
                    GL.Uniform1(_tevUseVertexRaster, batch.HasVertexRaster ? 1 : 0);
                    var rc = batch.RasterChannel;
                    var mat = rc?.MaterialRgba;
                    GL.Uniform4(_tevRasterMaterialColor,
                        mat is null ? 1f : mat[0], mat is null ? 1f : mat[1],
                        mat is null ? 1f : mat[2], mat is null ? 1f : mat[3]);
                    // Only unlit channels are resolved exactly. Lit/missing channels
                    // retain the known Color0 preview rather than fake lighting.
                    GL.Uniform3(_tevRasterSourceFlags,
                        rc?.IsUnlit == true ? 1 : 0,
                        rc?.ColorMaterialFromVertex == true ? 1 : 0,
                        rc?.AlphaMaterialFromVertex == true ? 1 : 0);
                    UploadTevStageUniforms(batch.Equation0, 0);
                    UploadTevStageUniforms(batch.Equation1, 1);
                }
                GL.BindVertexArray(batch.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, batch.VertexCount);
            }
        }
        finally
        {
            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.DepthMask(true);
            GL.Enable(EnableCap.Blend);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindVertexArray(0);
            GL.UseProgram(_program);
        }
    }

    private void UploadTevStageUniforms(BrresTevDiagnostics.Stage s, int index)
    {
        GL.Uniform4(_tevColorArgs[index], s.ColorA, s.ColorB, s.ColorC, s.ColorD);
        GL.Uniform4(_tevAlphaArgs[index], s.AlphaA, s.AlphaB, s.AlphaC, s.AlphaD);
        GL.Uniform4(_tevColorSettings[index], s.ColorBias, s.ColorSubtract ? 1 : 0,
            s.ColorScale, s.ColorClamp ? 1 : 0);
        GL.Uniform4(_tevAlphaSettings[index], s.AlphaBias, s.AlphaSubtract ? 1 : 0,
            s.AlphaScale, s.AlphaClamp ? 1 : 0);
        GL.Uniform1(_tevColorDest[index], s.ColorDestination);
        GL.Uniform1(_tevAlphaDest[index], s.AlphaDestination);
        GL.Uniform1(_tevKonstColorFraction[index], s.KonstColor);
        GL.Uniform1(_tevKonstAlphaFraction[index], s.KonstAlpha);
    }

    private void InitializeTevInputProgram()
    {
        const string vertex = """
            #version 330 core
            layout(location=0) in vec3 aPosition;
            layout(location=1) in vec2 aStage0Uv;
            layout(location=2) in vec2 aStage1Uv;
            layout(location=3) in vec4 aRasterColor;
            uniform mat4 uViewProjection;
            out vec2 vStage0Uv;
            out vec2 vStage1Uv;
            out vec4 vRasterColor;
            void main() {
                gl_Position = uViewProjection * vec4(aPosition, 1.0);
                vStage0Uv = aStage0Uv;
                vStage1Uv = aStage1Uv;
                vRasterColor = aRasterColor;
            }
            """;
        const string fragment = """
            #version 330 core
            in vec2 vStage0Uv;
            in vec2 vStage1Uv;
            in vec4 vRasterColor;
            uniform int uUseVertexRaster;
            uniform vec4 uRasterMaterialColor;
            uniform ivec3 uRasterSourceFlags;
            vec4 rasterInput() {
                vec4 vertex = uUseVertexRaster != 0 ? vRasterColor : vec4(1.0);
                if (uRasterSourceFlags.x == 0) return vertex;
                return vec4(uRasterSourceFlags.y != 0 ? vertex.rgb : uRasterMaterialColor.rgb,
                            uRasterSourceFlags.z != 0 ? vertex.a : uRasterMaterialColor.a);
            }
            uniform sampler2D uStage0;
            uniform sampler2D uStage1;
            uniform int uPreviewMode;
            uniform ivec4 uColorArgs[2];
            uniform ivec4 uAlphaArgs[2];
            // bias, subtract, scale, clamp
            uniform ivec4 uColorSettings[2];
            uniform ivec4 uAlphaSettings[2];
            uniform int uColorDest[2];
            uniform int uAlphaDest[2];
            uniform int uKonstColorFraction[2];
            uniform int uKonstAlphaFraction[2];
            uniform vec4 uKonstRegisters[4];
            uniform vec4 uInitialRegisters[4];
            float konstFraction(int selection) {
                if (selection >= 0 && selection <= 7)
                    return float(8 - selection) / 8.0;
                return 0.0;
            }
            float konstAlphaValue(int selection) {
                if (selection <= 7) return konstFraction(selection);
                if (selection >= 16 && selection <= 31)
                    return uKonstRegisters[selection & 3][(selection - 16) / 4];
                return 0.0;
            }
            vec3 konstColorValue(int selection) {
                if (selection <= 7) return vec3(konstFraction(selection));
                if (selection >= 12 && selection <= 15)
                    return uKonstRegisters[selection & 3].rgb;
                if (selection >= 16 && selection <= 31)
                    return vec3(uKonstRegisters[selection & 3][(selection - 16) / 4]);
                return vec3(0.0);
            }
            out vec4 FragColor;
            float colorArg(int selector, int component, vec4 tex, vec4 regs[4], vec3 konst) {
                if (selector < 8) {
                    int regIndex = selector / 2;
                    return (selector % 2) == 0 ? regs[regIndex][component] : regs[regIndex].a;
                }
                if (selector == 8) return tex[component];
                if (selector == 9) return tex.a;
                // GX raster channel0: available vertex color, or documented white fallback.
                if (selector == 10) return rasterInput()[component];
                if (selector == 11) return rasterInput().a;
                if (selector == 12) return 1.0;
                if (selector == 13) return 0.5;
                if (selector == 14) return konst[component];
                return 0.0; // 15: GX zero.
            }
            float alphaArg(int selector, vec4 tex, vec4 regs[4], float konst) {
                if (selector <= 3) return regs[selector].a;
                if (selector == 4) return tex.a;
                if (selector == 5) return rasterInput().a;
                if (selector == 6) return konst;
                return 0.0; // 7: GX zero.
            }
            float combine(float a, float b, float c, float d, ivec4 settings) {
                float mixValue = a * (1.0 - c) + b * c;
                float value = d + (settings.y != 0 ? -mixValue : mixValue);
                if (settings.x == 1) value += 0.5;
                if (settings.x == 2) value -= 0.5;
                if (settings.z == 1) value *= 2.0;
                if (settings.z == 2) value *= 4.0;
                if (settings.z == 3) value *= 0.5;
                return clamp(value, settings.w != 0 ? 0.0 : -4.0,
                                    settings.w != 0 ? 1.0 : 4.0);
            }
            void main() {
                vec4 tex0 = texture(uStage0, vStage0Uv);
                vec4 tex1 = texture(uStage1, vStage1Uv);
                if (uPreviewMode != 3) {
                    FragColor = uPreviewMode == 1 ? tex0 : tex1;
                    if (FragColor.a <= 0.0039) discard;
                    return;
                }
                // Initialize PREV/REG0/REG1/REG2 with decoded material BP values.
                // Undeclared channels are currently zero-filled, reported in logs.
                vec4 regs[4];
                for (int r = 0; r < 4; ++r) regs[r] = uInitialRegisters[r];
                for (int stage = 0; stage < 2; ++stage) {
                    vec4 tex = stage == 0 ? tex0 : tex1;
                    vec3 konstColor = konstColorValue(uKonstColorFraction[stage]);
                    float konstAlpha = konstAlphaValue(uKonstAlphaFraction[stage]);
                    vec3 rgb = vec3(0.0);
                    for (int c = 0; c < 3; ++c) {
                        rgb[c] = combine(colorArg(uColorArgs[stage].x,c,tex,regs,konstColor),
                                         colorArg(uColorArgs[stage].y,c,tex,regs,konstColor),
                                         colorArg(uColorArgs[stage].z,c,tex,regs,konstColor),
                                         colorArg(uColorArgs[stage].w,c,tex,regs,konstColor),
                                         uColorSettings[stage]);
                    }
                    float alpha = combine(alphaArg(uAlphaArgs[stage].x,tex,regs,konstAlpha),
                                          alphaArg(uAlphaArgs[stage].y,tex,regs,konstAlpha),
                                          alphaArg(uAlphaArgs[stage].z,tex,regs,konstAlpha),
                                          alphaArg(uAlphaArgs[stage].w,tex,regs,konstAlpha),
                                          uAlphaSettings[stage]);
                    // GX maintains separate RGB/alpha destinations.
                    regs[uColorDest[stage]].rgb = rgb;
                    regs[uAlphaDest[stage]].a = alpha;
                }
                FragColor = clamp(regs[0], vec4(0.0), vec4(1.0));
                if (FragColor.a <= 0.0039) discard;
            }
            """;
        int vs = Compile(ShaderType.VertexShader, vertex);
        int fs = Compile(ShaderType.FragmentShader, fragment);
        int program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
        string log = GL.GetProgramInfoLog(program);
        GL.DetachShader(program, vs);
        GL.DetachShader(program, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        if (ok == 0) { GL.DeleteProgram(program); throw new InvalidOperationException("TEV input shader: " + log); }
        _tevInputProgram = program;
        _tevInputMatrix = GL.GetUniformLocation(program, "uViewProjection");
        _tevInputMode = GL.GetUniformLocation(program, "uPreviewMode");
        _tevInputSampler0 = GL.GetUniformLocation(program, "uStage0");
        _tevInputSampler1 = GL.GetUniformLocation(program, "uStage1");
        _tevUseVertexRaster = GL.GetUniformLocation(program, "uUseVertexRaster");
        _tevRasterMaterialColor = GL.GetUniformLocation(program, "uRasterMaterialColor");
        _tevRasterSourceFlags = GL.GetUniformLocation(program, "uRasterSourceFlags");
        for (int i = 0; i < 2; i++)
        {
            _tevColorArgs[i] = GL.GetUniformLocation(program, $"uColorArgs[{i}]");
            _tevAlphaArgs[i] = GL.GetUniformLocation(program, $"uAlphaArgs[{i}]");
            _tevColorSettings[i] = GL.GetUniformLocation(program, $"uColorSettings[{i}]");
            _tevAlphaSettings[i] = GL.GetUniformLocation(program, $"uAlphaSettings[{i}]");
            _tevColorDest[i] = GL.GetUniformLocation(program, $"uColorDest[{i}]");
            _tevAlphaDest[i] = GL.GetUniformLocation(program, $"uAlphaDest[{i}]");
            _tevKonstColorFraction[i] = GL.GetUniformLocation(program, $"uKonstColorFraction[{i}]");
            _tevKonstAlphaFraction[i] = GL.GetUniformLocation(program, $"uKonstAlphaFraction[{i}]");
        }
        for (int i = 0; i < 4; i++)
            _tevKonstRegisters[i] = GL.GetUniformLocation(program, $"uKonstRegisters[{i}]");
        for (int i = 0; i < 4; i++)
            _tevInitialRegisters[i] = GL.GetUniformLocation(program, $"uInitialRegisters[{i}]");
    }

    // Alpha values of 0/255 represent cutout geometry, which can write depth.
    // Values strictly between them require conventional translucent rendering.
    private static bool HasPartialAlpha(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] > 0 && rgba[i] < 255) return true;
        return false;
    }

    private static bool AppendTiledTriangle(List<float> output,
        UvCorner a, UvCorner b, UvCorner c, int wrapU, int wrapV,
        double cx, double cy, double cz)
    {
        if (!Finite(a.Uv) || !Finite(b.Uv) || !Finite(c.Uv)) return false;
        double minU = Math.Min(a.Uv.X, Math.Min(b.Uv.X, c.Uv.X));
        double maxU = Math.Max(a.Uv.X, Math.Max(b.Uv.X, c.Uv.X));
        double minV = Math.Min(a.Uv.Y, Math.Min(b.Uv.Y, c.Uv.Y));
        double maxV = Math.Max(a.Uv.Y, Math.Max(b.Uv.Y, c.Uv.Y));
        int u0 = wrapU == 0 ? 0 : (int)Math.Floor(minU);
        int u1 = wrapU == 0 ? 0 : (int)Math.Floor(maxU);
        int v0 = wrapV == 0 ? 0 : (int)Math.Floor(minV);
        int v1 = wrapV == 0 ? 0 : (int)Math.Floor(maxV);
        if ((long)(u1 - u0 + 1) * (v1 - v0 + 1) > MaxTilesPerTriangle)
            return false;
        for (int v = v0; v <= v1; v++)
        for (int u = u0; u <= u1; u++)
        {
            List<UvCorner> clipped = [a, b, c];
            if (wrapU != 0)
            {
                clipped = Clip(clipped, 0, u, true);
                clipped = Clip(clipped, 0, u + 1, false);
            }
            if (wrapV != 0)
            {
                clipped = Clip(clipped, 1, v, true);
                clipped = Clip(clipped, 1, v + 1, false);
            }
            for (int i = 1; i + 1 < clipped.Count; i++)
            {
                if (output.Count / 15 >= MaxOutputTrianglesPerSurface) return false;
                EmitTileVertex(output, clipped[0], u, v, wrapU, wrapV, cx, cy, cz);
                EmitTileVertex(output, clipped[i], u, v, wrapU, wrapV, cx, cy, cz);
                EmitTileVertex(output, clipped[i + 1], u, v, wrapU, wrapV, cx, cy, cz);
            }
        }
        return true;
    }

    private static bool Finite(System.Windows.Point uv) =>
        double.IsFinite(uv.X) && double.IsFinite(uv.Y) &&
        Math.Abs(uv.X) < 100000 && Math.Abs(uv.Y) < 100000;

    private static List<UvCorner> Clip(List<UvCorner> input,
        int axis, double edge, bool greater)
    {
        var result = new List<UvCorner>();
        if (input.Count == 0) return result;
        UvCorner previous = input[^1];
        bool previousInside = Inside(previous);
        foreach (UvCorner current in input)
        {
            bool currentInside = Inside(current);
            if (currentInside != previousInside)
            {
                double before = Value(previous), after = Value(current);
                double t = (edge - before) / (after - before);
                result.Add(new UvCorner(
                    new Point3D(
                        previous.Position.X + (current.Position.X - previous.Position.X) * t,
                        previous.Position.Y + (current.Position.Y - previous.Position.Y) * t,
                        previous.Position.Z + (current.Position.Z - previous.Position.Z) * t),
                    new System.Windows.Point(
                        previous.Uv.X + (current.Uv.X - previous.Uv.X) * t,
                        previous.Uv.Y + (current.Uv.Y - previous.Uv.Y) * t)));
            }
            if (currentInside) result.Add(current);
            previous = current;
            previousInside = currentInside;
        }
        return result;
        double Value(UvCorner p) => axis == 0 ? p.Uv.X : p.Uv.Y;
        bool Inside(UvCorner p) => greater ? Value(p) >= edge - 1e-9 : Value(p) <= edge + 1e-9;
    }

    private static void EmitTileVertex(List<float> output, UvCorner corner,
        int u, int v, int wrapU, int wrapV,
        double cx, double cy, double cz)
    {
        double x = wrapU == 0 ? Math.Clamp(corner.Uv.X, 0, 1) : corner.Uv.X - u;
        double y = wrapV == 0 ? Math.Clamp(corner.Uv.Y, 0, 1) : corner.Uv.Y - v;
        if (wrapU == 2 && (u & 1) != 0) x = 1 - x;
        if (wrapV == 2 && (v & 1) != 0) y = 1 - y;
        EmitVertex(output, corner.Position,
            new System.Windows.Point(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)),
            cx, cy, cz);
    }

    private static void EmitVertex(List<float> output, Point3D point,
        System.Windows.Point uv, double cx, double cy, double cz)
    {
        output.Add((float)((point.X - cx) * 0.001));
        output.Add((float)((point.Y - cy) * 0.001));
        output.Add((float)((point.Z - cz) * 0.001));
        output.Add((float)uv.X);
        output.Add((float)uv.Y);
    }

    private int GetTexture(BrresModelReader.DecodeResult model, string name, int wrapU, int wrapV)
    {
        var key = (name, wrapU, wrapV);
        if (_textures.TryGetValue(key, out int id)) return id;
        var pixels = model.TexturePixels[name];
        id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            pixels.Width, pixels.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.Rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)Wrap(wrapU));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)Wrap(wrapV));
        _textures.Add(key, id);
        return id;
    }

    private static TextureWrapMode Wrap(int mode) => mode switch
    {
        0 => TextureWrapMode.ClampToEdge,
        2 => TextureWrapMode.MirroredRepeat,
        _ => TextureWrapMode.Repeat
    };

    private void Initialize()
    {
        const string vertex = """
            #version 330 core
            layout(location=0) in vec3 aPosition;
            layout(location=1) in vec2 aUv;
            uniform mat4 uViewProjection;
            out vec2 vUv;
            void main() {
                gl_Position = uViewProjection * vec4(aPosition, 1.0);
                vUv = aUv;
            }
            """;
        const string fragment = """
            #version 330 core
            in vec2 vUv;
            out vec4 FragColor;
            uniform sampler2D uTexture;
            uniform int uTextured;
            void main() {
                // Verified against the WPF renderer: do NOT flip V.
                FragColor = uTextured != 0 ? texture(uTexture, vUv) : vec4(0.61, 0.64, 0.66, 1.0);
                // Cutout pixels should not occlude objects behind them.
                if (FragColor.a <= 0.0039) discard;
            }
            """;
        int vs = Compile(ShaderType.VertexShader, vertex);
        int fs = Compile(ShaderType.FragmentShader, fragment);
        _program = GL.CreateProgram();
        GL.AttachShader(_program, vs);
        GL.AttachShader(_program, fs);
        GL.LinkProgram(_program);
        GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out int ok);
        string info = GL.GetProgramInfoLog(_program);
        GL.DetachShader(_program, vs);
        GL.DetachShader(_program, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        if (ok == 0) throw new InvalidOperationException("OpenGL shader link failed: " + info);
        _matrixLocation = GL.GetUniformLocation(_program, "uViewProjection");
        _samplerLocation = GL.GetUniformLocation(_program, "uTexture");
        _texturedLocation = GL.GetUniformLocation(_program, "uTextured");
        _whiteTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _whiteTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1, 1, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 });
    }

    private static int Compile(ShaderType type, string src)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, src);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new InvalidOperationException($"OpenGL {type} compilation failed: {log}");
        }
        return shader;
    }

    private void ReleaseModel()
    {
        foreach (var batch in _batches)
        {
            GL.DeleteBuffer(batch.Vbo);
            GL.DeleteVertexArray(batch.Vao);
        }
        _batches.Clear();
        foreach (var batch in _tevInputBatches)
        {
            GL.DeleteBuffer(batch.Vbo);
            GL.DeleteVertexArray(batch.Vao);
        }
        _tevInputBatches.Clear();
        foreach (int texture in _textures.Values) GL.DeleteTexture(texture);
        _textures.Clear();
        _uploaded = null;
    }
}
