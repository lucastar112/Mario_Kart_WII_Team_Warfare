using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Tools = GameManager.Tools.TrackFormats;
// Explicit WPF/Win32 aliases: DebugManager also enables Windows Forms.
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;


namespace DebugManager.Pages;

/// <summary>
/// Temporary WPF-only smoke-test harness. Every parser called here is from
/// GameManager.Tools; the existing DebugManager parsers and renderers are untouched.
/// </summary>
public partial class LibraryValidationPage : Page
{
    private readonly MainWindow _mainWindow;
    private bool _running;

    public LibraryValidationPage(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "szs"));
        if (Directory.Exists(directory))
        {
            string candidate = Path.Combine(directory, "000.szs");
            if (!File.Exists(candidate))
                candidate = Directory.EnumerateFiles(directory, "*.szs").FirstOrDefault() ?? string.Empty;
            ArchivePathBox.Text = candidate;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _mainWindow.ShowMainMenu();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a Mario Kart Wii course archive",
            Filter = "SZS archives (*.szs)|*.szs|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (File.Exists(ArchivePathBox.Text))
            dialog.FileName = ArchivePathBox.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ArchivePathBox.Text = dialog.FileName;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        string path = ArchivePathBox.Text.Trim().Trim('"');
        if (!File.Exists(path))
        {
            StatusText.Text = "Select an existing .szs file first.";
            return;
        }
        _running = true;
        RunButton.IsEnabled = false;
        StatusText.Text = "Reading KMP and decoding three BRRES profiles...";
        ProfileGrid.ItemsSource = null;
        KmpGrid.ItemsSource = null;
        TevGrid.ItemsSource = null;
        GeometryPreview.Source = null;
        TexturePreview.Source = null;
        TextureCaption.Text = string.Empty;
        ReportBox.Text = string.Empty;
        try
        {
            // All decoding executes off the UI thread. We retain only a compact
            // snapshot needed to display the results rather than three large models.
            ValidationResult result = await Task.Run(() => Validate(path));
            ProfileGrid.ItemsSource = result.Profiles;
            KmpGrid.ItemsSource = result.KmpSections;
            TevGrid.ItemsSource = result.TevStages;
            KmpSummaryText.Text = result.KmpSummary;
            ReportBox.Text = result.Report;
            TextureCaption.Text = result.TextureName;
            if (result.Mesh is not null)
                GeometryPreview.Source = DrawMesh(result.Mesh, result.Checkpoints);
            if (result.Texture is not null)
            {
                try { TexturePreview.Source = DecodeTextureImage(result.Texture); }
                catch (Exception ex) { TextureCaption.Text += " — preview error: " + ex.Message; }
            }
            StatusText.Text = result.Headline;
            Console.WriteLine("[DebugManager] GameManager.Tools validation: " + result.Headline);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Validation failed: " + ex.Message;
            ReportBox.Text = ex.ToString();
            Console.WriteLine("[DebugManager] GameManager.Tools validation failed: " + ex);
        }
        finally
        {
            _running = false;
            RunButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ReportBox.Text)) Clipboard.SetText(ReportBox.Text);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ReportBox.Text)) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save GameManager.Tools validation report",
            Filter = "Text report (*.txt)|*.txt",
            FileName = "GameManager-Tools-Validation.txt"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            File.WriteAllText(dialog.FileName, ReportBox.Text, Encoding.UTF8);
    }

    private static ValidationResult Validate(string path)
    {
        var watch = Stopwatch.StartNew();
        var result = new ValidationResult();
        var log = new StringBuilder();
        log.AppendLine("GameManager.Tools — portable KMP / BRRES validation");
        log.AppendLine("Archive: " + Path.GetFullPath(path));
        log.AppendLine("All parsing is performed by GameManager.Tools, not DebugManager.Rendering.");
        log.AppendLine();

        byte[] bytes = File.ReadAllBytes(path);
        Tools.SzsArchiveReader archive = Tools.SzsArchiveReader.Open(bytes);
        log.AppendLine($"SZS/U8: OK; compressed input={bytes.Length:N0} bytes; files={archive.FileNames.Count}");
        log.AppendLine("Files (first 12): " + string.Join(", ", archive.FileNames.Take(12)));
        log.AppendLine();

        // Test KMP independently so a checkpoint failure does not prevent BRRES tests.
        try
        {
            Tools.KmpDocument kmp = Tools.KmpFileReader.Read(archive.ReadFile("course.kmp"));
            result.KmpSections.AddRange(kmp.Sections.Select(s => new KmpRow(
                s.Magic, s.EntryCount, s.FixedEntrySize?.ToString() ?? "variable", s.RawPayload.Length, s.Entries.Count)));
            log.AppendLine($"KMP: OK; sections={kmp.Sections.Count}; version=0x{kmp.Version:X}; declaredBytes={kmp.FileLength:N0}");
            foreach (Tools.KmpSection section in kmp.Sections)
                log.AppendLine($"  {section.Magic,-5} count={section.EntryCount,5} entriesDecoded={section.Entries.Count,5} recordSize={(section.FixedEntrySize?.ToString() ?? "variable")}");
            result.KmpSummary = $"RKMD 0x{kmp.Version:X} · {kmp.Sections.Count} sections · " +
                $"{kmp.FindSection("GOBJ")?.EntryCount ?? 0} objects · {kmp.FindSection("ENPT")?.EntryCount ?? 0} enemy points";
            result.KmpOk = true;
            try
            {
                Tools.KmpCheckpointGraph graph = Tools.KmpCheckpointDecoder.Decode(kmp);
                result.Checkpoints = graph.Checkpoints.ToArray();
                result.KmpSummary += $" · {graph.Checkpoints.Count} checkpoints · {graph.Groups.Count} checkpoint groups";
                log.AppendLine($"CKPT / CKPH: OK; checkpoints={graph.Checkpoints.Count}; groups={graph.Groups.Count}");
            }
            catch (Exception ex)
            {
                result.KmpSummary += " · CKPT decoder failed";
                log.AppendLine("CKPT / CKPH: FAIL: " + ex);
                result.KmpOk = false;
            }
        }
        catch (Exception ex)
        {
            result.KmpSummary = "KMP: FAILED — " + ex.Message;
            log.AppendLine("KMP: FAILED: " + ex);
        }
        log.AppendLine();

        if (!archive.ContainsFile("course_model.brres"))
        {
            log.AppendLine("BRRES: course_model.brres is not present in this archive. All three profiles skipped.");
            foreach (Tools.BrresImportMode mode in Enum.GetValues<Tools.BrresImportMode>())
                result.Profiles.Add(new ProfileRow(mode.ToString(), "Missing BRRES", 0, 0, 0, 0, 0, 0, 0, 0, 0));
        }
        else
        {
            byte[] modelBytes = archive.ReadFile("course_model.brres");
            log.AppendLine($"BRRES: course_model.brres found ({modelBytes.Length:N0} bytes)");
            foreach (Tools.BrresImportMode mode in Enum.GetValues<Tools.BrresImportMode>())
            {
                var elapsed = Stopwatch.StartNew();
                try
                {
                    Tools.BrresModelReader.DecodeResult model = Tools.BrresModelReader.Decode(modelBytes, mode);
                    result.Models[mode] = new ModelSummary(model.Geometry.Positions.Count,
                        model.Geometry.TriangleCount, model.PolygonCount, model.DecodedPolygonCount,
                        model.UvDecodedPolygons, model.Uv1DecodedPolygons, model.TexturePixels.Count,
                        model.TevShaders.Count);
                    string state = "PASS";
                    if (model.Geometry.TriangleCount == 0 || model.DecodedPolygonCount == 0)
                        state = "FAIL (no geometry)";
                    if (mode == Tools.BrresImportMode.GeometryOnly &&
                        (model.TexturePixels.Count != 0 || model.TevShaders.Count != 0 || model.UvPolygons.Count != 0))
                        state = "FAIL (unexpected resources)";
                    if (mode == Tools.BrresImportMode.BaselineTextured &&
                        (model.TevShaders.Count != 0 || model.TevIndirect.Count != 0 || model.TexCoordGenerators.Count != 0))
                        state = "FAIL (unexpected TEV)";
                    if (mode == Tools.BrresImportMode.TevMetadata && model.Materials.MaterialCount > 0 && model.TevShaders.Count == 0)
                        state = "WARN (no GX shaders)";
                    result.Profiles.Add(new ProfileRow(mode.ToString(), state,
                        model.DecodedPolygonCount, model.TriangleCount, model.Geometry.Positions.Count,
                        model.UvDecodedPolygons, model.Uv1DecodedPolygons,
                        model.TexturePixels.Count, model.TevShaders.Count, model.TevIndirect.Count,
                        model.Warnings.Count));
                    log.AppendLine($"{mode}: {state}; {elapsed.ElapsedMilliseconds} ms; polygons={model.DecodedPolygonCount}/{model.PolygonCount}, " +
                        $"triangles={model.TriangleCount}, uniquePositions={model.Geometry.Positions.Count}, " +
                        $"UV0={model.UvDecodedPolygons}, UV1={model.Uv1DecodedPolygons}, RGBA textures={model.TexturePixels.Count}, " +
                        $"materials={model.Materials.MaterialCount}, TEV shaders={model.TevShaders.Count}, " +
                        $"texgens={model.TexCoordGenerators.Count}, indirect={model.TevIndirect.Count}, warnings={model.Warnings.Count}");
                    foreach (string warning in model.Warnings.Take(6)) log.AppendLine("  warning: " + warning);

                    if (mode == Tools.BrresImportMode.GeometryOnly)
                        result.Mesh = model.Geometry;
                    if (mode == Tools.BrresImportMode.BaselineTextured && model.TexturePixels.Count > 0)
                    {
                        // Keep only one image from the baseline loader, not all TEX0 data.
                        var sample = model.TexturePixels.OrderBy(t => t.Key, StringComparer.Ordinal).First();
                        result.Texture = sample.Value;
                        result.TextureName = $"{sample.Key} — {sample.Value.Width} × {sample.Value.Height} RGBA";
                    }
                    if (mode == Tools.BrresImportMode.TevMetadata)
                    {
                        foreach (var pair in model.TevShaders.OrderBy(p => p.Key))
                        {
                            foreach (Tools.BrresTevDiagnostics.Stage stage in pair.Value.Stages)
                            {
                                result.TevStages.Add(new TevStageRow(pair.Key, stage.Index, stage.TextureEnabled,
                                    stage.TextureMap, stage.TexCoord, stage.ColorDestination, stage.AlphaDestination,
                                    stage.TextureSwap, stage.RasterSwap, stage.IndirectCommand != 0));
                            }
                        }
                        log.AppendLine($"  GX: stages={result.TevStages.Count}, registered layers={model.TevLayers.Count}, " +
                            $"KONST={model.TevKonst.Count}, initialRegisters={model.TevInitialRegisters.Count}, " +
                            $"rasterChannels={model.TevRasterChannels.Count}, projectedGenerators={model.TexCoordGenerators.Count}");
                    }
                }
                catch (Exception ex)
                {
                    result.Profiles.Add(new ProfileRow(mode.ToString(), "FAIL: " + ex.Message,
                        0, 0, 0, 0, 0, 0, 0, 0, 0));
                    log.AppendLine($"{mode}: FAILED after {elapsed.ElapsedMilliseconds} ms: " + ex);
                }
            }
            // Verify shared geometry and resource continuity between all three profiles.
            if (result.Models.TryGetValue(Tools.BrresImportMode.GeometryOnly, out var geo) &&
                result.Models.TryGetValue(Tools.BrresImportMode.BaselineTextured, out var baseline) &&
                result.Models.TryGetValue(Tools.BrresImportMode.TevMetadata, out var advanced))
            {
                bool geometryEqual = geo.Triangles == baseline.Triangles &&
                    geo.Triangles == advanced.Triangles && geo.Vertices == baseline.Vertices &&
                    geo.Vertices == advanced.Vertices && geo.Polygons == baseline.Polygons &&
                    geo.Polygons == advanced.Polygons;
                bool textureEqual = baseline.Textures == advanced.Textures &&
                    baseline.Uv0 == advanced.Uv0 && baseline.Uv1 == advanced.Uv1;
                result.ParityOk = geometryEqual && textureEqual;
                log.AppendLine($"Cross-profile parity: geometry={(geometryEqual ? "PASS" : "FAIL")}; " +
                    $"baseline vs TEV texture/UV counts={(textureEqual ? "PASS" : "FAIL")}");
            }
        }

        // Separate end-to-end smoke test for the public convenience API.
        // It is intentionally explicit so failures do not get hidden by direct parsers.
        try
        {
            Tools.CourseData course = Tools.CourseDataLoader.Open(bytes, Tools.BrresImportMode.GeometryOnly);
            bool valid = course.Kmp.Sections.Count > 0 && course.Model?.Geometry.TriangleCount > 0;
            result.CourseLoaderOk = valid;
            log.AppendLine("CourseDataLoader.Open(GeometryOnly): " + (valid ? "PASS" : "WARN — no KMP sections or course model"));
        }
        catch (Exception ex)
        {
            result.CourseLoaderOk = false;
            log.AppendLine("CourseDataLoader.Open(GeometryOnly): FAILED: " + ex);
        }
        watch.Stop();
        bool profilesOk = result.Profiles.Count == 3 && result.Profiles.All(p => p.State == "PASS");
        result.Headline = $"{Path.GetFileName(path)} · KMP {(result.KmpOk ? "PASS" : "FAIL")} · " +
            $"BRRES {result.Profiles.Count(p => p.State == "PASS")}/3 PASS · " +
            $"parity {(result.ParityOk ? "PASS" : "CHECK")} · " +
            $"CourseDataLoader {(result.CourseLoaderOk ? "PASS" : "CHECK")} · {watch.ElapsedMilliseconds:N0} ms";
        log.AppendLine();
        log.AppendLine("FINAL: " + result.Headline);
        log.AppendLine("Notes: TEV metadata is not cycle-exact rendering. This page tests decoder output, not OpenGL material appearance.");
        if (!result.KmpOk || !profilesOk || !result.ParityOk || !result.CourseLoaderOk)
            log.AppendLine("One or more checks need inspection; see errors/warnings above.");
        result.Report = log.ToString();
        return result;
    }

    private static BitmapSource DecodeTextureImage(Tools.BrresMaterialDiagnostics.TexturePixels source)
    {
        int pixels = checked(source.Width * source.Height);
        if (pixels <= 0 || source.Rgba.Length < checked(pixels * 4))
            throw new InvalidDataException("Decoded TEX0 pixel buffer is incomplete.");
        byte[] bgra = new byte[pixels * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = source.Rgba[i + 2];
            bgra[i + 1] = source.Rgba[i + 1];
            bgra[i + 2] = source.Rgba[i];
            bgra[i + 3] = source.Rgba[i + 3];
        }
        BitmapSource bitmap = BitmapSource.Create(source.Width, source.Height, 96, 96,
            PixelFormats.Bgra32, null, bgra, checked(source.Width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource DrawMesh(Tools.TrackMesh mesh, IReadOnlyList<Tools.KmpCheckpoint> checkpoints)
    {
        const int width = 960, height = 520;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 24, 33)), null, new Rect(0, 0, width, height));
            if (mesh.Positions.Count > 0 && mesh.TriangleIndices.Count >= 3)
            {
                double minX = mesh.Positions.Min(p => p.X), maxX = mesh.Positions.Max(p => p.X);
                double minZ = mesh.Positions.Min(p => p.Z), maxZ = mesh.Positions.Max(p => p.Z);
                double rangeX = Math.Max(maxX - minX, 1), rangeZ = Math.Max(maxZ - minZ, 1);
                double scale = Math.Min((width - 42.0) / rangeX, (height - 42.0) / rangeZ);
                double x0 = (width - rangeX * scale) / 2, z0 = (height - rangeZ * scale) / 2;
                Point Project(double x, double z) => new(x0 + (x - minX) * scale, height - z0 - (z - minZ) * scale);
                var stroke = new Pen(new SolidColorBrush(Color.FromArgb(155, 108, 195, 219)), 0.7);
                var crossing = new Pen(Brushes.YellowGreen, 2.5);
                stroke.Freeze();
                crossing.Freeze();
                int triangles = mesh.TriangleIndices.Count / 3;
                int step = Math.Max(1, (int)Math.Ceiling(triangles / 4500.0));
                for (int tri = 0; tri < triangles; tri += step)
                {
                    int offset = tri * 3;
                    int i0 = mesh.TriangleIndices[offset], i1 = mesh.TriangleIndices[offset + 1], i2 = mesh.TriangleIndices[offset + 2];
                    if ((uint)i0 >= (uint)mesh.Positions.Count || (uint)i1 >= (uint)mesh.Positions.Count || (uint)i2 >= (uint)mesh.Positions.Count) continue;
                    var a = mesh.Positions[i0]; var b = mesh.Positions[i1]; var c = mesh.Positions[i2];
                    Point pa = Project(a.X, a.Z), pb = Project(b.X, b.Z), pc = Project(c.X, c.Z);
                    dc.DrawLine(stroke, pa, pb);
                    dc.DrawLine(stroke, pb, pc);
                    dc.DrawLine(stroke, pc, pa);
                }
                foreach (Tools.KmpCheckpoint cp in checkpoints)
                {
                    Point left = Project(cp.LeftX, cp.LeftZ);
                    Point right = Project(cp.RightX, cp.RightZ);
                    dc.DrawLine(crossing, left, right);
                }
                var label = new FormattedText($"{triangles:N0} triangles (wireframe sampled every {step}) · {checkpoints.Count} checkpoints",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 16, Brushes.White, 1);
                dc.DrawText(label, new Point(14, 12));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class ValidationResult
    {
        public List<ProfileRow> Profiles { get; } = [];
        public List<KmpRow> KmpSections { get; } = [];
        public List<TevStageRow> TevStages { get; } = [];
        public Dictionary<Tools.BrresImportMode, ModelSummary> Models { get; } = [];
        public IReadOnlyList<Tools.KmpCheckpoint> Checkpoints { get; set; } = [];
        public Tools.TrackMesh? Mesh { get; set; }
        public Tools.BrresMaterialDiagnostics.TexturePixels? Texture { get; set; }
        public string TextureName { get; set; } = "No decoded TEX0 image";
        public string KmpSummary { get; set; } = "Not tested";
        public string Report { get; set; } = "";
        public string Headline { get; set; } = "";
        public bool KmpOk { get; set; }
        public bool CourseLoaderOk { get; set; }
        public bool ParityOk { get; set; }
    }
    private sealed record ModelSummary(int Vertices, int Triangles, int Polygons, int DecodedPolygons,
        int Uv0, int Uv1, int Textures, int TevShaders);
}

// Public types allow WPF's DataGrid reflection/binding to display row properties.
public sealed record ProfileRow(string Profile, string State, int Polygons, int Triangles,
    int Vertices, int Uv0, int Uv1, int RgbaTextures, int TevShaders, int Indirect, int Warnings);
public sealed record KmpRow(string Section, int DeclaredEntries, string EntrySize, int PayloadBytes, int ParsedEntries);
public sealed record TevStageRow(int Material, int Stage, bool TextureEnabled,
    int TextureMap, int TexCoord, int ColorDestination, int AlphaDestination,
    int TextureSwap, int RasterSwap, bool Indirect);
