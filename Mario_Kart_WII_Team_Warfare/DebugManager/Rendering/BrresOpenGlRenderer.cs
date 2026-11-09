using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Globalization;
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

    // Step 6e: render the consolidated preview by default, while keeping the
    // original WPF-parity baseline explicitly selectable (F8 mode 0 / F9).
    // These constants are also used by the page's keyboard controls.
    internal const int BaselineMode = 0;
    internal const int RegularTevMode = 3;
    internal const int IndirectTevMode = 6;
    internal const int ProjectedTevMode = 12;
    internal const int ConsolidatedTevMode = 13;
    // Window-frame regression diagnostics. F8 modes 14/15 change only the
    // consolidated draw composition; the default mode 13 stays untouched.
    internal const int ConsolidatedWithoutRegularMode = 14;
    internal const int ConsolidatedRegularNoOffsetMode = 15;
    internal const int ConsolidatedRegularAlphaBlendMode = 16;
    internal const int ConsolidatedRegularHalfBlendMode = 17;
    internal const int PreviewModeCount = 18;
    // All experimental paths retain their existing GX precision/UV limitations.
    internal int TevInputPreviewMode { get; set; } = ConsolidatedTevMode;
    private sealed record TevInputBatch(int Vao, int Vbo, int VertexCount,
        int Texture0, int Texture1, int MaterialIndex, int Stage0, int Stage1,
        BrresTevDiagnostics.Stage Equation0, BrresTevDiagnostics.Stage Equation1,
        BrresTevDiagnostics.MaterialKonst? Constants,
        BrresTevDiagnostics.InitialRegisters? InitialRegisters,
        BrresTevDiagnostics.RasterChannel0? RasterChannel,
        bool HasVertexRaster, bool EquationSupported,
        int IndirectTexture = 0, float[]? IndirectRowS = null, float[]? IndirectRowT = null,
        float IndirectScale = 1f, int IndirectBias = 0, bool ApproximateRegularUv = false,
        bool ProjectedStage1 = false);
    // Step 6e Phase 3: one model-scoped owner for all advanced TEV GPU batches.
    // Upload functions remain specialized; their output is immediately registered
    // here. This registry owns each VAO/VBO exactly once through ReleaseModel().
    private readonly List<MaterialDrawItem> _gpuTevMaterials = [];
    // Feature flags describe known approximations; they are not GX-exact claims.
    [Flags]
    private enum TevMaterialFeatures
    {
        None = 0,
        TwoStageTev = 1,
        Indirect = 2,
        ProjectedStage1 = 4,
        ApproximateRegularUv = 8,
        FrozenFifoProjection = 16
    }

    private sealed record MaterialDrawItem(TevInputBatch Batch, int ShaderMode,
        TevMaterialFeatures Features);

    private sealed record MaterialDrawPass(int ShaderMode, MaterialDrawItem[] Items,
        TevInputBatch[] Batches);

    // Phase 3: registration is the shared GPU upload/ownership boundary. All
    // three specialized uploaders call this after configuring a batch's VAO.
    private void RegisterTevMaterial(TevInputBatch batch, int mode)
    {
        TevMaterialFeatures path = mode switch
        {
            RegularTevMode => TevMaterialFeatures.None,
            IndirectTevMode => TevMaterialFeatures.Indirect,
            ProjectedTevMode => TevMaterialFeatures.ProjectedStage1,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown TEV path")
        };
        if (_gpuTevMaterials.Any(item => item.Batch.Vao == batch.Vao))
            throw new InvalidOperationException($"TEV VAO {batch.Vao} registered twice");
        var features = TevMaterialFeatures.TwoStageTev | path |
            (batch.ApproximateRegularUv ? TevMaterialFeatures.ApproximateRegularUv : TevMaterialFeatures.None) |
            (batch.ProjectedStage1 ? TevMaterialFeatures.ProjectedStage1 | TevMaterialFeatures.FrozenFifoProjection
                                   : TevMaterialFeatures.None);
        _gpuTevMaterials.Add(new MaterialDrawItem(batch, mode, features));
    }

    // Used only by upload-time diagnostics; all rendering uses the immutable
    // material draw pipeline built after all three uploaders finish.
    private List<TevInputBatch> GetUploadedTevBatches(int mode) =>
        _gpuTevMaterials.Where(item => item.ShaderMode == mode)
            .Select(item => item.Batch).ToList();

    // Step 6e isolated regression investigation: -1 draws every regular TEV
    // material; F10 cycles one excluded regular material at a time.  This is
    // diagnostic-only and does not modify material eligibility or shaders.
    private int _suppressedRegularTevMaterial = -1;
    public string CycleSuppressedRegularTevMaterial()
    {
        int[] ids = GetTevBatches(RegularTevMode)
            .Select(batch => batch.MaterialIndex).Distinct().OrderBy(id => id).ToArray();
        int current = Array.IndexOf(ids, _suppressedRegularTevMaterial);
        _suppressedRegularTevMaterial = current >= 0 && current + 1 < ids.Length
            ? ids[current + 1]
            : current < 0 && ids.Length > 0 ? ids[0] : -1;
        TevInputPreviewMode = ConsolidatedTevMode;
        string status = _suppressedRegularTevMaterial < 0
            ? "all regular TEV materials active"
            : $"regular TEV material {_suppressedRegularTevMaterial} skipped; baseline visible for that material";
        WriteDiagnostic($"Step 6e window-frame per-material A/B: {status}. " +
            $"Regular material IDs=[{string.Join(",", ids)}]. F10=next; F9=baseline/consolidated.");
        return status;
    }

    private MaterialDrawPass[] _materialDrawPipeline = [];

    private void BuildMaterialDrawPipeline()
    {
        MaterialDrawPass MakePass(int mode)
        {
            var items = _gpuTevMaterials.Where(item => item.ShaderMode == mode).ToArray();
            return new MaterialDrawPass(mode, items, items.Select(item => item.Batch).ToArray());
        }

        // The original regular -> indirect -> projected pass order is retained.
        _materialDrawPipeline =
        [
            MakePass(RegularTevMode),
            MakePass(IndirectTevMode),
            MakePass(ProjectedTevMode)
        ];
        // Do not carry an exclusion over to a newly uploaded track.
        _suppressedRegularTevMaterial = -1;

        int approximated = _gpuTevMaterials.Count(item =>
            (item.Features & TevMaterialFeatures.ApproximateRegularUv) != 0);
        int frozen = _gpuTevMaterials.Count(item =>
            (item.Features & TevMaterialFeatures.FrozenFifoProjection) != 0);
        WriteDiagnostic("Step 6e window-frame A/B: F8=13 original consolidated; " +
            "F8=14 baseline+indirect+projected (regular TEV OFF); " +
            "F8=15 regular TEV ON, depth polygon offset OFF for regular only. " +
            "Compare the SAME house windows from the SAME camera position. " +
            "No TEV equations, UVs, textures, or default-mode behavior changed.");
        WriteDiagnostic("Step 6e regular TEV material IDs: [" +
            string.Join(",", _gpuTevMaterials
                .Where(item => item.ShaderMode == RegularTevMode)
                .Select(item => item.Batch.MaterialIndex).Distinct().OrderBy(id => id)) + "]");
        WriteDiagnostic($"Step 6e Phase 3 unified GPU uploads: owned={_gpuTevMaterials.Count}, " +
            $"regular={_materialDrawPipeline[0].Items.Length}, " +
            $"indirect={_materialDrawPipeline[1].Items.Length}, " +
            $"projected={_materialDrawPipeline[2].Items.Length}; " +
            $"approximatedRegularUv={approximated}; frozenProjection={frozen}; " +
            "one GPU owner per advanced batch. Draw order and shaders unchanged.");
    }

    private MaterialDrawPass? FindMaterialDrawPass(int shaderMode)
    {
        // F8 input modes 1 and 2 use regular TEV input batches, just as before.
        int pathMode = shaderMode is 1 or 2 ? RegularTevMode : shaderMode;
        foreach (var pass in _materialDrawPipeline)
            if (pass.ShaderMode == pathMode) return pass;
        return null;
    }

    // Pass 4: isolated camera-position projection diagnostic. This is NOT the
    // complete GX Pos/Tex matrix-memory transform and never changes modes 0-6.
    private sealed record ProjectedPreviewBatch(int Vao, int Vbo, int VertexCount,
        int Texture, int MaterialIndex, int StageIndex);
    private readonly List<ProjectedPreviewBatch> _projectedPreviewBatches = [];
    private int _projectedProgram, _projectedMatrixLocation, _projectedSamplerLocation;

    // Pass 4f: fixed FIFO-frame replay preview. Separate VAO, GLSL and F8 mode;
    // never modifies mode 7, TEV mode 6 or the regular camera/matrix path.
    private sealed record FifoProjectionBatch(int Vao, int Vbo, int VertexCount, int Texture,
        int MaterialIndex, int StageIndex);
    private readonly List<FifoProjectionBatch> _fifoProjectionBatches = [];
    // Pass 4h: GX/Dolphin texgen order (raw vertex input -> identity TEXMTX60 -> POSTMTX3).
    private readonly List<FifoProjectionBatch> _fifoTexgenOrderBatches = [];
    private int _fifoProjectionProgram, _fifoProjectionMatrixLocation, _fifoProjectionSamplerLocation;
    // Pass 4i: the same input VAO as mode 9, with alternative GPU sampling
    // and an independent Q-sign visualization. Modes 0-9 remain unmodified.
    private int _fifoProjectiveTestProgram, _fifoProjectiveTestMatrixLocation;
    private int _fifoProjectiveTestSamplerLocation, _fifoProjectiveTestModeLocation;
    // Captured from FIFOLog(1).dff frame 0, BRRES material 21.
    // GX POSMTX0 from XF[0x0000..0x000B]; identity TEXMTX60; postmatrix 3 from XF[0x050C..0x0517].
    // A fixed playback reference ONLY, not a matrix for the freely moving viewer camera.
    private static readonly double[,] FifoFrame0Position = new double[3, 4]
    {
        { 0.4442273, -0, 0.8959142, -12866.13965 },
        { 0.1851498, 0.9784129, -0.0918041, -1811.952881 },
        { -0.8765738, 0.2066602, 0.4346377, 1208.759277 },
    };
    private static readonly double[,] FifoFrame0Post = new double[3, 4]
    {
        { 0.5264229, 0, -0.5, -0.000453 },
        { 0, -0.960491, -0.6, 7.32e-05 },
        { 0, 0, -1, 0.0001221 },
    };


    private int _tevIndirectSampler, _tevIndirectEnabled, _tevIndirectRowS, _tevIndirectRowT;
    private int _tevIndirectScale, _tevIndirectBias;
    // Step 6d.5 Pass 2: separate, read-only indirect-source GPU preview.
    // No indirect offsets are injected into the final TEV shader in this pass.
    private sealed record IndirectPreviewBatch(int Vao, int Vbo, int VertexCount,
        int Texture, int MaterialIndex, int TevStage, bool ApproximatedUv,
        float[] RowS, float[] RowT, float MatrixScale, int BiasSelection);
    private readonly List<IndirectPreviewBatch> _indirectPreviewBatches = [];
    private int _indirectProgram, _indirectMatrixLocation, _indirectModeLocation;
    private int _indirectSamplerLocation, _indirectRowSLocation, _indirectRowTLocation;
    private int _indirectMatrixScaleLocation, _indirectBiasLocation;

    private int _tevInputProgram;
    private int _tevInputMatrix;
    private int _tevInputMode;
    private int _tevInputSampler0;
    private int _tevInputSampler1;
    private int _tevProjectedStage1;
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
                UploadIndirectPreviewBatches(_requested);
                UploadProjectedPreviewBatches(_requested);
                UploadFifoProjectionBatches(_requested);
                UploadGxTexgenOrderBatches(_requested);
                UploadProjectedTevBatches(_requested);
                BuildMaterialDrawPipeline();
                LogConsolidatedTevCoverage(_requested);
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
        // Only the overlay dispatch changes in Step 6e. Baseline drawing,
        // depth handling, shader equations, and all uploaded batches are unchanged.
        switch (TevInputPreviewMode)
        {
            case >= 1 and <= 3:
                DrawTevInputPreview(combined);
                break;
            case 4 or 5:
                DrawIndirectPreview(combined);
                break;
            case IndirectTevMode:
            case ProjectedTevMode:
                DrawTevInputPreview(combined);
                break;
            case 7:
                DrawProjectedPreview(combined);
                break;
            case 8:
                DrawFifoProjectionPreview(combined);
                break;
            case 9:
                DrawFifoProjectionPreview(combined, _fifoTexgenOrderBatches);
                break;
            case 10 or 11:
                DrawFifoProjectiveTest(combined);
                break;
            case ConsolidatedTevMode:
                DrawConsolidatedTevPreview(combined);
                break;
            case ConsolidatedWithoutRegularMode:
                DrawConsolidatedTevPreview(combined, includeRegular: false);
                break;
            case ConsolidatedRegularNoOffsetMode:
                DrawConsolidatedTevPreview(combined, regularPolygonOffset: false);
                break;
            case ConsolidatedRegularAlphaBlendMode:
                DrawConsolidatedTevPreview(combined, regularBlend: RegularTevBlend.SourceAlpha);
                break;
            case ConsolidatedRegularHalfBlendMode:
                DrawConsolidatedTevPreview(combined, regularBlend: RegularTevBlend.HalfOpacity);
                break;
        }
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
        int eligible = 0, skipped = 0, verifiedPostMatrix3 = 0, unresolvedPostMatrix3 = 0, equationsSupported = 0;
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
            if (gen.DualMatrix != 0 && !gen.ExactPostMatrix) reason += "/unresolvedDualMatrix";
            if (input is null || input.Count != surface.Corners.Count) reason += "/missingInput";
            return $"coord={texCoord}, generator={gen.Id}, row={gen.SourceRow}, inputUV={gen.InputUv}, " +
                $"type={gen.Type}, projection={gen.Projection}, inputForm={gen.InputForm}, " +
                $"normalize={gen.Normalize}, mapMode={gen.MapMode}, dualMatrix={gen.DualMatrix}, " +
                $"verifiedIdentityPost={gen.VerifiedIdentityPostMatrix}, " +
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
            RegisterTevMaterial(new TevInputBatch(vao, vbo, surface.Corners.Count,
                texture0, texture1, mid, sampled[0].Index, sampled[1].Index,
                sampled[0], sampled[1], konst, initial, rasterChannel, hasVertexRaster, equationSupported), RegularTevMode);
            eligible++;
            if (sampled.Any(s => generators.ForTexCoord(s.TexCoord)?.DualMatrix == 3))
            {
                if (sampled.Where(s => generators.ForTexCoord(s.TexCoord)?.DualMatrix == 3)
                    .All(s => generators.ForTexCoord(s.TexCoord)?.ExactPostMatrix == true))
                    verifiedPostMatrix3++;
                else
                    unresolvedPostMatrix3++;
            }
        }
        GL.BindVertexArray(0);
        WriteDiagnostic($"Step 6d.1 GPU TEV inputs: {eligible} eligible; {skipped} skipped. " +
            "Step 6e: initial mode 13 consolidated TEV; F8 cycles modes 0-13; " +
            "F9 switches between baseline and consolidated. GX approximations remain experimental.");
        // Read-only GX indirect diagnostics; Pass 1 never changes texture sampling.
        var indirectMaterials = model.TevIndirect.Values.Where(m => m.UsesIndirect).ToArray();
        WriteDiagnostic($"Step 6d.5 Pass 1 GX indirect: {indirectMaterials.Length} materials with active indirect TEV; " +
            $"{indirectMaterials.Count(m => m.ReferenceRaw.HasValue)} with indirect source references; " +
            $"{indirectMaterials.Sum(m => m.Matrices.Count(x => x.Complete))} complete matrix groups. GPU source/offset preview only; no final TEV indirect evaluation.");
        foreach (var im in indirectMaterials.Take(12))
        {
            foreach (var st in im.Stages.Where(st => st.Active).Take(8))
            {
                var source = im.Sources.FirstOrDefault(x => x.Stage == st.IndirectStage);
                var scale = im.Scales.FirstOrDefault(x => x.Stage == st.IndirectStage);
                WriteDiagnostic($"Step 6d.5 indirect material={im.MaterialIndex} TEV={st.Index} " +
                    $"raw=0x{st.Raw:X6} indStage={st.IndirectStage} " +
                    $"map={source?.TextureMap.ToString() ?? "?"} coord={source?.TexCoord.ToString() ?? "?"} " +
                    $"scaleExp=({scale?.SExponent.ToString() ?? "?"},{scale?.TExponent.ToString() ?? "?"}) " +
                    $"fmt={st.Format} bias={st.BiasSelection} alpha={st.AlphaSelection} " +
                    $"matrix={st.MatrixSelection} wrap=({st.WrapS},{st.WrapT}) " +
                    $"lod={st.UseOriginalLod} addPrev={st.AddPrevious}");
            }
            foreach (var matrix in im.Matrices)
                WriteDiagnostic($"Step 6d.5 matrix material={im.MaterialIndex} index={matrix.Index} " +
                    $"complete={matrix.Complete} raw=[{string.Join(",", matrix.Commands.Select(c => $"{c.Register:X2}:{c.Raw:X6}"))}]");
            foreach (var warning in im.Warnings.Take(5))
                WriteDiagnostic($"Step 6d.5 warning material={im.MaterialIndex}: {warning}");
        }
        int unlitChannel = GetUploadedTevBatches(RegularTevMode).Count(b => b.EquationSupported && b.RasterChannel?.IsUnlit == true);
        int lightingPending = GetUploadedTevBatches(RegularTevMode).Count(b => b.EquationSupported &&
            b.RasterChannel is { IsUnlit: false });
        int missingChannel = GetUploadedTevBatches(RegularTevMode).Count(b => b.EquationSupported && b.RasterChannel is null);
        int registerSourced = GetUploadedTevBatches(RegularTevMode).Count(b => b.EquationSupported &&
            b.RasterChannel is { IsUnlit: true, MaterialRegisterComponents: > 0 });
        WriteDiagnostic($"Step 6d.3 Pass 3 GX raster channel0: {unlitChannel} equation-ready unlit channels " +
            $"(of which {registerSourced} use material register components), " +
            $"{lightingPending} require GX light evaluation, {missingChannel} lack channel state. " +
            "Ambient registers decoded; enabled lighting retains Color0 approximation.");
        int rasterReady = GetUploadedTevBatches(RegularTevMode).Count(b => b.EquationSupported && b.HasVertexRaster);
        WriteDiagnostic($"Step 6d.3 Pass 2 GX color0: {rasterReady} equation-ready surfaces with per-corner colors; " +
            $"{equationsSupported - rasterReady} still use WHITE raster approximation. " +
            "Lighting and GX channel material/ambient sources not yet evaluated.");
        int initialKnown = GetUploadedTevBatches(RegularTevMode).Where(b => b.EquationSupported)
            .Sum(b => b.InitialRegisters?.KnownChannels ?? 0);
        int initialMissing = equationsSupported * 16 - initialKnown;
        WriteDiagnostic($"Step 6d.3 TEV register channels: {initialKnown} decoded / " +
            $"{equationsSupported * 16} across {equationsSupported} equation-ready surfaces; " +
            $"{initialMissing} unset channels use zero as an EXPLICIT approximation.");
        WriteDiagnostic($"Step 6d.2 GLSL TEV equations: {equationsSupported} preview candidates / {eligible} input-ready surfaces; " +
            "decoded initial TEV registers where available; vertex Color0/channel0 where available; " +
            "unresolved GX texgens remain approximated only in previews; baseline unchanged.");
        foreach (var r in equationReject.OrderByDescending(p => p.Value))
            WriteDiagnostic($"Step 6d.2 equation fallback {r.Value}x: {r.Key}");
        WriteDiagnostic($"Step 6d.4 GX post matrices: {verifiedPostMatrix3} input-ready surfaces " +
            "have identity 3x4 matrices in the MDL0 material data for DUALTEX index 3; " +
            $"{unresolvedPostMatrix3} still approximate this index in F8 previews. " +
            "Non-identity matrices and projected texgens remain on fallback.");
        foreach (var pair in rejectCounts.OrderByDescending(p => p.Value))
            WriteDiagnostic($"Step 6d.1 reject {pair.Value}x: {pair.Key}");
        foreach (var detail in rejectionDetails)
            WriteDiagnostic("Step 6d.1 detail: " + detail);
    }

    // Checks selector bounds and structural capabilities. Missing BP material
    // initial TEV register values are not fabricated as faithful output: mode 3
    // deliberately uses zero-initialized registers and is labeled experimental.
    // Step 6d.5 Pass 4j: isolated TEV integration for the source-row-0,
    // ABC1 projected generator on Mario Circuit material 21. Stage 0 retains
    // its resolved GX UVs; stage 1 receives homogeneous S/T/Q from the same
    // frozen FIFO frame 0 post-matrix as modes 9/10. No baseline changes.
    private void UploadProjectedTevBatches(BrresModelReader.DecodeResult model)
    {
        int accepted = 0, rejected = 0;
        double cx = (model.Minimum.X + model.Maximum.X) * 0.5;
        double cy = (model.Minimum.Y + model.Maximum.Y) * 0.5;
        double cz = (model.Minimum.Z + model.Maximum.Z) * 0.5;
        foreach (var surface in model.PolygonSurfaces)
        {
            if (!model.SurfaceMaterials.TryGetValue(surface.ObjectIndex, out var material) ||
                material.MaterialIndex != 21) continue;
            int mid = material.MaterialIndex;
            if (!model.TevShaders.TryGetValue(mid, out var shader) ||
                !model.TevLayers.TryGetValue(mid, out var layers) ||
                !model.TexCoordGenerators.TryGetValue(mid, out var generators))
            { rejected++; continue; }
            var stages = shader.Stages.Where(st => st.TextureEnabled).ToArray();
            model.TevKonst.TryGetValue(mid, out var konst);
            model.TevInitialRegisters.TryGetValue(mid, out var initial);
            model.TevRasterChannels.TryGetValue(mid, out var rasterChannel);
            if (shader.Problem is not null || stages.Length != 2 ||
                stages[0].TextureMap < 0 || stages[0].TextureMap >= layers.Count ||
                stages[1].TextureMap < 0 || stages[1].TextureMap >= layers.Count ||
                !string.IsNullOrEmpty(ValidateTwoStageEquations(shader, konst)) ||
                !BrresTexCoordGenerators.TryResolveStageUvs(surface, generators,
                    stages[0].TexCoord, out var firstUvs,
                    allowUnresolvedPostMatrix3ForPreview: true) || firstUvs is null ||
                firstUvs.Count != surface.Corners.Count)
            { rejected++; WriteDiagnostic($"Step 6d.5 Pass 4j skip material={mid}: TEV/stage0 prerequisites failed"); continue; }
            var gen = generators.ForTexCoord(stages[1].TexCoord);
            // Never extend this frozen-capture approximation to other generators.
            if (gen is null || gen.SourceRow != 0 || gen.InputForm != 1 ||
                gen.Projection != 1 || gen.Type != 0 || gen.Normalize ||
                gen.MapMode != 2 || gen.DualMatrix != 3 ||
                !gen.ExactPostMatrix || stages[1].Index != 1)
            { rejected++; WriteDiagnostic($"Step 6d.5 Pass 4j skip material={mid}: stage1 GX projection signature mismatch"); continue; }
            var layer0 = layers[stages[0].TextureMap];
            var layer1 = layers[stages[1].TextureMap];
            if (!model.TexturePixels.ContainsKey(layer0.TextureName) ||
                !model.TexturePixels.ContainsKey(layer1.TextureName) ||
                layer0.WrapU is < 0 or > 2 || layer0.WrapV is < 0 or > 2 ||
                layer1.WrapU is < 0 or > 2 || layer1.WrapV is < 0 or > 2)
            { rejected++; continue; }
            bool hasColor = surface.Colors0?.Count == surface.Corners.Count;
            // xyz, stage0 uv, unused stage1 uv, vertex RGBA, projected STQ.
            const int stride = 14;
            float[] data = new float[surface.Corners.Count * stride];
            bool valid = true;
            for (int i = 0; i < surface.Corners.Count; i++)
            {
                var pt = surface.Corners[i];
                double[] v = { pt.X, pt.Y, pt.Z, 1.0 };
                double[] stq = new double[3];
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 4; col++)
                        stq[row] += FifoFrame0Post[row, col] * v[col];
                if (!double.IsFinite(stq[0]) || !double.IsFinite(stq[1]) ||
                    !double.IsFinite(stq[2]) || !double.IsFinite(firstUvs[i].X) ||
                    !double.IsFinite(firstUvs[i].Y)) { valid = false; break; }
                int j = i * stride;
                data[j] = (float)((pt.X - cx) * 0.001);
                data[j + 1] = (float)((pt.Y - cy) * 0.001);
                data[j + 2] = (float)((pt.Z - cz) * 0.001);
                data[j + 3] = (float)firstUvs[i].X;
                data[j + 4] = (float)firstUvs[i].Y;
                // Stage1 UV comes exclusively from projected S/T/Q in mode12.
                var color = hasColor ? surface.Colors0![i] : System.Windows.Media.Colors.White;
                data[j + 7] = color.R / 255f;
                data[j + 8] = color.G / 255f;
                data[j + 9] = color.B / 255f;
                data[j + 10] = color.A / 255f;
                data[j + 11] = (float)stq[0];
                data[j + 12] = (float)stq[1];
                data[j + 13] = (float)stq[2];
            }
            if (!valid) { rejected++; continue; }
            int vao = GL.GenVertexArray(), vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
            void Attrib(int location, int size, int offset)
            {
                GL.VertexAttribPointer(location, size, VertexAttribPointerType.Float, false,
                    stride * sizeof(float), offset * sizeof(float));
                GL.EnableVertexAttribArray(location);
            }
            Attrib(0, 3, 0);
            Attrib(1, 2, 3);
            Attrib(2, 2, 5);
            Attrib(3, 4, 7);
            Attrib(5, 3, 11);
            GL.BindVertexArray(0);
            RegisterTevMaterial(new TevInputBatch(vao, vbo, surface.Corners.Count,
                GetTexture(model, layer0.TextureName, layer0.WrapU, layer0.WrapV),
                GetTexture(model, layer1.TextureName, layer1.WrapU, layer1.WrapV),
                mid, stages[0].Index, stages[1].Index, stages[0], stages[1],
                konst, initial, rasterChannel, hasColor, true, ProjectedStage1: true), ProjectedTevMode);
            accepted++;
        }
        WriteDiagnostic($"Step 6d.5 Pass 4j projected TEV: {accepted} material=21 surfaces ready; " +
            $"{rejected} rejected; stage0=XF UV, stage1=frozen FIFO frame0 homogeneous STQ " +
            "(raw ABC1 source -> identity TEXMTX60 -> POSTMTX3). " +
            "F8 mode 12 ONLY; not live GX camera reconstruction; baseline and mode6 unchanged.");
    }

    private static string ValidateTwoStageEquations(BrresTevDiagnostics.Shader shader,
        BrresTevDiagnostics.MaterialKonst? konst, bool allowIndirect = false)
    {
        if (shader.Stages.Count != 2 || shader.Stages.Any(s => !s.TextureEnabled))
            return "requires exactly two texture-enabled stages";
        foreach (var s in shader.Stages)
        {
            if (s.IndirectCommand != 0 && !allowIndirect) return "indirect TEV";
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

    private void UploadIndirectPreviewBatches(BrresModelReader.DecodeResult model)
    {
        int ready = 0, approximateUv = 0, rejected = 0;
        double cx = (model.Minimum.X + model.Maximum.X) * 0.5;
        double cy = (model.Minimum.Y + model.Maximum.Y) * 0.5;
        double cz = (model.Minimum.Z + model.Maximum.Z) * 0.5;
        foreach (var surface in model.PolygonSurfaces)
        {
            if (!model.SurfaceMaterials.TryGetValue(surface.ObjectIndex, out var surfaceMaterial) ||
                !model.TevIndirect.TryGetValue(surfaceMaterial.MaterialIndex, out var im) ||
                !im.UsesIndirect) continue;
            int mid = surfaceMaterial.MaterialIndex;
            var stage = im.Stages.FirstOrDefault(s => s.Active);
            if (stage is null || !model.TevLayers.TryGetValue(mid, out var layers) ||
                !model.TexCoordGenerators.TryGetValue(mid, out var gens) ||
                stage.IndirectStage >= im.Sources.Count || stage.IndirectStage >= im.Scales.Count)
            { rejected++; WriteDiagnostic($"Step 6d.5 Pass 2 skip material={mid}: missing stage/source/scale/layers"); continue; }
            var source = im.Sources[stage.IndirectStage];
            var coordScale = im.Scales[stage.IndirectStage];
            if (source.TextureMap < 0 || source.TextureMap >= layers.Count ||
                stage.Format != 0 || stage.MatrixSelection is < 1 or > 3 ||
                stage.AlphaSelection != 0 || stage.WrapS != 0 || stage.WrapT != 0 ||
                stage.AddPrevious || stage.UseOriginalLod)
            { rejected++; WriteDiagnostic($"Step 6d.5 Pass 2 skip material={mid}: unsupported indirect mode/source"); continue; }
            var matrix = im.Matrices.FirstOrDefault(x => x.Index == stage.MatrixSelection - 1 && x.Complete);
            if (matrix is null)
            { rejected++; WriteDiagnostic($"Step 6d.5 Pass 2 skip material={mid}: missing matrix"); continue; }
            var commands = matrix.Commands.OrderBy(x => x.Register).ToArray();
            if (commands.Length != 3) { rejected++; continue; }
            var uvValid = BrresTexCoordGenerators.TryResolveStageUvs(surface, gens, source.TexCoord,
                out var resolvedUv, allowUnresolvedPostMatrix3ForPreview: true) &&
                resolvedUv?.Count == surface.Corners.Count;
            // For non-direct/projected sources, preview raw UV as a *diagnostic substitute*.
            // Never present this as a faithful GX indirect coordinate generator.
            var uv = uvValid ? resolvedUv :
                source.TexCoord == 1 && surface.Uvs1?.Count == surface.Corners.Count ? surface.Uvs1 :
                surface.Uvs?.Count == surface.Corners.Count ? surface.Uvs : null;
            if (uv is null) { rejected++; WriteDiagnostic($"Step 6d.5 Pass 2 skip material={mid}: no usable preview UV"); continue; }
            bool approximated = !uvValid;
            if (approximated) approximateUv++;
            var layer = layers[source.TextureMap];
            if (!model.TexturePixels.ContainsKey(layer.TextureName) ||
                layer.WrapU is < 0 or > 2 || layer.WrapV is < 0 or > 2)
            { rejected++; continue; }
            var vertices = new float[surface.Corners.Count * 5];
            bool finite = true;
            for (int i = 0, at = 0; i < surface.Corners.Count; i++)
            {
                if (!double.IsFinite(uv[i].X) || !double.IsFinite(uv[i].Y) ||
                    Math.Abs(uv[i].X) > 100000 || Math.Abs(uv[i].Y) > 100000)
                { finite = false; break; }
                var point = surface.Corners[i];
                vertices[at++] = (float)((point.X - cx) * .001);
                vertices[at++] = (float)((point.Y - cy) * .001);
                vertices[at++] = (float)((point.Z - cz) * .001);
                vertices[at++] = (float)uv[i].X * coordScale.SFactor;
                vertices[at++] = (float)uv[i].Y * coordScale.TFactor;
            }
            if (!finite) { rejected++; continue; }
            int vao = GL.GenVertexArray(), vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.BindVertexArray(0);
            // BP stores each matrix column as two signed Q10 coefficients.
            // Three 2-bit exponent fields make the 6-bit exponent (biased by 17).
            float[] rowS = commands.Select(c => c.Coefficient0 / 1024f).ToArray();
            float[] rowT = commands.Select(c => c.Coefficient1 / 1024f).ToArray();
            int exponent = commands[0].ExponentBits | (commands[1].ExponentBits << 2) |
                           (commands[2].ExponentBits << 4);
            float matrixScale = MathF.Pow(2f, exponent - 17);
            int texture = GetTexture(model, layer.TextureName, layer.WrapU, layer.WrapV);
            _indirectPreviewBatches.Add(new IndirectPreviewBatch(vao, vbo, surface.Corners.Count,
                texture, mid, stage.Index, approximated, rowS, rowT, matrixScale, stage.BiasSelection));
            // Pass 3: opt-in final TEV preview for material 2 only. Never use the
            // projected / RAW FALLBACK indirect generator of material 1.
            if (mid == 2 && !approximated)
                TryUploadIndirectTevBatch(model, surface, mid, texture, uv,
                    rowS, rowT, matrixScale, stage.BiasSelection, cx, cy, cz);
            ready++;
            WriteDiagnostic($"Step 6d.5 Pass 2 material={mid}: map={source.TextureMap} coord={source.TexCoord} " +
                $"matrixScale=2^({exponent - 17}) bias={stage.BiasSelection} " +
                $"UV={(approximated ? "RAW FALLBACK (not GX-exact)" : "XF generated")}");
        }
        WriteDiagnostic($"Step 6d.5 Pass 3 GPU offset+TEV preview: {GetUploadedTevBatches(IndirectTevMode).Count} surfaces ready; " +
            $"{GetUploadedTevBatches(IndirectTevMode).Count(b => b.ApproximateRegularUv)} with approximated regular TEV UVs. " +
            "F8 mode 6 only; no baseline changes.");
        WriteDiagnostic($"Step 6d.5 Pass 2 GPU indirect preview: {ready} surfaces, " +
            $"{approximateUv} use approximate source UV; {rejected} skipped. " +
            "F8 mode 4 = sampled indirect texture, mode 5 = mapped offset visualization. " +
            "Neither mode alters final TEV output.");
    }

    // Experimental stage-combination path. Regular TEV texgens may still be
    // approximated; indirect source UV must be verified to enable this batch.
    private void TryUploadIndirectTevBatch(BrresModelReader.DecodeResult model,
        BrresModelReader.PolygonSurface surface, int mid, int indirectTexture,
        IReadOnlyList<System.Windows.Point> indirectUv,
        float[] rowS, float[] rowT, float matrixScale, int bias,
        double cx, double cy, double cz)
    {
        if (!model.TevShaders.TryGetValue(mid, out var shader) ||
            !model.TevLayers.TryGetValue(mid, out var layers) ||
            !model.TexCoordGenerators.TryGetValue(mid, out var generators) ||
            !model.TevIndirect.TryGetValue(mid, out var indirect)) return;
        var stages = shader.Stages.Where(x => x.TextureEnabled).Take(3).ToArray();
        if (stages.Length != 2 || shader.Stages.Count != 2 ||
            stages.Any(x => x.TextureMap < 0 || x.TextureMap >= layers.Count ||
                             x.RasterSwap != 0 || x.TextureSwap != 0)) return;
        var active = indirect.Stages.Where(x => x.Active).ToArray();
        if (active.Length != 2 || active.Any(x => x.Index is < 0 or > 1 ||
            x.Format != 0 || x.BiasSelection != bias ||
            x.MatrixSelection != 1 || x.WrapS != 0 || x.WrapT != 0 ||
            x.AlphaSelection != 0 || x.AddPrevious || x.UseOriginalLod ||
            x.IndirectStage != 0)) return;
        // Reuse the existing equation validator, allowing *only* the already
        // decoded, validated indirect stage commands for this separate mode.
        model.TevKonst.TryGetValue(mid, out var konst);
        if (ValidateTwoStageEquations(shader, konst, allowIndirect: true).Length != 0) return;
        IReadOnlyList<System.Windows.Point>?[] stageUvs = new IReadOnlyList<System.Windows.Point>?[2];
        bool approximate = false;
        for (int j = 0; j < 2; ++j)
        {
            bool valid = BrresTexCoordGenerators.TryResolveStageUvs(surface, generators,
                stages[j].TexCoord, out var resolved, allowUnresolvedPostMatrix3ForPreview: true) &&
                resolved?.Count == surface.Corners.Count;
            if (valid) stageUvs[j] = resolved!.ToArray();
            else
            {
                // This is an approximation of the *regular* TEV coordinate,
                // not of the verified indirect coordinate. Clearly logged.
                var fallback = surface.Uvs?.Count == surface.Corners.Count ? surface.Uvs : null;
                if (fallback is null) return;
                stageUvs[j] = fallback.ToArray();
                approximate = true;
            }
        }
        if (indirectUv.Count != surface.Corners.Count) return;
        var layer0 = layers[stages[0].TextureMap];
        var layer1 = layers[stages[1].TextureMap];
        if (!model.TexturePixels.ContainsKey(layer0.TextureName) ||
            !model.TexturePixels.ContainsKey(layer1.TextureName)) return;
        bool hasRaster = surface.Colors0?.Count == surface.Corners.Count;
        float[] packed = new float[surface.Corners.Count * 13];
        for (int i = 0, k = 0; i < surface.Corners.Count; ++i)
        {
            var a = stageUvs[0]![i]; var b = stageUvs[1]![i]; var ind = indirectUv[i];
            if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) ||
                !double.IsFinite(b.X) || !double.IsFinite(b.Y) ||
                !double.IsFinite(ind.X) || !double.IsFinite(ind.Y) ||
                Math.Abs(a.X) > 100000 || Math.Abs(a.Y) > 100000 ||
                Math.Abs(b.X) > 100000 || Math.Abs(b.Y) > 100000 ||
                Math.Abs(ind.X) > 100000 || Math.Abs(ind.Y) > 100000) return;
            var pt = surface.Corners[i];
            packed[k++] = (float)((pt.X - cx) * .001);
            packed[k++] = (float)((pt.Y - cy) * .001);
            packed[k++] = (float)((pt.Z - cz) * .001);
            packed[k++] = (float)a.X; packed[k++] = (float)a.Y;
            packed[k++] = (float)b.X; packed[k++] = (float)b.Y;
            var col = hasRaster ? surface.Colors0![i] : System.Windows.Media.Colors.White;
            packed[k++] = col.R / 255f; packed[k++] = col.G / 255f;
            packed[k++] = col.B / 255f; packed[k++] = col.A / 255f;
            packed[k++] = (float)ind.X; packed[k++] = (float)ind.Y;
        }
        int vao = GL.GenVertexArray(), vbo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, packed.Length * sizeof(float), packed, BufferUsageHint.StaticDraw);
        for (int attribute = 0; attribute < 5; attribute++)
        {
            int[] widths = [3, 2, 2, 4, 2]; int[] offsets = [0, 3, 5, 7, 11];
            GL.VertexAttribPointer(attribute, widths[attribute], VertexAttribPointerType.Float,
                false, 13 * sizeof(float), offsets[attribute] * sizeof(float));
            GL.EnableVertexAttribArray(attribute);
        }
        GL.BindVertexArray(0);
        model.TevInitialRegisters.TryGetValue(mid, out var initial);
        model.TevRasterChannels.TryGetValue(mid, out var raster);
        RegisterTevMaterial(new TevInputBatch(vao, vbo, surface.Corners.Count,
            GetTexture(model, layer0.TextureName, layer0.WrapU, layer0.WrapV),
            GetTexture(model, layer1.TextureName, layer1.WrapU, layer1.WrapV),
            mid, stages[0].Index, stages[1].Index, stages[0], stages[1],
            konst, initial, raster, hasRaster, true, indirectTexture,
            rowS, rowT, matrixScale, bias, approximate), IndirectTevMode);
        WriteDiagnostic($"Step 6d.5 Pass 3 material={mid}: indirect offsets enabled for TEV stages 0/1; " +
            $"regular UV={(approximate ? "RAW UV0 fallback (NOT GX-exact)" : "XF verified")}; " +
            "indirect UV=XF verified; preview-only, GX precision not guaranteed.");
    }

    // Step 6d.5 Pass 4: select only GX position-projected generators for an
    // isolated preview. The material's runtime XF texture matrices have not
    // been reconstructed, so camera projection is a DIAGNOSTIC substitute.
    private void UploadProjectedPreviewBatches(BrresModelReader.DecodeResult model)
    {
        double cx = (model.Minimum.X + model.Maximum.X) * 0.5;
        double cy = (model.Minimum.Y + model.Maximum.Y) * 0.5;
        double cz = (model.Minimum.Z + model.Maximum.Z) * 0.5;
        int selected = 0, uploaded = 0, rejected = 0;
        int staticEffectPresent = 0, explicitXfWords = 0, requiresRuntime = 0;
        foreach (var surface in model.PolygonSurfaces)
        {
            if (!model.SurfaceMaterials.TryGetValue(surface.ObjectIndex, out var material) ||
                !model.TexCoordGenerators.TryGetValue(material.MaterialIndex, out var generators) ||
                !model.TevShaders.TryGetValue(material.MaterialIndex, out var shader) ||
                !model.TevLayers.TryGetValue(material.MaterialIndex, out var layers))
                continue;
            foreach (var stage in shader.Stages.Where(s => s.TextureEnabled))
            {
                var gen = generators.ForTexCoord(stage.TexCoord);
                if (gen is null || gen.SourceRow != 0 || gen.Projection != 1 || gen.MapMode != 2)
                    continue;
                selected++;
                if (gen.HasFiniteAuthoredEffectMatrix) staticEffectPresent++;
                explicitXfWords += generators.ExplicitXfMatrixWords.Count;
                if (gen.NeedsRuntimeCameraMatrix && generators.ExplicitXfMatrices.Count == 0)
                    requiresRuntime++;
                WriteDiagnostic($"Step 6d.5 Pass 4b projected matrix provenance: " +
                    $"object={surface.ObjectIndex} material={material.MaterialIndex} " +
                    $"stage={stage.Index} mapMode={gen.MapMode} " +
                    $"cameraRef={gen.CameraReference} lightRef={gen.LightReference} " +
                    $"authoredEffect={(gen.HasFiniteAuthoredEffectMatrix ? (gen.VerifiedIdentityPostMatrix ? "identity" : "nonidentity") : "invalid")} " +
                    $"XFmatrixWords={generators.ExplicitXfMatrixWords.Count} " +
                    $"completeXF3x4={generators.ExplicitXfMatrices.Count} " +
                    "matrix association unresolved; cannot claim exact GX projection");
                if (gen.Type != 0 || gen.Normalize || !gen.EffectIdentity ||
                    !gen.ExactPostMatrix || stage.TextureMap < 0 ||
                    stage.TextureMap >= layers.Count || surface.Corners.Count == 0)
                { rejected++; continue; }
                var layer = layers[stage.TextureMap];
                if (!model.TexturePixels.ContainsKey(layer.TextureName) ||
                    layer.WrapU is < 0 or > 2 || layer.WrapV is < 0 or > 2)
                { rejected++; continue; }
                float[] vertices = new float[surface.Corners.Count * 3];
                bool finite = true;
                for (int i = 0; i < surface.Corners.Count; i++)
                {
                    var pt = surface.Corners[i];
                    double x = (pt.X - cx) * .001;
                    double y = (pt.Y - cy) * .001;
                    double z = (pt.Z - cz) * .001;
                    if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                    { finite = false; break; }
                    vertices[i * 3] = (float)x;
                    vertices[i * 3 + 1] = (float)y;
                    vertices[i * 3 + 2] = (float)z;
                }
                if (!finite) { rejected++; continue; }
                int vao = GL.GenVertexArray(), vbo = GL.GenBuffer();
                GL.BindVertexArray(vao);
                GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float),
                    vertices, BufferUsageHint.StaticDraw);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float,
                    false, 3 * sizeof(float), 0);
                GL.EnableVertexAttribArray(0);
                GL.BindVertexArray(0);
                int texture = GetTexture(model, layer.TextureName, layer.WrapU, layer.WrapV);
                _projectedPreviewBatches.Add(new ProjectedPreviewBatch(vao, vbo,
                    surface.Corners.Count, texture, material.MaterialIndex, stage.Index));
                uploaded++;
                WriteDiagnostic($"Step 6d.5 Pass 4 candidate: object={surface.ObjectIndex} " +
                    $"material={material.MaterialIndex} stage={stage.Index} map={stage.TextureMap} " +
                    $"sourceRow={gen.SourceRow} projection={gen.Projection} mapMode={gen.MapMode} " +
                    $"postIdentity={gen.ExactPostMatrix}; GPU camera-space approximation only");
            }
        }
        WriteDiagnostic($"Step 6d.5 Pass 4b matrix reconstruction: {selected} projected candidates; " +
            $"{staticEffectPresent} authored finite 3x4 effects; " +
            $"{explicitXfWords} XF matrix words observed; " +
            $"{requiresRuntime} require runtime matrix state (e.g. SCN0 camera). " +
            "No unverified matrix is substituted for the Wii XF projection; mode 7 remains camera DIAGNOSTIC.");
        WriteDiagnostic($"Step 6d.5 Pass 4 projected texgen: {selected} candidates; " +
            $"{uploaded} camera-projection preview surfaces; {rejected} rejected. " +
            "F8 mode 7 ONLY; GX XF position/texture matrix reconstruction pending.");
    }

    private void DrawProjectedPreview(Matrix4 viewProjection)
    {
        if (_projectedPreviewBatches.Count == 0) return;
        if (_projectedProgram == 0) InitializeProjectedPreviewProgram();
        GL.UseProgram(_projectedProgram);
        GL.UniformMatrix4(_projectedMatrixLocation, false, ref viewProjection);
        GL.Uniform1(_projectedSamplerLocation, 0);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(false);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(-1f, -1f);
        try
        {
            foreach (var batch in _projectedPreviewBatches)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, batch.Texture);
                GL.BindVertexArray(batch.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, batch.VertexCount);
            }
        }
        finally
        {
            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.DepthMask(true);
            GL.Enable(EnableCap.Blend);
            GL.BindVertexArray(0);
        }
    }

    private void InitializeProjectedPreviewProgram()
    {
        const string vertex = """
            #version 330 core
            layout(location = 0) in vec3 aPosition;
            uniform mat4 uViewProjection;
            out vec3 vProjectedStq;
            void main() {
                vec4 clip = uViewProjection * vec4(aPosition, 1.0);
                gl_Position = clip;
                // Homogeneous, perspective-correct S,T,Q interpolation.
                // Uses the viewer camera matrix, NOT the missing Wii XF matrix.
                vProjectedStq = vec3(clip.xy * 0.5 + clip.w * 0.5, clip.w);
            }
            """;
        const string fragment = """
            #version 330 core
            in vec3 vProjectedStq;
            uniform sampler2D uTexture;
            out vec4 FragColor;
            void main() {
                if (vProjectedStq.z <= 0.00001) discard;
                vec2 uv = vProjectedStq.xy / vProjectedStq.z;
                FragColor = texture(uTexture, uv);
                if (FragColor.a <= 0.0039) discard;
            }
            """;
        int vs = Compile(ShaderType.VertexShader, vertex);
        int fs = Compile(ShaderType.FragmentShader, fragment);
        int program = GL.CreateProgram();
        GL.AttachShader(program, vs); GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
        string log = GL.GetProgramInfoLog(program);
        GL.DetachShader(program, vs); GL.DetachShader(program, fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        if (ok == 0) { GL.DeleteProgram(program); throw new InvalidOperationException("Projected preview shader: " + log); }
        _projectedProgram = program;
        _projectedMatrixLocation = GL.GetUniformLocation(program, "uViewProjection");
        _projectedSamplerLocation = GL.GetUniformLocation(program, "uTexture");
    }

    // Step 6d.5 Pass 4f: a deliberately STATIC replay of the verified GX state
    // from frame 0 of the user's Dolphin FIFO capture. Texture coords derive
    // from the unscaled original model position; display coords use the normal
    // viewer re-centering. Camera rotation should NOT re-project the texture.
    private void UploadFifoProjectionBatches(BrresModelReader.DecodeResult model)
    {
        double cx = (model.Minimum.X + model.Maximum.X) * 0.5;
        double cy = (model.Minimum.Y + model.Maximum.Y) * 0.5;
        double cz = (model.Minimum.Z + model.Maximum.Z) * 0.5;
        int accepted = 0, rejected = 0, invalidQ = 0, nonfinite = 0;
        int positiveQ = 0, negativeQ = 0, zeroCrossingTriangles = 0;
        int cornersExported = 0;
        string exportPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "DebugManager-Pass4g-Material21-Corners.csv");
        System.Text.StringBuilder export = new("object,corner,x,y,z,eye_x,eye_y,eye_z,s,t,q,u,v\n");
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        double minQ = double.PositiveInfinity, maxQ = double.NegativeInfinity;
        foreach (var surface in model.PolygonSurfaces)
        {
            if (!model.SurfaceMaterials.TryGetValue(surface.ObjectIndex, out var material) ||
                material.MaterialIndex != 21 ||
                !model.TexCoordGenerators.TryGetValue(material.MaterialIndex, out var gens) ||
                !model.TevShaders.TryGetValue(material.MaterialIndex, out var shader) ||
                !model.TevLayers.TryGetValue(material.MaterialIndex, out var layers))
                continue;
            foreach (var stage in shader.Stages.Where(s => s.TextureEnabled))
            {
                var gen = gens.ForTexCoord(stage.TexCoord);
                if (gen is null || gen.SourceRow != 0 || gen.Projection != 1 ||
                    gen.MapMode != 2 || gen.Normalize || gen.Type != 0 ||
                    stage.TextureMap < 0 || stage.TextureMap >= layers.Count)
                    continue;
                var layer = layers[stage.TextureMap];
                if (!model.TexturePixels.ContainsKey(layer.TextureName) ||
                    surface.Corners.Count == 0 || layer.WrapU is < 0 or > 2 ||
                    layer.WrapV is < 0 or > 2)
                { rejected++; continue; }

                // xyz + (S,T,Q) all at ORIGINAL triangle corners. Transform in
                // double precision before narrowing to GPU float. Translation in
                // the recorded GX model-view matrix is ~-12866, so applying it to
                // already recentered/scaled positions would be demonstrably wrong.
                var vertices = new float[surface.Corners.Count * 6];
                bool valid = true;
                for (int i = 0; i < surface.Corners.Count; i++)
                {
                    var pt = surface.Corners[i];
                    double[] input = { pt.X, pt.Y, pt.Z, 1.0 };
                    double[] eye = new double[4];
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 4; c++) eye[r] += FifoFrame0Position[r, c] * input[c];
                    eye[3] = 1.0;
                    double[] stq = new double[3];
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 4; c++) stq[r] += FifoFrame0Post[r, c] * eye[c];
                    if (!double.IsFinite(stq[0]) || !double.IsFinite(stq[1]) ||
                        !double.IsFinite(stq[2]) ||
                        !double.IsFinite(pt.X) || !double.IsFinite(pt.Y) || !double.IsFinite(pt.Z))
                    { valid = false; nonfinite++; break; }
                    if (Math.Abs(stq[2]) < 1e-7) invalidQ++;
                    else if (stq[2] > 0) positiveQ++;
                    else negativeQ++;
                    minX = Math.Min(minX, pt.X); maxX = Math.Max(maxX, pt.X);
                    minY = Math.Min(minY, pt.Y); maxY = Math.Max(maxY, pt.Y);
                    minZ = Math.Min(minZ, pt.Z); maxZ = Math.Max(maxZ, pt.Z);
                    // Export original BRRES corners independently from the frozen
                    // GPU projection. FIFO vertex input can be compared against
                    // these source positions without any viewer camera dependence.
                    var culture = CultureInfo.InvariantCulture;
                    double u = Math.Abs(stq[2]) > 1e-12 ? stq[0] / stq[2] : double.NaN;
                    double v = Math.Abs(stq[2]) > 1e-12 ? stq[1] / stq[2] : double.NaN;
                    export.Append(surface.ObjectIndex).Append(',').Append(i);
                    foreach (double value in new[] { pt.X, pt.Y, pt.Z, eye[0], eye[1], eye[2], stq[0], stq[1], stq[2], u, v })
                        export.Append(',').Append(value.ToString("R", culture));
                    export.Append('\n');
                    cornersExported++;
                    if (i % 3 == 2)
                    {
                        // Inspect crossing of the homogeneous Q plane in the
                        // decoded triangle strip-expanded surface.
                        // Use S/T/Q only; avoid camera clip-space assumptions.
                        double qa = vertices[(i-2)*6+5], qb = vertices[(i-1)*6+5];
                        if (Math.Min(qa, Math.Min(qb, stq[2])) < 0 &&
                            Math.Max(qa, Math.Max(qb, stq[2])) > 0)
                            zeroCrossingTriangles++;
                    }
                    minQ = Math.Min(minQ, stq[2]);
                    maxQ = Math.Max(maxQ, stq[2]);
                    int j = i * 6;
                    vertices[j] = (float)((pt.X - cx) * 0.001);
                    vertices[j + 1] = (float)((pt.Y - cy) * 0.001);
                    vertices[j + 2] = (float)((pt.Z - cz) * 0.001);
                    vertices[j + 3] = (float)stq[0];
                    vertices[j + 4] = (float)stq[1];
                    vertices[j + 5] = (float)stq[2];
                }
                if (!valid) { rejected++; continue; }
                int vao = GL.GenVertexArray(), vbo = GL.GenBuffer();
                GL.BindVertexArray(vao);
                GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float),
                    vertices, BufferUsageHint.StaticDraw);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float,
                    false, 6 * sizeof(float), 0);
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float,
                    false, 6 * sizeof(float), 3 * sizeof(float));
                GL.EnableVertexAttribArray(1);
                GL.BindVertexArray(0);
                int texture = GetTexture(model, layer.TextureName, layer.WrapU, layer.WrapV);
                _fifoProjectionBatches.Add(new FifoProjectionBatch(vao, vbo,
                    surface.Corners.Count, texture, material.MaterialIndex, stage.Index));
                accepted++;
            }
        }
        if (cornersExported > 0)
        {
            try
            {
                System.IO.File.WriteAllText(exportPath, export.ToString());
                WriteDiagnostic($"Step 6d.5 Pass 4g vertex export: {cornersExported} corners " +
                    $"to {exportPath}; BRRES source XYZ bounds=" +
                    $"[{minX:G9},{minY:G9},{minZ:G9}]..[{maxX:G9},{maxY:G9},{maxZ:G9}]; " +
                    $"Q positive={positiveQ}, negative={negativeQ}, near-zero={invalidQ}, " +
                    $"Q-crossing triangles={zeroCrossingTriangles}. " +
                    "Compare this CSV with FIFO vertex arrays; not yet evidence of GX-exact projection.");
            }
            catch (Exception ex) { WriteDiagnostic("Step 6d.5 Pass 4g export failed: " + ex.Message); }
        }
        WriteDiagnostic($"Step 6d.5 Pass 4f FIFO frame=0 static GX replay: {accepted} " +
            $"material=21 surfaces uploaded, {rejected} rejected, {nonfinite} nonfinite " +
            $"corners, {invalidQ} near-zero Q corners; Q range=[{minQ:G7},{maxQ:G7}]. " +
            "POSMTX0 -> identity TEXMTX60 -> POSTMTX3 (XF 0x050C); " +
            "unscaled source corners, double precision CPU transform. F8 mode 8 ONLY. " +
            "Capture-frame reference, NOT live GX-exact camera reconstruction.");
    }

    // Step 6d.5 Pass 4h: independent reference calculation following Dolphin's
    // VertexShaderGen.cpp source-row and texgen transform order.
    // SourceRow=Geom selects rawpos.xyz, not the POSMTX-transformed position.
    // TEXMTX60 is GX identity; inputform AB11 (value 0) forces source.z=1;
    // inputform ABC1 (value 1) preserves source.z. A 3x4
    // post-transform is then applied to (S,T,Q,1). No additional POSMTX.
    // This is a deliberately frozen FIFO frame 0 test, not live camera data.
    private void UploadGxTexgenOrderBatches(BrresModelReader.DecodeResult model)
    {
        double cx = (model.Minimum.X + model.Maximum.X) * 0.5;
        double cy = (model.Minimum.Y + model.Maximum.Y) * 0.5;
        double cz = (model.Minimum.Z + model.Maximum.Z) * 0.5;
        int accepted = 0, rejected = 0, nonfinite = 0;
        int positiveQ = 0, negativeQ = 0, zeroQ = 0, crossing = 0;
        double minQ = double.PositiveInfinity, maxQ = double.NegativeInfinity;
        double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
        double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
        double maxDifferenceMode8 = 0;
        var csv = new System.Text.StringBuilder("object,corner,x,y,z,source_z,s,t,q,u,v,mode8_u,mode8_v\n");
        var culture = CultureInfo.InvariantCulture;
        foreach (var surface in model.PolygonSurfaces)
        {
            if (!model.SurfaceMaterials.TryGetValue(surface.ObjectIndex, out var material) ||
                material.MaterialIndex != 21 ||
                !model.TexCoordGenerators.TryGetValue(material.MaterialIndex, out var gens) ||
                !model.TevShaders.TryGetValue(material.MaterialIndex, out var shader) ||
                !model.TevLayers.TryGetValue(material.MaterialIndex, out var layers))
                continue;
            foreach (var stage in shader.Stages.Where(s => s.TextureEnabled))
            {
                var gen = gens.ForTexCoord(stage.TexCoord);
                if (gen is null || gen.SourceRow != 0 || gen.Projection != 1 ||
                    gen.MapMode != 2 || gen.Normalize || gen.Type != 0 ||
                    stage.TextureMap < 0 || stage.TextureMap >= layers.Count)
                    continue;
                var layer = layers[stage.TextureMap];
                if (!model.TexturePixels.ContainsKey(layer.TextureName) ||
                    surface.Corners.Count == 0 || layer.WrapU is < 0 or > 2 ||
                    layer.WrapV is < 0 or > 2)
                { rejected++; continue; }
                float[] vertices = new float[surface.Corners.Count * 6];
                bool valid = true;
                for (int i = 0; i < surface.Corners.Count; ++i)
                {
                    var p = surface.Corners[i];
                    // Dolphin TexInputForm: AB11=0 forces coord.z=1.0; ABC1=1
                    // retains coord.z from SourceRow::Geom (rawpos.z).
                    // Pass 4h initially inverted this flag, forcing Q near -1
                    // and producing severe repeat tiling in the preview.
                    double srcZ = gen.InputForm == 0 ? 1.0 : p.Z;
                    double[] src = { p.X, p.Y, srcZ, 1.0 };
                    double[] stq = new double[3];
                    for (int row = 0; row < 3; ++row)
                        for (int col = 0; col < 4; ++col)
                            stq[row] += FifoFrame0Post[row, col] * src[col];
                    if (!double.IsFinite(stq[0]) || !double.IsFinite(stq[1]) ||
                        !double.IsFinite(stq[2]) || !double.IsFinite(p.X) ||
                        !double.IsFinite(p.Y) || !double.IsFinite(p.Z))
                    { nonfinite++; valid = false; break; }
                    double q = stq[2];
                    if (Math.Abs(q) < 1e-7) zeroQ++;
                    else if (q > 0) positiveQ++;
                    else negativeQ++;
                    minQ = Math.Min(minQ, q); maxQ = Math.Max(maxQ, q);
                    double u = Math.Abs(q) > 1e-12 ? stq[0] / q : double.NaN;
                    double v = Math.Abs(q) > 1e-12 ? stq[1] / q : double.NaN;
                    if (double.IsFinite(u)) { minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); }
                    if (double.IsFinite(v)) { minV = Math.Min(minV, v); maxV = Math.Max(maxV, v); }
                    // Compute the previous Pass 4f path for a direct, exact
                    // per-corner comparison, without changing F8 mode 8.
                    double[] eye = new double[4];
                    double[] original = { p.X, p.Y, p.Z, 1.0 };
                    for (int row = 0; row < 3; row++)
                        for (int col = 0; col < 4; col++)
                            eye[row] += FifoFrame0Position[row, col] * original[col];
                    eye[3] = 1.0;
                    double[] oldStq = new double[3];
                    for (int row = 0; row < 3; row++)
                        for (int col = 0; col < 4; col++)
                            oldStq[row] += FifoFrame0Post[row, col] * eye[col];
                    double oldU = Math.Abs(oldStq[2]) > 1e-12 ? oldStq[0] / oldStq[2] : double.NaN;
                    double oldV = Math.Abs(oldStq[2]) > 1e-12 ? oldStq[1] / oldStq[2] : double.NaN;
                    if (double.IsFinite(oldU) && double.IsFinite(u))
                        maxDifferenceMode8 = Math.Max(maxDifferenceMode8, Math.Abs(oldU - u));
                    if (double.IsFinite(oldV) && double.IsFinite(v))
                        maxDifferenceMode8 = Math.Max(maxDifferenceMode8, Math.Abs(oldV - v));
                    csv.Append(surface.ObjectIndex).Append(',').Append(i);
                    foreach (double value in new[] { p.X, p.Y, p.Z, srcZ, stq[0], stq[1], q, u, v, oldU, oldV })
                        csv.Append(',').Append(value.ToString("R", culture));
                    csv.Append('\n');
                    if (i % 3 == 2)
                    {
                        double qa = vertices[(i - 2) * 6 + 5];
                        double qb = vertices[(i - 1) * 6 + 5];
                        if (Math.Min(qa, Math.Min(qb, q)) < 0 &&
                            Math.Max(qa, Math.Max(qb, q)) > 0) crossing++;
                    }
                    int j = i * 6;
                    vertices[j] = (float)((p.X - cx) * 0.001);
                    vertices[j + 1] = (float)((p.Y - cy) * 0.001);
                    vertices[j + 2] = (float)((p.Z - cz) * 0.001);
                    vertices[j + 3] = (float)stq[0];
                    vertices[j + 4] = (float)stq[1];
                    vertices[j + 5] = (float)q;
                }
                if (!valid) { rejected++; continue; }
                int vao = GL.GenVertexArray(), vbo = GL.GenBuffer();
                GL.BindVertexArray(vao);
                GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float),
                    vertices, BufferUsageHint.StaticDraw);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float,
                    false, 6 * sizeof(float), 0);
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float,
                    false, 6 * sizeof(float), 3 * sizeof(float));
                GL.EnableVertexAttribArray(1);
                GL.BindVertexArray(0);
                int texture = GetTexture(model, layer.TextureName, layer.WrapU, layer.WrapV);
                _fifoTexgenOrderBatches.Add(new FifoProjectionBatch(vao, vbo,
                    surface.Corners.Count, texture, material.MaterialIndex, stage.Index));
                accepted++;
                WriteDiagnostic($"Step 6d.5 Pass 4h generator material=21: " +
                    $"sourceRow={gen.SourceRow} inputForm={gen.InputForm} " +
                    $"({(gen.InputForm == 0 ? "AB11 z=1" : "ABC1 retain z")}) " +
                    $"projection={gen.Projection} postMatrix=3; " +
                    "raw GX source, not modelview position. F8 mode 9 only.");
            }
        }
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "DebugManager-Pass4h-Material21-GX-Texgen.csv");
        try { if (accepted > 0) System.IO.File.WriteAllText(path, csv.ToString()); }
        catch (Exception ex) { WriteDiagnostic("Step 6d.5 Pass 4h CSV: " + ex.Message); }
        WriteDiagnostic($"Step 6d.5 Pass 4h GX texgen order: {accepted} surfaces; " +
            $"{rejected} rejected, {nonfinite} nonfinite; Q pos={positiveQ} " +
            $"neg={negativeQ} near-zero={zeroQ} crossing-triangles={crossing}; " +
            $"Q=[{minQ:G9},{maxQ:G9}], U=[{minU:G9},{maxU:G9}], " +
            $"V=[{minV:G9},{maxV:G9}]; max|mode9-mode8 UV|={maxDifferenceMode8:G9}; " +
            $"CSV={path}. Frozen FIFO frame 0; texture-matrix identity, " +
            "post-matrix 3; inputForm corrected (AB11=0, ABC1=1). " +
            "Projection still experimental; modes 0-8 unchanged.");
    }

    private void DrawFifoProjectionPreview(Matrix4 viewProjection,
        List<FifoProjectionBatch>? selectedBatches = null)
    {
        var drawBatches = selectedBatches ?? _fifoProjectionBatches;
        if (drawBatches.Count == 0) return;
        if (_fifoProjectionProgram == 0) InitializeFifoProjectionProgram();
        GL.UseProgram(_fifoProjectionProgram);
        GL.UniformMatrix4(_fifoProjectionMatrixLocation, false, ref viewProjection);
        GL.Uniform1(_fifoProjectionSamplerLocation, 0);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(false);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(-1f, -1f);
        try
        {
            foreach (var batch in drawBatches)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, batch.Texture);
                GL.BindVertexArray(batch.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, batch.VertexCount);
            }
        }
        finally
        {
            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.DepthMask(true);
            GL.Enable(EnableCap.Blend);
            GL.BindVertexArray(0);
        }
    }

    private void InitializeFifoProjectionProgram()
    {
        const string vertex = """
            #version 330 core
            layout(location=0) in vec3 aPosition;
            layout(location=1) in vec3 aFifoStq;
            uniform mat4 uViewProjection;
            out vec3 vFifoStq;
            void main() {
                gl_Position = uViewProjection * vec4(aPosition, 1.0);
                vFifoStq = aFifoStq;
            }
            """;
        const string fragment = """
            #version 330 core
            in vec3 vFifoStq;
            uniform sampler2D uTexture;
            out vec4 FragColor;
            void main() {
                if (abs(vFifoStq.z) < 0.0000001) discard;
                vec2 uv = vFifoStq.xy / vFifoStq.z;
                FragColor = texture(uTexture, uv);
                if (FragColor.a <= 0.0039) discard;
            }
            """;
        int vs = Compile(ShaderType.VertexShader, vertex);
        int fs = Compile(ShaderType.FragmentShader, fragment);
        int program = GL.CreateProgram();
        GL.AttachShader(program, vs); GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
        string log = GL.GetProgramInfoLog(program);
        GL.DetachShader(program, vs); GL.DetachShader(program, fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        if (ok == 0) { GL.DeleteProgram(program); throw new InvalidOperationException("FIFO projection shader: " + log); }
        _fifoProjectionProgram = program;
        _fifoProjectionMatrixLocation = GL.GetUniformLocation(program, "uViewProjection");
        _fifoProjectionSamplerLocation = GL.GetUniformLocation(program, "uTexture");
    }

    // Pass 4i comparison: mode 10 uses GLSL's projective sampler, which
    // interpolates S/T/Q before the lookup. Mode 11 shows interpolated Q
    // without hiding either sign. The geometry and GX matrices are EXACTLY
    // those already uploaded by Pass 4h mode 9.
    private void DrawFifoProjectiveTest(Matrix4 viewProjection)
    {
        if (_fifoTexgenOrderBatches.Count == 0) return;
        if (_fifoProjectiveTestProgram == 0) InitializeFifoProjectiveTestProgram();
        GL.UseProgram(_fifoProjectiveTestProgram);
        GL.UniformMatrix4(_fifoProjectiveTestMatrixLocation, false, ref viewProjection);
        GL.Uniform1(_fifoProjectiveTestSamplerLocation, 0);
        GL.Uniform1(_fifoProjectiveTestModeLocation, TevInputPreviewMode == 11 ? 1 : 0);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(false);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(-1f, -1f);
        try
        {
            foreach (var batch in _fifoTexgenOrderBatches)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, batch.Texture);
                GL.BindVertexArray(batch.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, batch.VertexCount);
            }
        }
        finally
        {
            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.DepthMask(true);
            GL.Enable(EnableCap.Blend);
            GL.BindVertexArray(0);
            GL.UseProgram(_program);
        }
    }

    private void InitializeFifoProjectiveTestProgram()
    {
        const string vertex = """
            #version 330 core
            layout(location=0) in vec3 aPosition;
            layout(location=1) in vec3 aFifoStq;
            uniform mat4 uViewProjection;
            out vec3 vStq;
            void main() {
                gl_Position = uViewProjection * vec4(aPosition, 1.0);
                vStq = aFifoStq;
            }
            """;
        const string fragment = """
            #version 330 core
            in vec3 vStq;
            uniform sampler2D uTexture;
            uniform int uDiagnosticMode;
            out vec4 FragColor;
            void main() {
                float q = vStq.z;
                if (uDiagnosticMode == 1) {
                    // Bright yellow: close to the projective singularity;
                    // red: negative Q, green: positive Q. No Q-sign discard.
                    // The threshold is in captured GX coordinate units.
                    if (abs(q) < 100.0)
                        FragColor = vec4(1.0, 0.95, 0.05, 1.0);
                    else if (q < 0.0)
                        FragColor = vec4(0.93, 0.15, 0.20, 1.0);
                    else
                        FragColor = vec4(0.12, 0.80, 0.30, 1.0);
                    return;
                }
                // This intentionally differs from mode 9's explicit divide.
                // GLSL textureProj performs the homogeneous divide before
                // sampling, with implementation-defined texture derivatives.
                // At Q == 0 the projected lookup is undefined: mark only
                // an extremely narrow band instead of discarding a sign.
                if (abs(q) < 0.000001) {
                    FragColor = vec4(1.0, 0.0, 1.0, 1.0);
                    return;
                }
                FragColor = textureProj(uTexture, vStq);
                if (FragColor.a <= 0.0039) discard;
            }
            """;
        int vs = Compile(ShaderType.VertexShader, vertex);
        int fs = Compile(ShaderType.FragmentShader, fragment);
        int program = GL.CreateProgram();
        GL.AttachShader(program, vs); GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
        string log = GL.GetProgramInfoLog(program);
        GL.DetachShader(program, vs); GL.DetachShader(program, fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        if (ok == 0)
        {
            GL.DeleteProgram(program);
            throw new InvalidOperationException("Pass 4i projective sampling shader: " + log);
        }
        _fifoProjectiveTestProgram = program;
        _fifoProjectiveTestMatrixLocation = GL.GetUniformLocation(program, "uViewProjection");
        _fifoProjectiveTestSamplerLocation = GL.GetUniformLocation(program, "uTexture");
        _fifoProjectiveTestModeLocation = GL.GetUniformLocation(program, "uDiagnosticMode");
        WriteDiagnostic("Step 6d.5 Pass 4i projective test compiled: " +
            "F8=10 GLSL textureProj vs F8=9 explicit S/Q,T/Q; " +
            "F8=11 Q-sign red negative / green positive / yellow abs(Q)<100. " +
            "Both use the SAME Pass 4h vertices and captured GX matrices; " +
            "no Q-sign rejection, no TEV/baseline changes.");
    }

    private void DrawIndirectPreview(Matrix4 viewProjection)
    {
        if (_indirectPreviewBatches.Count == 0) return;
        if (_indirectProgram == 0) InitializeIndirectPreviewProgram();
        GL.UseProgram(_indirectProgram);
        GL.UniformMatrix4(_indirectMatrixLocation, false, ref viewProjection);
        GL.Uniform1(_indirectModeLocation, TevInputPreviewMode);
        GL.Uniform1(_indirectSamplerLocation, 0);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(false);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(-1f, -1f);
        try
        {
            foreach (var batch in _indirectPreviewBatches)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, batch.Texture);
                GL.Uniform3(_indirectRowSLocation, batch.RowS[0], batch.RowS[1], batch.RowS[2]);
                GL.Uniform3(_indirectRowTLocation, batch.RowT[0], batch.RowT[1], batch.RowT[2]);
                GL.Uniform1(_indirectMatrixScaleLocation, batch.MatrixScale);
                GL.Uniform1(_indirectBiasLocation, batch.BiasSelection);
                GL.BindVertexArray(batch.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, batch.VertexCount);
            }
        }
        finally
        {
            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.DepthMask(true);
            GL.Enable(EnableCap.Blend);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindVertexArray(0);
            GL.UseProgram(_program);
        }
    }

    private void InitializeIndirectPreviewProgram()
    {
        const string vertex = """
            #version 330 core
            layout(location=0) in vec3 aPosition;
            layout(location=1) in vec2 aUv;
            uniform mat4 uViewProjection;
            out vec2 vUv;
            void main() { gl_Position = uViewProjection * vec4(aPosition,1.0); vUv = aUv; }
            """;
        const string fragment = """
            #version 330 core
            in vec2 vUv;
            uniform sampler2D uIndirectTexture;
            uniform int uIndirectMode;
            uniform vec3 uRowS;
            uniform vec3 uRowT;
            uniform float uMatrixScale;
            uniform int uBiasSelection;
            out vec4 FragColor;
            void main() {
                vec3 source = texture(uIndirectTexture, vUv).rgb;
                if (uIndirectMode == 4) { FragColor = vec4(source, 1.0); return; }
                vec3 values = floor(source * 255.0 + 0.5);
                // GX 8-bit signed offset bias, independently selected per component.
                if ((uBiasSelection & 1) != 0) values.x -= 128.0;
                if ((uBiasSelection & 2) != 0) values.y -= 128.0;
                if ((uBiasSelection & 4) != 0) values.z -= 128.0;
                vec2 offset = vec2(dot(uRowS, values), dot(uRowT, values)) * uMatrixScale;
                // Diagnostic-only visualization, not a texture-coordinate addition.
                FragColor = vec4(clamp(vec2(0.5) + offset / 256.0, 0.0, 1.0), 0.5, 1.0);
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
        GL.DetachShader(program, vs); GL.DetachShader(program, fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        if (ok == 0) { GL.DeleteProgram(program); throw new InvalidOperationException("Indirect preview shader: " + log); }
        _indirectProgram = program;
        _indirectMatrixLocation = GL.GetUniformLocation(program, "uViewProjection");
        _indirectModeLocation = GL.GetUniformLocation(program, "uIndirectMode");
        _indirectSamplerLocation = GL.GetUniformLocation(program, "uIndirectTexture");
        _indirectRowSLocation = GL.GetUniformLocation(program, "uRowS");
        _indirectRowTLocation = GL.GetUniformLocation(program, "uRowT");
        _indirectMatrixScaleLocation = GL.GetUniformLocation(program, "uMatrixScale");
        _indirectBiasLocation = GL.GetUniformLocation(program, "uBiasSelection");
    }

    // Phase 2: the three advanced paths are unified as material draw passes.
    // For every mode, the exact same source GPU batches, shader mode and order
    // used in Phase 1 are retained. An unknown mode still selects regular TEV.
    private IReadOnlyList<TevInputBatch> GetTevBatches(int mode) =>
        FindMaterialDrawPass(mode)?.Batches ?? Array.Empty<TevInputBatch>();

    // Isolated A/B modes 16/17: diagnose whether regular TEV output alpha
    // or opaque replacement is hiding the baseline window-frame details.
    private enum RegularTevBlend { Opaque, SourceAlpha, HalfOpacity }

    private void DrawConsolidatedTevPreview(Matrix4 viewProjection,
        bool includeRegular = true, bool regularPolygonOffset = true,
        RegularTevBlend regularBlend = RegularTevBlend.Opaque)
    {
        // Isolated window-frame A/B diagnostics:
        //   mode 13: unchanged Phase 3 consolidated renderer
        //   mode 14: baseline + indirect/projected; skip regular TEV
        //   mode 15: all three passes, but do not pull regular TEV forward in depth
        // Every other material pass and diagnostic mode retains its old state.
        foreach (var pass in _materialDrawPipeline)
        {
            if (pass.ShaderMode == RegularTevMode && !includeRegular)
                continue;
            bool useOffset = pass.ShaderMode != RegularTevMode || regularPolygonOffset;
            DrawTevInputPreview(viewProjection, pass.Batches, pass.ShaderMode,
                polygonOffset: useOffset,
                regularBlend: pass.ShaderMode == RegularTevMode ? regularBlend : RegularTevBlend.Opaque,
                suppressedMaterial: pass.ShaderMode == RegularTevMode ? _suppressedRegularTevMaterial : -1);
        }
    }

    private void LogConsolidatedTevCoverage(BrresModelReader.DecodeResult model)
    {
        var regular = GetTevBatches(RegularTevMode).Select(b => b.MaterialIndex).ToHashSet();
        var indirect = GetTevBatches(IndirectTevMode).Select(b => b.MaterialIndex).ToHashSet();
        var projected = GetTevBatches(ProjectedTevMode).Select(b => b.MaterialIndex).ToHashSet();
        int duplicateMaterials = regular.Intersect(indirect).Count() +
            regular.Intersect(projected).Count() + indirect.Intersect(projected).Count();
        int regularCount = GetTevBatches(RegularTevMode).Count;
        int indirectCount = GetTevBatches(IndirectTevMode).Count;
        int projectedCount = GetTevBatches(ProjectedTevMode).Count;
        int advanced = regularCount + indirectCount + projectedCount;
        // This estimate assumes one GPU batch per polygon surface; it retains
        // Phase 1's existing behavior and is not a proof of GX-exact coverage.
        int baselineOnly = Math.Max(0, model.PolygonSurfaces.Count - advanced);
        WriteDiagnostic($"Step 6d.5 Pass 4k consolidated TEV: " +
            $"regular={regularCount}, indirect={indirectCount}, " +
            $"projected={projectedCount}, totalAdvanced={advanced}, " +
            $"baselineOnlyEstimate={baselineOnly}, sharedMaterialIds={duplicateMaterials}. " +
            "Step 6e Phase 3: unified GPU batch ownership; default mode 13, " +
            "F8 cycles 0-13, F9 toggles baseline/consolidated. Shader equations unchanged. " +
            "Projected TEV uses frozen FIFO frame0, indirect regular UV can be approximate.");
    }

    private void DrawTevInputPreview(Matrix4 viewProjection,
        IReadOnlyList<TevInputBatch>? selectedBatches = null, int? selectedMode = null,
        bool polygonOffset = true,
        RegularTevBlend regularBlend = RegularTevBlend.Opaque,
        int suppressedMaterial = -1)
    {
        int mode = selectedMode ?? TevInputPreviewMode;
        IReadOnlyList<TevInputBatch> drawBatches = selectedBatches ?? GetTevBatches(mode);
        if (drawBatches.Count == 0) return;
        if (_tevInputProgram == 0) InitializeTevInputProgram();
        GL.UseProgram(_tevInputProgram);
        GL.UniformMatrix4(_tevInputMatrix, false, ref viewProjection);
        GL.Uniform1(_tevInputSampler0, 0);
        GL.Uniform1(_tevInputSampler1, 1);
        GL.Uniform1(_tevIndirectSampler, 2);
        GL.Uniform1(_tevInputMode, mode);
        // Default remains opaque, exactly as in Phase 3. Modes 16/17 opt in
        // ONLY for the regular pass; the indirect/projected passes are unchanged.
        if (regularBlend == RegularTevBlend.Opaque)
            GL.Disable(EnableCap.Blend);
        else
        {
            GL.Enable(EnableCap.Blend);
            if (regularBlend == RegularTevBlend.HalfOpacity)
            {
                GL.BlendColor(0f, 0f, 0f, 0.5f);
                GL.BlendFunc(BlendingFactor.ConstantAlpha, BlendingFactor.OneMinusConstantAlpha);
            }
            else
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }
        GL.DepthMask(false); // preview never modifies baseline depth buffer
        if (polygonOffset)
        {
            GL.Enable(EnableCap.PolygonOffsetFill);
            GL.PolygonOffset(-1f, -1f);
        }
        else
        {
            GL.Disable(EnableCap.PolygonOffsetFill);
        }
        try
        {
            foreach (var batch in drawBatches)
            {
                if (mode == RegularTevMode && batch.MaterialIndex == suppressedMaterial)
                    continue;
                GL.Uniform1(_tevIndirectEnabled, mode == 6 && batch.IndirectTexture != 0 ? 1 : 0);
                GL.Uniform1(_tevProjectedStage1, mode == 12 && batch.ProjectedStage1 ? 1 : 0);
                if (mode == 6)
                {
                    GL.ActiveTexture(TextureUnit.Texture2);
                    GL.BindTexture(TextureTarget.Texture2D, batch.IndirectTexture);
                    GL.Uniform3(_tevIndirectRowS, batch.IndirectRowS![0], batch.IndirectRowS[1], batch.IndirectRowS[2]);
                    GL.Uniform3(_tevIndirectRowT, batch.IndirectRowT![0], batch.IndirectRowT[1], batch.IndirectRowT[2]);
                    GL.Uniform1(_tevIndirectScale, batch.IndirectScale);
                    GL.Uniform1(_tevIndirectBias, batch.IndirectBias);
                }
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, batch.Texture0);
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, batch.Texture1);
                if ((mode is 3 or 6 or 12) && !batch.EquationSupported) continue;
                if (mode is 3 or 6 or 12)
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
            // Restore blending for all baseline and subsequent advanced passes.
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.ActiveTexture(TextureUnit.Texture2);
            GL.BindTexture(TextureTarget.Texture2D, 0);
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
            layout(location=4) in vec2 aIndirectUv;
            layout(location=5) in vec3 aProjectedStq;
            uniform mat4 uViewProjection;
            out vec2 vStage0Uv;
            out vec2 vStage1Uv;
            out vec4 vRasterColor;
            out vec2 vIndirectUv;
            out vec3 vProjectedStq;
            void main() {
                vProjectedStq = aProjectedStq;
                vIndirectUv = aIndirectUv;
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
            in vec2 vIndirectUv;
            in vec3 vProjectedStq;
            uniform int uProjectedStage1;
            uniform sampler2D uIndirectSource;
            uniform int uIndirectEnabled;
            uniform vec3 uIndirectRowS;
            uniform vec3 uIndirectRowT;
            uniform float uIndirectScale;
            uniform int uIndirectBias;
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
                vec2 ofs = vec2(0.0);
                if (uIndirectEnabled != 0) {
                    vec3 values = floor(texture(uIndirectSource, vIndirectUv).rgb * 255.0 + 0.5);
                    if ((uIndirectBias & 1) != 0) values.x -= 128.0;
                    if ((uIndirectBias & 2) != 0) values.y -= 128.0;
                    if ((uIndirectBias & 4) != 0) values.z -= 128.0;
                    ofs = vec2(dot(uIndirectRowS, values), dot(uIndirectRowT, values)) * uIndirectScale;
                }
                // Approximate GX fixed-point coordinate accumulation in normalized
                // texture space; do not mistake this for cycle-exact GX behavior.
                vec2 uv0 = vStage0Uv + ofs / vec2(textureSize(uStage0, 0));
                vec2 uv1 = vStage1Uv + ofs / vec2(textureSize(uStage1, 0));
                vec4 tex0 = texture(uStage0, uv0);
                vec4 tex1 = uProjectedStage1 != 0
                    ? textureProj(uStage1, vProjectedStq) : texture(uStage1, uv1);
                if (uPreviewMode != 3 && uPreviewMode != 6 && uPreviewMode != 12) {
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
        _tevProjectedStage1 = GL.GetUniformLocation(program, "uProjectedStage1");
        _tevIndirectSampler = GL.GetUniformLocation(program, "uIndirectSource");
        _tevIndirectEnabled = GL.GetUniformLocation(program, "uIndirectEnabled");
        _tevIndirectRowS = GL.GetUniformLocation(program, "uIndirectRowS");
        _tevIndirectRowT = GL.GetUniformLocation(program, "uIndirectRowT");
        _tevIndirectScale = GL.GetUniformLocation(program, "uIndirectScale");
        _tevIndirectBias = GL.GetUniformLocation(program, "uIndirectBias");
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
        // Drop the model-scoped references before their GL batches are deleted.
        _materialDrawPipeline = [];
        foreach (var batch in _batches)
        {
            GL.DeleteBuffer(batch.Vbo);
            GL.DeleteVertexArray(batch.Vao);
        }
        _batches.Clear();
        // Exactly one release per advanced batch, regardless of diagnostic mode.
        foreach (var item in _gpuTevMaterials)
        {
            GL.DeleteBuffer(item.Batch.Vbo);
            GL.DeleteVertexArray(item.Batch.Vao);
        }
        _gpuTevMaterials.Clear();
        foreach (var batch in _indirectPreviewBatches)
        {
            GL.DeleteBuffer(batch.Vbo);
            GL.DeleteVertexArray(batch.Vao);
        }
        _indirectPreviewBatches.Clear();
        foreach (var batch in _projectedPreviewBatches)
        {
            GL.DeleteBuffer(batch.Vbo);
            GL.DeleteVertexArray(batch.Vao);
        }
        _projectedPreviewBatches.Clear();
        foreach (var batch in _fifoProjectionBatches)
        {
            GL.DeleteBuffer(batch.Vbo);
            GL.DeleteVertexArray(batch.Vao);
        }
        _fifoProjectionBatches.Clear();
        foreach (var batch in _fifoTexgenOrderBatches)
        {
            GL.DeleteBuffer(batch.Vbo);
            GL.DeleteVertexArray(batch.Vao);
        }
        _fifoTexgenOrderBatches.Clear();
        foreach (int texture in _textures.Values) GL.DeleteTexture(texture);
        _textures.Clear();
        _uploaded = null;
    }
}
