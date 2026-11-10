using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Input;
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
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;


namespace DebugManager.Pages;

/// <summary>
/// Temporary WPF-only smoke-test harness. Every parser called here is from
/// GameManager.Tools; the existing DebugManager parsers and renderers are untouched.
/// </summary>
public partial class LibraryValidationPage : Page
{
    private readonly MainWindow _mainWindow;
    private bool _running;
    private Point _kclDragPoint;
    private bool _kclDragging;
    private double _kclYaw = 0.65, _kclPitch = 0.85, _kclDistance = 100;
    private Point3D _kclCenter;
    private double _kclRadius = 1;
    private IReadOnlyList<Tools.KclTriangle>? _kclLoadedTriangles;
    private bool _kclColorById = true;
    private readonly HashSet<int> _kclVisibleTypes = new();
    private bool _kclUpdatingFilters;



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
        StatusText.Text = "Reading KMP, KCL and three BRRES profiles...";
        ProfileGrid.ItemsSource = null;
        KmpGrid.ItemsSource = null;
        KclGrid.ItemsSource = null;
        KclModelVisual.Content = null;
        _kclLoadedTriangles = null;
        KclViewportHint.Text = "Loading collision geometry...";
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
            KclSummaryText.Text = result.KclSummary;
            KclGrid.ItemsSource = result.KclFlags;
            if (result.KclTriangles is { Count: > 0 })
            {
                PopulateKclTypeFilters(result.KclTriangles);
                ShowKclTriangles(result.KclTriangles);
            }
            else
                KclViewportHint.Text = "No decoded KCL triangles available";
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
        log.AppendLine("GameManager.Tools — portable KMP / KCL / BRRES validation");
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

        // Phase 1 KCL: parse raw vectors, 1-based prisms, collision flags and octree.
        // Geometry decoding verifies the data but is NOT displayed in this phase.
        if (!archive.ContainsFile("course.kcl"))
        {
            result.KclSummary = "course.kcl not present — KCL validation skipped";
            log.AppendLine("KCL: course.kcl not present (SKIPPED)");
        }
        else
        {
            try
            {
                byte[] kclBytes = archive.ReadFile("course.kcl");
                Tools.KclDocument kcl = Tools.KclFileReader.Read(kclBytes);
                Tools.KclTriangleDecodeResult decoded = Tools.KclTriangleDecoder.Decode(kcl);
                result.KclTriangles = decoded.Triangles;
                result.KclPrisms = kcl.PrismCount;
                result.KclOk = kcl.PrismCount > 0 && decoded.Triangles.Count > 0;
                result.KclSummary = $"{kcl.PrismCount:N0} prisms · {decoded.Triangles.Count:N0} decoded triangles · " +
                    $"{kcl.CollisionFlagCounts.Count} full flags · {kcl.CollisionTypeCounts.Count} base types";
                log.AppendLine($"KCL: {(result.KclOk ? "PASS" : "CHECK")}; bytes={kclBytes.Length:N0}; " +
                    $"positions={kcl.Positions.Count:N0}; normals={kcl.Normals.Count:N0}; " +
                    $"prisms={kcl.PrismCount:N0}; reconstructed={decoded.Triangles.Count:N0}; " +
                    $"degenerate={decoded.DegeneratePrismIds.Count}; " +
                    $"octreeRoots={kcl.Octree.RootEntryCount}; branchTables={kcl.Octree.BranchTables}; " +
                    $"leafLists={kcl.Octree.LeafLists}; referencedPrisms={kcl.Octree.ReferencedPrismIds.Count}; " +
                    $"invalidLeafReferences={kcl.Octree.InvalidReferencedPrismIds.Count}; warnings={kcl.Warnings.Count}");
                log.AppendLine($"  header: thickness={kcl.Header.PrismThickness}, min={kcl.Header.AreaMinimum}, " +
                    $"sphereRadius={kcl.Header.SphereRadius?.ToString() ?? "absent"}, " +
                    $"octreeShift={kcl.Header.BlockWidthShift}");
                foreach (var group in kcl.CollisionFlagCounts.OrderByDescending(pair => pair.Value))
                {
                    var flag = new Tools.KclCollisionFlag(group.Key);
                    result.KclFlags.Add(new KclFlagRow($"0x{flag.Raw:X4}", flag.Type, flag.Variant,
                        flag.BlightIndex, flag.WheelDepth, flag.Effects, group.Value));
                }
                foreach (var row in result.KclFlags.Take(20))
                    log.AppendLine($"  collision {row.Flag}: type=0x{row.Type:X2} variant={row.Variant} " +
                        $"blight={row.Blight} wheel={row.WheelDepth} effects={row.Effects} prisms={row.Prisms}");
                if (result.KclFlags.Count > 20)
                    log.AppendLine($"  ... {result.KclFlags.Count - 20} additional full flag values in the KCL tab");
                foreach (string warning in kcl.Warnings.Take(10)) log.AppendLine("  warning: " + warning);
            }
            catch (Exception ex)
            {
                result.KclSummary = "KCL parser failed: " + ex.Message;
                log.AppendLine("KCL: FAILED: " + ex);
            }
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
            bool valid = course.Kmp.Sections.Count > 0 && course.Model?.Geometry.TriangleCount > 0 &&
                (!archive.ContainsFile("course.kcl") || course.Kcl?.PrismCount == result.KclPrisms);
            result.CourseLoaderOk = valid;
            log.AppendLine("CourseDataLoader.Open(GeometryOnly): " + (valid ? "PASS" : "WARN — KMP/BRRES/KCL parity mismatch"));
        }
        catch (Exception ex)
        {
            result.CourseLoaderOk = false;
            log.AppendLine("CourseDataLoader.Open(GeometryOnly): FAILED: " + ex);
        }
        watch.Stop();
        bool profilesOk = result.Profiles.Count == 3 && result.Profiles.All(p => p.State == "PASS");
        result.Headline = $"{Path.GetFileName(path)} · KMP {(result.KmpOk ? "PASS" : "FAIL")} · " +
            $"KCL {(archive.ContainsFile("course.kcl") ? (result.KclOk ? "PASS" : "FAIL") : "SKIP")} · " +
            $"BRRES {result.Profiles.Count(p => p.State == "PASS")}/3 PASS · " +
            $"parity {(result.ParityOk ? "PASS" : "CHECK")} · " +
            $"CourseDataLoader {(result.CourseLoaderOk ? "PASS" : "CHECK")} · {watch.ElapsedMilliseconds:N0} ms";
        log.AppendLine();
        log.AppendLine("FINAL: " + result.Headline);
        log.AppendLine("Notes: TEV metadata is not cycle-exact rendering. This page tests decoder output, not OpenGL material appearance.");
        if (!result.KmpOk || (archive.ContainsFile("course.kcl") && !result.KclOk) || !profilesOk || !result.ParityOk || !result.CourseLoaderOk)
            log.AppendLine("One or more checks need inspection; see errors/warnings above.");
        result.Report = log.ToString();
        return result;
    }

    // Phase 2: geometry-only WPF visualization. The reusable KCL decoder remains UI-independent.
    // Build on the UI thread after the background parse has completed.
    private void KclColorMode_Changed(object sender, RoutedEventArgs e)
    {
        _kclColorById = KclColorModeBox?.IsChecked == true;
        if (_kclLoadedTriangles is { Count: > 0 } triangles)
            ShowKclTriangles(triangles, resetCamera: false);
    }

    // Group presets operate on base IDs only. Names are broad working categories,
    // not a substitute for a complete MKW collision behavior table.
    private static readonly (string Name, int[] Types)[] KclGroupPresets =
    {
        ("Road / driveable", new[] { 0x00, 0x01, 0x05, 0x09 }),
        ("Off-road", new[] { 0x02, 0x03, 0x04 }),
        ("Boost / jump", new[] { 0x06, 0x07, 0x08 }),
        ("Walls", new[] { 0x0C, 0x0D, 0x0E, 0x0F }),
        ("Fall / boundaries", new[] { 0x0A, 0x10 }),
        ("Other types", new[] { 0x0B, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
                                0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F })
    };

    private void KclGroupPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string name)
            return;
        var preset = KclGroupPresets.FirstOrDefault(p => p.Name == name);
        if (preset.Types is null)
            return;
        var selected = new HashSet<int>(preset.Types);
        _kclUpdatingFilters = true;
        _kclVisibleTypes.Clear();
        foreach (CheckBox box in KclTypeFilterPanel.Children.OfType<CheckBox>())
        {
            int type = (int)box.Tag;
            bool enabled = selected.Contains(type);
            box.IsChecked = enabled;
            if (enabled) _kclVisibleTypes.Add(type);
        }
        _kclUpdatingFilters = false;
        if (_kclLoadedTriangles is { Count: > 0 } triangles)
            ShowKclTriangles(triangles, resetCamera: false);
    }

    private void PopulateKclTypeFilters(IReadOnlyList<Tools.KclTriangle> triangles)
    {
        _kclUpdatingFilters = true;
        KclTypeFilterPanel.Children.Clear();
        _kclVisibleTypes.Clear();
        foreach (var group in triangles.GroupBy(t => (int)(t.Collision.Raw & 0x1F)).OrderBy(g => g.Key))
        {
            int type = group.Key;
            _kclVisibleTypes.Add(type);
            var checkbox = new CheckBox
            {
                Content = $"0x{type:X2}  ({group.Count():N0})",
                Tag = type,
                IsChecked = true,
                Foreground = Brushes.White,
                Margin = new Thickness(3, 2, 10, 2)
            };
            checkbox.Checked += KclTypeFilter_Changed;
            checkbox.Unchecked += KclTypeFilter_Changed;
            KclTypeFilterPanel.Children.Add(checkbox);
        }
        KclGroupPresetPanel.Children.Clear();
        foreach (var preset in KclGroupPresets)
        {
            int count = triangles.Count(t => preset.Types.Contains(t.Collision.Raw & 0x1F));
            if (count == 0)
                continue;
            var button = new Button
            {
                Content = $"{preset.Name} ({count:N0})",
                Tag = preset.Name,
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 5)
            };
            button.Click += KclGroupPreset_Click;
            KclGroupPresetPanel.Children.Add(button);
        }
        _kclUpdatingFilters = false;
    }

    private void KclTypeFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_kclUpdatingFilters || sender is not CheckBox box || box.Tag is not int type)
            return;
        if (box.IsChecked == true) _kclVisibleTypes.Add(type);
        else _kclVisibleTypes.Remove(type);
        if (_kclLoadedTriangles is { Count: > 0 } triangles)
            ShowKclTriangles(triangles, resetCamera: false);
    }

    private void KclFilterAll_Click(object sender, RoutedEventArgs e) => SetKclFilters(true);
    private void KclFilterNone_Click(object sender, RoutedEventArgs e) => SetKclFilters(false);
    private void KclFilterInvert_Click(object sender, RoutedEventArgs e)
    {
        _kclUpdatingFilters = true;
        foreach (CheckBox box in KclTypeFilterPanel.Children.OfType<CheckBox>())
        {
            bool enabled = box.IsChecked != true;
            box.IsChecked = enabled;
            int type = (int)box.Tag;
            if (enabled) _kclVisibleTypes.Add(type);
            else _kclVisibleTypes.Remove(type);
        }
        _kclUpdatingFilters = false;
        if (_kclLoadedTriangles is { Count: > 0 } triangles)
            ShowKclTriangles(triangles, resetCamera: false);
    }

    private void SetKclFilters(bool visible)
    {
        _kclUpdatingFilters = true;
        foreach (CheckBox box in KclTypeFilterPanel.Children.OfType<CheckBox>())
        {
            box.IsChecked = visible;
            int type = (int)box.Tag;
            if (visible) _kclVisibleTypes.Add(type);
            else _kclVisibleTypes.Remove(type);
        }
        _kclUpdatingFilters = false;
        if (_kclLoadedTriangles is { Count: > 0 } triangles)
            ShowKclTriangles(triangles, resetCamera: false);
    }

    // Stable, reproducible color per full 16-bit collision flag. The low 5 bits
    // identify the base collision type; higher bits distinguish its variants.
    private static Color ColorForKclFlag(ushort raw)
    {
        uint hash = raw;
        hash ^= hash >> 7;
        hash *= 0x9E3779B1u;
        hash ^= hash >> 16;
        double hue = (hash % 3600) / 10.0;
        double saturation = 0.64 + ((hash >> 12) & 7) / 70.0;
        double value = 0.84;
        double chroma = value * saturation;
        double sector = hue / 60.0;
        double second = chroma * (1 - Math.Abs(sector % 2 - 1));
        (double r, double g, double b) = ((int)sector) switch
        {
            0 => (chroma, second, 0.0), 1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second), 3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma), _ => (chroma, 0.0, second)
        };
        double m = value - chroma;
        return Color.FromRgb((byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private void ShowKclTriangles(IReadOnlyList<Tools.KclTriangle> triangles, bool resetCamera = true)
    {
        _kclLoadedTriangles = triangles;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        foreach (var t in triangles)
        {
            foreach (var v in new[] { t.A, t.B, t.C })
            {
                minX = Math.Min(minX, v.X); minY = Math.Min(minY, v.Y); minZ = Math.Min(minZ, v.Z);
                maxX = Math.Max(maxX, v.X); maxY = Math.Max(maxY, v.Y); maxZ = Math.Max(maxZ, v.Z);
            }
        }
        if (!double.IsFinite(minX) || !double.IsFinite(maxX))
        {
            KclViewportHint.Text = "Invalid KCL vertex coordinates";
            return;
        }
        // Normalize for stable WPF depth precision even on very large custom tracks.
        double cx = (minX + maxX) * 0.5, cy = (minY + maxY) * 0.5, cz = (minZ + maxZ) * 0.5;
        double extent = Math.Max(1, Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)));
        double unit = 200.0 / extent;
        var model = new Model3DGroup();
        // One material per collision flag; each flag's geometry is chunked independently.
        // This avoids per-triangle WPF GeometryModel3D objects and stays efficient.
        const int chunkSize = 12000;
        var visibleTriangles = triangles.Where(t => _kclVisibleTypes.Contains(t.Collision.Raw & 0x1F));
        var groups = _kclColorById
            ? visibleTriangles.GroupBy(t => t.Collision.Raw).OrderBy(g => g.Key)
            : visibleTriangles.GroupBy(t => (ushort)0);
        int drawn = 0;
        int distinctFlags = triangles.Select(t => t.Collision.Raw).Distinct().Count();
        foreach (var group in groups)
        {
            Color color = _kclColorById ? ColorForKclFlag(group.Key) : Color.FromRgb(173, 185, 196);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            // Enumerate each group once; do not allocate a separate model for each triangle.
            var positions = new Point3DCollection();
            var indices = new Int32Collection();
            var normals = new Vector3DCollection();
            void Flush()
            {
                if (indices.Count == 0) return;
                var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices, Normals = normals };
                mesh.Freeze();
                var geometry = new GeometryModel3D(mesh, material) { BackMaterial = material };
                model.Children.Add(geometry);
                positions = new Point3DCollection();
                indices = new Int32Collection();
                normals = new Vector3DCollection();
            }
            foreach (var t in group)
            {
                var a = new Point3D((t.A.X - cx) * unit, (t.A.Y - cy) * unit, (t.A.Z - cz) * unit);
                var b = new Point3D((t.B.X - cx) * unit, (t.B.Y - cy) * unit, (t.B.Z - cz) * unit);
                var d = new Point3D((t.C.X - cx) * unit, (t.C.Y - cy) * unit, (t.C.Z - cz) * unit);
                Vector3D normal = Vector3D.CrossProduct(b - a, d - a);
                if (normal.LengthSquared < 1e-16) continue;
                normal.Normalize();
                int n = positions.Count;
                positions.Add(a); positions.Add(b); positions.Add(d);
                indices.Add(n); indices.Add(n + 1); indices.Add(n + 2);
                normals.Add(normal); normals.Add(normal); normals.Add(normal);
                drawn++;
                if (indices.Count / 3 >= chunkSize) Flush();
            }
            Flush();
        }
        KclModelVisual.Content = model;
        if (resetCamera)
        {
            _kclCenter = new Point3D(0, 0, 0);
            _kclRadius = Math.Sqrt(3) * 100;
            _kclDistance = 320;
            _kclYaw = 0.65; _kclPitch = 0.85;
            UpdateKclCamera();
        }
        KclViewportHint.Text = $"{drawn:N0}/{triangles.Count:N0} visible triangles · {_kclVisibleTypes.Count} base types · {distinctFlags} total flags · " +
            (_kclColorById ? "colored by full ID" : "gray") + " · drag to orbit · wheel to zoom";
        Console.WriteLine($"[DebugManager] KCL Phase 3 WPF mesh: {drawn} triangles, {model.Children.Count} chunks, " +
            $"{distinctFlags} full collision flags, mode={(_kclColorById ? "color" : "gray")}; library decoder unchanged.");
    }

    private void UpdateKclCamera()
    {
        double h = _kclDistance * Math.Cos(_kclPitch);
        var pos = new Point3D(_kclCenter.X + h * Math.Sin(_kclYaw),
            _kclCenter.Y + _kclDistance * Math.Sin(_kclPitch),
            _kclCenter.Z + h * Math.Cos(_kclYaw));
        KclCamera.Position = pos;
        KclCamera.LookDirection = _kclCenter - pos;
        KclCamera.UpDirection = new Vector3D(0, 1, 0);
        KclCamera.NearPlaneDistance = Math.Max(0.01, _kclDistance / 10000);
        KclCamera.FarPlaneDistance = Math.Max(1000, _kclDistance * 100);
    }

    private void KclViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _kclDragging = true;
        _kclDragPoint = e.GetPosition(KclViewport);
        KclViewport.CaptureMouse();
    }
    private void KclViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _kclDragging = false;
        KclViewport.ReleaseMouseCapture();
    }
    private void KclViewport_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_kclDragging || e.LeftButton != MouseButtonState.Pressed) return;
        Point next = e.GetPosition(KclViewport);
        _kclYaw += (next.X - _kclDragPoint.X) * 0.008;
        _kclPitch = Math.Clamp(_kclPitch + (next.Y - _kclDragPoint.Y) * 0.008, -1.5, 1.5);
        _kclDragPoint = next;
        UpdateKclCamera();
    }
    private void KclViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _kclDistance = Math.Clamp(_kclDistance * Math.Pow(0.88, e.Delta / 120.0), 2, 10000);
        UpdateKclCamera();
        e.Handled = true;
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
        public List<KclFlagRow> KclFlags { get; } = [];
        public string KclSummary { get; set; } = "Not tested";
        public bool KclOk { get; set; }
        public int KclPrisms { get; set; }
        public IReadOnlyList<Tools.KclTriangle>? KclTriangles { get; set; }
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

public sealed record KclFlagRow(string Flag, int Type, int Variant, int Blight, int WheelDepth, int Effects, int Prisms);
