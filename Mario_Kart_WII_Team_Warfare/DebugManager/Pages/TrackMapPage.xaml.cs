using DebugManager.Models;
using DebugManager.Rendering;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Mathematics;
using WF = System.Windows.Forms;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Vector3 = DebugManager.Services.TrackFormats.Vector3;
using DebugManager.Services;
using DebugManager.Services.TrackFormats;
using System.IO;
using IOPath = System.IO.Path;
using Tools = GameManager.Tools.TrackFormats;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace DebugManager.Pages;

public partial class TrackMapPage : Page
{
    private readonly MainWindow _mainWindow;
    private readonly BrresOpenGlRenderer _openGlRenderer = new();
    private bool _kclOverlayEnabled;
    private System.Windows.Controls.StackPanel? _overlayFilterStack;
    private System.Windows.Controls.StackPanel? _kclTypeStack;
    private readonly Dictionary<int, System.Windows.Controls.CheckBox> _overlayLayerChecks = new();
    private System.Windows.Controls.CheckBox? _kclEnabledCheck, _playersEnabledCheck;
    private System.Windows.Controls.ComboBox? _pollCombo;
    private HashSet<int>? _visibleKclTypes; // null = all collision types
    private string? _kclFilterSignature;

    private void Build3dFilterPanel()
    {
        if (TrackOpenGlHost.Parent is not System.Windows.Controls.Grid viewport || _overlayFilterStack is not null) return;
        // WPF controls cannot reliably overlay WindowsFormsHost. Reserve a separate grid column.
        viewport.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        viewport.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(245) });
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        _overlayFilterStack = panel;
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "3D OVERLAYS", FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 9) });
        void AddCheck(string name, bool initial, Action<bool> change, int? bit = null)
        {
            var check = new System.Windows.Controls.CheckBox { Content = name, IsChecked = initial, Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 3, 0, 3) };
            check.Checked += (_, _) => change(true);
            check.Unchecked += (_, _) => change(false);
            panel.Children.Add(check);
            if (bit.HasValue) _overlayLayerChecks[bit.Value] = check;
            else if (name == "KCL wireframe") _kclEnabledCheck = check;
            else if (name == "Live players") _playersEnabledCheck = check;
        }
        AddCheck("Checkpoints", true, v => SetLayer(1, v), 1);
        AddCheck("Enemy paths", true, v => SetLayer(2, v), 2);
        AddCheck("Item paths", true, v => SetLayer(4, v), 4);
        AddCheck("KMP objects", true, v => SetLayer(8, v), 8);
        AddCheck("KCL wireframe", _kclOverlayEnabled, v => _kclOverlayEnabled = v);
        AddCheck("Live players", _show3dPlayers, v => _show3dPlayers = v);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Collision types", Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 13, 0, 5) });
        var allTypes = new System.Windows.Controls.Button { Content = "Show all KCL types", Margin = new Thickness(0, 0, 0, 5) };
        allTypes.Click += (_, _) => { _visibleKclTypes = null; RefreshKclTypeChecks(); };
        panel.Children.Add(allTypes);
        _kclTypeStack = new System.Windows.Controls.StackPanel();
        panel.Children.Add(_kclTypeStack);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Player refresh", Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 13, 0, 5) });
        _pollCombo = new System.Windows.Controls.ComboBox { ItemsSource = new[] { "100 ms", "250 ms", "500 ms" }, SelectedIndex = 0 };
        _pollCombo.SelectionChanged += (_, _) =>
        {
            if (_pollCombo.SelectedItem is string selection && int.TryParse(selection.Split(' ')[0], out int ms))
                SetPlayerRefresh(ms);
        };
        panel.Children.Add(_pollCombo);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "F6: KCL  |  Ctrl+1–5: layers/players  |  Ctrl+6: refresh",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.LightGray,
            FontSize = 11, Margin = new Thickness(0, 15, 0, 0) });
        var scroll = new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 35, 42)) };
        System.Windows.Controls.Grid.SetColumn(scroll, 1);
        viewport.Children.Add(scroll);
        Sync3dFilterPanel();
    }
    private void SetLayer(int bit, bool enabled)
    {
        _kmp3dLayers = enabled ? _kmp3dLayers | bit : _kmp3dLayers & ~bit;
    }
    private void SetPlayerRefresh(int ms)
    {
        _playerRefreshMilliseconds = ms;
        _openGlRenderer.PlayerInterpolationMilliseconds = ms + 10;
        LogOpenGl($"Step 6g.4 player poll={ms} ms");
    }
    private void Sync3dFilterPanel()
    {
        foreach (var entry in _overlayLayerChecks)
            entry.Value.IsChecked = (_kmp3dLayers & entry.Key) != 0;
        if (_kclEnabledCheck is not null) _kclEnabledCheck.IsChecked = _kclOverlayEnabled;
        if (_playersEnabledCheck is not null) _playersEnabledCheck.IsChecked = _show3dPlayers;
        if (_pollCombo is not null) _pollCombo.SelectedItem = $"{_playerRefreshMilliseconds} ms";
    }
    private void RefreshKclTypeChecks()
    {
        if (_kclTypeStack is null) return;
        _kclTypeStack.Children.Clear();
        var types = _track?.CollisionTriangles?.Select(t => (int)t.Collision.Type).Distinct().OrderBy(t => t).ToArray() ?? Array.Empty<int>();
        foreach (int type in types)
        {
            int captured = type;
            var check = new System.Windows.Controls.CheckBox { Content = $"Type {type}", IsChecked = _visibleKclTypes is null || _visibleKclTypes.Contains(type),
                Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(8, 2, 0, 2) };
            void Change(bool selected)
            {
                _visibleKclTypes ??= new HashSet<int>(types);
                if (selected) _visibleKclTypes.Add(captured);
                else _visibleKclTypes.Remove(captured);
            }
            check.Checked += (_, _) => Change(true);
            check.Unchecked += (_, _) => Change(false);
            _kclTypeStack.Children.Add(check);
        }
    }

    private IReadOnlyList<BrresOpenGlRenderer.KmpLine>? _kmp3dLines;
    private int _kmp3dLayers = 15;
    private KmpDocument? _kmp3dSource;
    private bool _show3dPlayers = true;
    // Faster sampling is inexpensive compared with BRRES decoding; 100 ms is a safe starting point.
    private int _playerRefreshMilliseconds = 100;
    private void CyclePlayerRefresh()
    {
        SetPlayerRefresh(_playerRefreshMilliseconds switch { 100 => 250, 250 => 500, _ => 100 });
        Sync3dFilterPanel();
        LogOpenGl($"Step 6g.3 live players: poll={_playerRefreshMilliseconds} ms; interpolation={_openGlRenderer.PlayerInterpolationMilliseconds} ms; Ctrl+6 cycles 100/250/500 ms.");
    }
    private IReadOnlyList<BrresOpenGlRenderer.PlayerMarker> _live3dPlayers = Array.Empty<BrresOpenGlRenderer.PlayerMarker>();
    private IReadOnlyList<BrresOpenGlRenderer.PlayerMarker>? _uploaded3dPlayers;
    private readonly Dictionary<uint, (byte[] Pixels,int Width,int Height)> _portraitCache = new();

    private void Update3dPlayers(IReadOnlyList<LivePlayerSnapshot> players)
    {
        var markers=new List<BrresOpenGlRenderer.PlayerMarker>(players.Count);
        foreach(var p in players)
        {
            if(!float.IsFinite(p.WorldX)||!float.IsFinite(p.WorldY)||!float.IsFinite(p.WorldZ))continue;
            byte[]? pixels=null;int width=0,height=0;
            if(!_portraitCache.TryGetValue(p.CharacterId,out var cached) && p.CharacterImage is BitmapSource bitmap)
            {
                try
                {
                    BitmapSource source=bitmap.Format==PixelFormats.Bgra32?bitmap:
                        new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);
                    width=source.PixelWidth;height=source.PixelHeight;
                    if(width>0 && height>0 && width<=2048 && height<=2048)
                    {
                        pixels=new byte[checked(width*height*4)];
                        source.CopyPixels(pixels,width*4,0);
                        _portraitCache[p.CharacterId]=(pixels,width,height);
                    }
                }
                catch(Exception ex){LogOpenGl("Player portrait conversion: "+ex.Message);}
            }
            else if(cached.Pixels is not null)
                (pixels,width,height)=cached;
            float red=p.StarTimer>0?1f:0.2f;
            float green=p.StarTimer>0?0.8f:0.85f;
            float blue=p.MegaTimer>0?1f:0.95f;
            markers.Add(new BrresOpenGlRenderer.PlayerMarker(p.PlayerId,p.CharacterId,
                p.WorldX,p.WorldY,p.WorldZ,pixels,width,height,red,green,blue,
                p.Character, p.PositionText));
        }
        _live3dPlayers=markers;
    }



    private bool _openGlStarted;
    private bool _openGlFailed;
    private GLControl? _glControl;
    private bool _openGlStarting;
    private bool _openGlFailureQueued;
    private bool _openGlFirstFrameLogged;
    private bool _glRenderBusy;
    private System.Windows.Threading.DispatcherTimer? _glFrameTimer;
    private System.Drawing.Point _glLastMouse;
    private bool _glDragging;

    // This file survives a native graphics-driver crash (which a C# catch cannot handle).
    private static readonly string OpenGlLogPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "DebugManager-OpenGL-startup.log");

    private static void LogOpenGl(string message)
    {
        try
        {
            System.IO.File.AppendAllText(OpenGlLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never crash the viewer */ }
    }
    private CancellationTokenSource? _cancellation;
    private TrackDefinition? _track;
    private bool _baseDrawn;
    private int _baseChildCount;
    private int _staticChildCount;
    private KmpCheckpointGraph? _checkpointGraph;
    private double _cameraYaw = -35;
    private double _cameraPitch = 28;
    private double _cameraDistance = 180;
    private Point _lastCameraMouse;
    private Point3D _cameraTarget = new(0, 0, 0);
    private bool _cameraDragging;
    private bool _cameraNavigationActive;
    private System.Windows.Window? _cameraInputWindow;
    private const double MapWidth = 1100;
    private const double MapHeight = 700;

    public TrackMapPage(MainWindow mainWindow)
    {
        InitializeComponent();
        Build3dFilterPanel();
        _mainWindow = mainWindow;
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            // GL resources are owned by the GL context and released there on the next render.
            _openGlRenderer.SetModel(null);
            StopGlFrameTimer();
            // Detach native child before releasing it; no GL calls after disposal.
            if (_glControl is not null)
            {
                try
                {
                    TrackOpenGlHost.Child = null;
                    _glControl.Dispose();
                }
                catch (Exception ex) { LogOpenGl("GL host dispose: " + ex.Message); }
                _glControl = null;
                _openGlStarted = false;
            }
            _cancellation?.Cancel();
            if (_cameraInputWindow is not null)
            {
                _cameraInputWindow.PreviewKeyDown -= TrackMapPage_PreviewKeyDown;
                _cameraInputWindow = null;
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        System.Windows.Window? host = System.Windows.Window.GetWindow(this);
        if (host is not null && !ReferenceEquals(host, _cameraInputWindow))
        {
            if (_cameraInputWindow is not null)
                _cameraInputWindow.PreviewKeyDown -= TrackMapPage_PreviewKeyDown;
            _cameraInputWindow = host;
            host.PreviewKeyDown += TrackMapPage_PreviewKeyDown;
        }
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = new();
        try { await MonitorAsync(_cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        await using GameManagerConnection connection = new();
        await connection.ConnectAsync(token);
        LiveRaceDataService service = new(connection);
        while (!token.IsCancellationRequested)
        {
            try
            {
                // LE-CODE exposes the actual distribution slot ID at 0x808F9420.
                // Do not use the vanilla RaceDataSettings CourseId for archive lookup.
                JsonElement courseValue = await connection.GetAsync<JsonElement>("LeCodeTrackId", cancellationToken: token);
                uint courseId = courseValue.ValueKind == JsonValueKind.Number && courseValue.TryGetUInt32(out uint id) ? id : uint.MaxValue;
                if (_track is null || _track.CourseId != courseId)
                {
                    _track = LoadTrack(courseId);
                    DrawTrackBase();
                }
                List<LivePlayerSnapshot> players = await service.GetPlayersAsync(token);
                DrawPlayers(players);
                Update3dPlayers(players);
                StatusText.Text = _track?.Summary is { Length: > 0 } summary
                    ? summary
                    : players.Count == 0 ? "Waiting for race data..." : $"{players.Count} slots · LE-CODE slot 0x{courseId:X}";
                HeaderText.Text = _track?.DisplayName ?? "Track Map";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusText.Text = "Waiting for race data · " + ex.Message;
            }
            await Task.Delay(_playerRefreshMilliseconds, token);
        }
    }

    private TrackDefinition LoadTrack(uint courseId)
    {
        // LE-CODE slot IDs are 16-bit values. The default naming is %03x.szs;
        // IDs >= 0x1000 naturally expand to four digits. Some distributions use
        // the optional SLOT-04X setting, so also accept a zero-padded 4-digit name.
        if (courseId == uint.MaxValue)
        {
            return new TrackDefinition
            {
                CourseId = courseId,
                DisplayName = "LE-CODE track ID unavailable",
                Summary = "Could not read the LE-CODE slot ID at 0x808F9420.",
                Waypoints = []
            };
        }

        string trackId = courseId.ToString("x3", System.Globalization.CultureInfo.InvariantCulture);
        string szsDirectory = IOPath.GetFullPath(IOPath.Combine(
            AppContext.BaseDirectory, "..", "..", "szs"));
        string defaultPath = IOPath.Combine(szsDirectory, trackId + ".szs");
        string fourDigitId = courseId.ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
        string fourDigitPath = IOPath.Combine(szsDirectory, fourDigitId + ".szs");
        string szsPath = File.Exists(defaultPath) ? defaultPath : fourDigitPath;

        if (!File.Exists(szsPath))
        {
            return new TrackDefinition
            {
                CourseId = courseId,
                DisplayName = $"Track {trackId} · archive not found",
                ArchivePath = szsPath,
                Summary = $"LE-CODE slot: 0x{courseId:X} · searched: {defaultPath} and {fourDigitPath}",
                Waypoints = []
            };
        }

        try
        {
            LoadedTrackArchive archive = TrackArchiveLoader.Load(szsPath);
            _checkpointGraph = KmpCheckpointDecoder.Decode(archive.Kmp);
            int checkpointCount = archive.Kmp.FindSection("CKPT")?.EntryCount ?? 0;
            int objectCount = archive.Kmp.FindSection("GOBJ")?.EntryCount ?? 0;
            int cpuRouteCount = archive.Kmp.FindSection("ENPT")?.EntryCount ?? 0;
            int itemRouteCount = archive.Kmp.FindSection("ITPT")?.EntryCount ?? 0;
            string modelSummary = "BRRES: course_model.brres not found";
            BrresModelReader.DecodeResult? courseModel = null;
            if (archive.ArchiveFiles.Contains("course_model.brres", StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    SzsArchiveReader modelArchive = SzsArchiveReader.Open(File.ReadAllBytes(szsPath));
                    courseModel = BrresModelReader.Decode(modelArchive.ReadFile("course_model.brres"));
                    modelSummary = courseModel.Summary;
                }
                catch (Exception modelException)
                {
                    modelSummary = "BRRES mesh decode failed: " + modelException.Message;
                    Console.WriteLine("[TrackMap] BRRES mesh decode failed: " + modelException);
                }
            }
            IReadOnlyList<Tools.KclTriangle>? collisionTriangles = null;
            try
            {
                var kclArchive = Tools.SzsArchiveReader.Open(File.ReadAllBytes(szsPath));
                if (kclArchive.ContainsFile("course.kcl"))
                    collisionTriangles = Tools.KclTriangleDecoder.Decode(
                        Tools.KclFileReader.Read(kclArchive.ReadFile("course.kcl"))).Triangles;
            }
            catch (Exception kclError)
            {
                Console.WriteLine("[TrackMap] Optional KCL overlay unavailable: " + kclError);
            }
            return new TrackDefinition
            {
                CollisionTriangles = collisionTriangles,
                CourseId = courseId,
                DisplayName = $"Track {archive.TrackId}",
                ArchivePath = archive.SourcePath,
                Kmp = archive.Kmp,
                Lex = archive.Lex,
                CourseModel = courseModel,
                Summary = $"KMP: {archive.Kmp.Sections.Count} sections · {checkpointCount} checkpoints · {objectCount} objects · {cpuRouteCount} CPU points · {itemRouteCount} item points · LEX: {archive.Lex?.Sections.Count ?? 0} sections · {modelSummary}",
                Waypoints = []
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TrackMap] Could not load '{szsPath}': {ex}");
            return new TrackDefinition
            {
                CourseId = courseId,
                DisplayName = $"Track {trackId} · load failed",
                ArchivePath = szsPath,
                Summary = ex.Message,
                Waypoints = []
            };
        }
    }

    private void DrawTrackBase()
    {
        TrackCanvas.Children.Clear(); _baseDrawn = true;
        TrackCanvas.Width = _track is not null && _track.CanvasWidth > 0 ? _track.CanvasWidth : MapWidth;
        TrackCanvas.Height = _track is not null && _track.CanvasHeight > 0 ? _track.CanvasHeight : MapHeight;
        if (!string.IsNullOrWhiteSpace(_track?.TrackImage))
        {
            string imagePath = IOPath.Combine(AppContext.BaseDirectory, _track.TrackImage.Replace('/', IOPath.DirectorySeparatorChar));
            if (File.Exists(imagePath))
            {
                try
                {
                    BitmapImage bitmap = new(); bitmap.BeginInit(); bitmap.UriSource = new Uri(imagePath); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
                    Image image = new() { Source = bitmap, Width = TrackCanvas.Width, Height = TrackCanvas.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
                    TrackCanvas.Children.Add(image);
                }
                catch (Exception ex) { Console.WriteLine("[TrackMap] Track image error: " + ex.Message); }
            }
        }
        TrackCanvas.Background = new SolidColorBrush(Color.FromRgb(38, 50, 56));
        if (_track?.Waypoints is { Count: > 1 } points)
        {
            Polyline route = new() { Stroke = new SolidColorBrush(Color.FromRgb(38, 198, 218)), StrokeThickness = 12, Opacity = 0.75, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
            foreach (TrackWaypoint point in points) route.Points.Add(new Point(point.X * TrackCanvas.Width, point.Y * TrackCanvas.Height));
            route.Points.Add(new Point(points[0].X * TrackCanvas.Width, points[0].Y * TrackCanvas.Height));
            TrackCanvas.Children.Add(route);
        }
        _baseChildCount = TrackCanvas.Children.Count;
        DrawSelectedOverlays();
        Build3DPrototype();
    }

    private void View3DToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool show3D = View3DToggle.IsChecked == true;
        Map2DScroll.Visibility = show3D ? Visibility.Collapsed : Visibility.Visible;
        Map3DPanel.Visibility = show3D ? Visibility.Visible : Visibility.Collapsed;
        View3DToggle.Content = show3D ? "2D MAP" : "3D TEST";
        UpdateRendererSelection();
        if (show3D && TrackViewport3D.Children.Count == 0) Build3DPrototype();
    }

    private void OpenGlToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (TrackOpenGlHost is null || TrackViewport3D is null) return;
        UpdateRendererSelection();
    }

    private void UpdateRendererSelection()
    {
        if (TrackOpenGlHost is null || TrackViewport3D is null) return;
        bool useGl = View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true && !_openGlFailed;
        TrackOpenGlHost.Visibility = useGl ? Visibility.Visible : Visibility.Collapsed;
        TrackViewport3D.Visibility = useGl ? Visibility.Collapsed : Visibility.Visible;
        // WinForms' HWND is above WPF elements even if the latter are later in the XAML.
        OpenGlStatus.Visibility = Visibility.Collapsed;
        if (!useGl) { StopGlFrameTimer(); return; }
        if (_openGlStarted) { StartGlFrameTimer(); return; }
        if (_openGlStarting) return;
        _openGlStarting = true;
        LogOpenGl("GLControl selected; scheduling native control creation");
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                if (View3DToggle.IsChecked != true || OpenGlToggle.IsChecked != true || _openGlFailed)
                    return;
                LogOpenGl($"BEFORE WinForms GLControl creation; host size={TrackOpenGlHost.ActualWidth:0}x{TrackOpenGlHost.ActualHeight:0}");
                _glControl = new GLControl(new GLControlSettings
                {
                    APIVersion = new Version(3, 3, 0, 0),
                    Profile = ContextProfile.Core,
                    Flags = ContextFlags.Default
                }) { Dock = WF.DockStyle.Fill, TabStop = true };
                _glControl.MouseDown += GlMouseDown;
                _glControl.MouseMove += GlMouseMove;
                _glControl.MouseUp += GlMouseUp;
                _glControl.MouseWheel += GlMouseWheel;
                _glControl.KeyDown += GlKeyDown;
                TrackOpenGlHost.Child = _glControl;
                LogOpenGl("BEFORE GLControl.MakeCurrent()");
                _glControl.MakeCurrent();
                LogOpenGl("AFTER GLControl.MakeCurrent(); GL_VERSION=" + GL.GetString(StringName.Version));
                _openGlStarted = true;
                StartGlFrameTimer();
            }
            catch (Exception ex) { DisableOpenGl(ex); }
            finally { _openGlStarting = false; }
        }));
    }

    private void StartGlFrameTimer()
    {
        _glFrameTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(33), System.Windows.Threading.DispatcherPriority.Render,
            (_, _) => RenderOpenGlFrame(), Dispatcher);
        _glFrameTimer.Start();
    }

    private void StopGlFrameTimer() => _glFrameTimer?.Stop();

    // KMP geometry is built once per track; the library remains graphics-neutral.
    private IReadOnlyList<BrresOpenGlRenderer.KmpLine>? BuildKmp3dLines(KmpDocument? kmp)
    {
        if (kmp is null) return null;
        var segments = new List<BrresOpenGlRenderer.KmpLine>();
        var enemy = kmp.FindSection("ENPT")?.Entries
            .Where(e => e.Length >= 12).Select(e => KmpEntryDecoder.ReadVector3(e)).ToArray()
            ?? Array.Empty<Vector3>();
        float HeightAt(float x, float z)
        {
            if (enemy.Length == 0) return 0;
            var nearest = enemy.MinBy(v => (double)(v.X-x)*(v.X-x)+(double)(v.Z-z)*(v.Z-z));
            return nearest.Y + 80f; // CKPT has XZ only; approximate its display height.
        }
        void Add(float ax,float ay,float az,float bx,float by,float bz,int layer) =>
            segments.Add(new BrresOpenGlRenderer.KmpLine(ax,ay,az,bx,by,bz,layer));
        if (_checkpointGraph is not null)
        {
            foreach (var cp in _checkpointGraph.Checkpoints)
                Add(cp.LeftX,HeightAt(cp.LeftX,cp.LeftZ),cp.LeftZ,
                    cp.RightX,HeightAt(cp.RightX,cp.RightZ),cp.RightZ,1);
            foreach (var edge in _checkpointGraph.BuildRouteEdges())
            {
                if (edge.From < 0 || edge.From >= _checkpointGraph.Checkpoints.Count ||
                    edge.To < 0 || edge.To >= _checkpointGraph.Checkpoints.Count) continue;
                var a = _checkpointGraph.Checkpoints[edge.From];
                var b = _checkpointGraph.Checkpoints[edge.To];
                Add((float)a.CenterX,HeightAt((float)a.CenterX,(float)a.CenterZ),(float)a.CenterZ,
                    (float)b.CenterX,HeightAt((float)b.CenterX,(float)b.CenterZ),(float)b.CenterZ,1);
            }
        }
        foreach (var (pointsMagic, groupsMagic, layer) in new[]
            { ("ENPT","ENPH",2), ("ITPT","ITPH",4) })
        {
            var points = kmp.FindSection(pointsMagic)?.Entries
                .Where(e => e.Length >= 12).Select(e => KmpEntryDecoder.ReadVector3(e)).ToArray()
                ?? Array.Empty<Vector3>();
            var groups = kmp.FindSection(groupsMagic)?.Entries;
            if (groups is null) continue;
            foreach (var g in groups)
            {
                if (g.Length < 2) continue;
                int start=g[0], count=g[1];
                if (count < 1 || start + count > points.Length) continue;
                for (int i=start; i<start+count-1; i++)
                {
                    var a=points[i]; var b=points[i+1];
                    Add(a.X,a.Y+65,a.Z,b.X,b.Y+65,b.Z,layer);
                }
                // Small cross markers identify waypoints, including singleton groups.
                for (int i=start; i<start+count; i++)
                {
                    var v=points[i];
                    Add(v.X-35,v.Y+65,v.Z,v.X+35,v.Y+65,v.Z,layer);
                    Add(v.X,v.Y+65,v.Z-35,v.X,v.Y+65,v.Z+35,layer);
                }
            }
        }
        var objects=kmp.FindSection("GOBJ")?.Entries;
        if (objects is not null)
            foreach (var entry in objects)
            {
                if (entry.Length < 16) continue;
                var v=KmpEntryDecoder.ReadVector3(entry,0x04);
                const float d=90;
                Add(v.X-d,v.Y,v.Z,v.X+d,v.Y,v.Z,8);
                Add(v.X,v.Y-d,v.Z,v.X,v.Y+d,v.Z,8);
                Add(v.X,v.Y,v.Z-d,v.X,v.Y,v.Z+d,8);
            }
        LogOpenGl($"Step 6g.1 KMP geometry: {segments.Count} segments; Ctrl+1 checkpoints, Ctrl+2 enemy, Ctrl+3 item, Ctrl+4 objects, Ctrl+0 all.");
        return segments;
    }

    private void RenderOpenGlFrame()
    {
        if (_glRenderBusy || !_openGlStarted || _glControl is not { IsDisposed: false } control ||
            !control.IsHandleCreated || View3DToggle.IsChecked != true || OpenGlToggle.IsChecked != true ||
            _openGlFailed || TrackOpenGlHost.Visibility != Visibility.Visible) return;
        _glRenderBusy = true;
        try
        {
            control.MakeCurrent();
            if (!_openGlFirstFrameLogged)
            {
                _openGlFirstFrameLogged = true;
                LogOpenGl("FIRST GL frame; model=" + (_track?.CourseModel is null ? "null" : "present"));
            }
            if (!ReferenceEquals(_kmp3dSource, _track?.Kmp))
            {
                _kmp3dSource = _track?.Kmp;
                _kmp3dLines = BuildKmp3dLines(_track?.Kmp);
            }
            if (!ReferenceEquals(_uploaded3dPlayers, _live3dPlayers))
            {
                _openGlRenderer.SetPlayerMarkers(_live3dPlayers);
                _uploaded3dPlayers = _live3dPlayers;
            }
            _openGlRenderer.ShowPlayerMarkers = _show3dPlayers;
            _openGlRenderer.SetKmpLines(_kmp3dLines);
            _openGlRenderer.KmpVisibleLayers = _kmp3dLayers;
            _openGlRenderer.SetKcl(_track?.CollisionTriangles);
            _openGlRenderer.ShowKclOverlay = _kclOverlayEnabled;
            _openGlRenderer.VisibleKclTypes = _visibleKclTypes;
            var signature = string.Join(",", _track?.CollisionTriangles?.Select(t => (int)t.Collision.Type).Distinct().OrderBy(t => t) ?? Enumerable.Empty<int>());
            if (_kclFilterSignature != signature)
            {
                _kclFilterSignature = signature;
                _visibleKclTypes = null; // New track starts with every collision type visible.
                RefreshKclTypeChecks();
            }
            _openGlRenderer.Draw(_track?.CourseModel, Math.Max(1, control.ClientSize.Width),
                Math.Max(1, control.ClientSize.Height),
                _cameraYaw, _cameraPitch, _cameraDistance, _cameraTarget);
            control.SwapBuffers();
        }
        catch (Exception ex) { DisableOpenGl(ex); }
        finally { _glRenderBusy = false; }
    }

    private void DisableOpenGl(Exception ex)
    {
        if (_openGlFailureQueued) return;
        _openGlFailureQueued = true;
        _openGlFailed = true;
        StopGlFrameTimer();
        LogOpenGl("OpenGL disabled: " + ex);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            OpenGlToggle.IsChecked = false;
            StatusText.ToolTip = $"OpenGL failed (WPF restored): {ex}\nLog: {OpenGlLogPath}";
            UpdateRendererSelection();
        }));
    }

    // Native WinForms mouse events do not bubble into the WPF Viewport3D handlers.
    private void GlMouseDown(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left) return;
        _cameraNavigationActive = true;
        _glControl?.Focus();
        _glDragging = true;
        _glLastMouse = e.Location;
        if (_glControl is not null) _glControl.Capture = true;
    }

    private void GlMouseMove(object? sender, WF.MouseEventArgs e)
    {
        if (!_glDragging) return;
        _cameraYaw += (e.X - _glLastMouse.X) * 0.55;
        _cameraPitch -= (e.Y - _glLastMouse.Y) * 0.45;
        _glLastMouse = e.Location;
        Update3DCamera();
    }

    private void GlMouseUp(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left) return;
        _glDragging = false;
        if (_glControl is not null) _glControl.Capture = false;
    }

    private void GlMouseWheel(object? sender, WF.MouseEventArgs e)
    {
        _cameraDistance = Math.Clamp(_cameraDistance * (e.Delta > 0 ? 0.88 : 1.14), 1, 50000);
        Update3DCamera();
    }

    private void CycleTevInputPreview()
    {
        _openGlRenderer.TevInputPreviewMode =
            (_openGlRenderer.TevInputPreviewMode + 1) % BrresOpenGlRenderer.PreviewModeCount;
        string label = _openGlRenderer.TevInputPreviewMode switch
        {
            1 => "TEV stage 0 texture-input preview (not blended)",
            2 => "TEV stage 1 texture-input preview (not blended)",
            3 => "Step 6d.2 experimental two-stage TEV equations",
            4 => "Step 6d.5 sampled indirect source texture (not TEV composition)",
            5 => "Step 6d.5 indirect matrix offset visualization (not TEV composition)",
            6 => "Step 6d.5 Pass 3 indirect TEV offsets (experimental; regular UV fallback possible)",
            7 => "Step 6d.5 Pass 4 position-projected texgen (CAMERA projection diagnostic, not GX-exact)",
            8 => "Step 6d.5 Pass 4f FIFO frame-0 captured GX projection (FROZEN VIEW; not live GX)",
            9 => "Step 6d.5 Pass 4h Dolphin GX source-row/inputform and post-matrix order (TEST)",
            10 => "Step 6d.5 Pass 4i GLSL textureProj reference against F8 mode 9 (TEST)",
            11 => "Step 6d.5 Pass 4i interpolated Q sign: red negative / green positive / yellow near Q=0",
            12 => "Step 6d.5 Pass 4j experimental material21 projected stage1 + two-stage TEV (frozen FIFO frame0)",
            13 => "Step 6d.5 Pass 4k CONSOLIDATED TEV: regular + indirect water + projected material21 (experimental)",
            14 => "Step 6e window-frame A/B: regular TEV OFF; retain baseline + indirect + projected",
            15 => "Step 6e window-frame A/B: all TEV ON, regular TEV polygon offset OFF",
            16 => "Step 6e window-frame A/B: regular TEV with SRC_ALPHA blending (indirect/projected unchanged)",
            17 => "Step 6e window-frame A/B: regular TEV forced 50% blend for comparison (NOT GX-exact)",
            18 => "Step 6f Pass 2: single-stage generalized TEV overlays (EXPERIMENTAL)",
            19 => "Step 6f Pass 3: generalized consolidated TEV (regular + water + projected + single-stage; DEFAULT)",
            20 => "Step 6f Pass 4: generalized TEV GX comparison operations (EXPERIMENTAL; same batches as mode 19)",
            21 => "Step 6f Pass 5a: GX TEV texture/raster swap tables + comparison operations (EXPERIMENTAL)",
            22 => "Step 6f Pass 5b: GX signed 11-bit TEV register writeback (A/B, EXPERIMENTAL)",
            23 => "Step 6f Pass 5e: complete GX TEV stage order, texture-disabled stages + up to 3 distinct texture inputs (EXPERIMENTAL)",
            _ => "Verified first-layer baseline"
        };
        LogOpenGl("Step 6d: " + label);
        System.Diagnostics.Debug.WriteLine("Step 6d: " + label);
    }

    // Step 6e: a one-key visual A/B comparison, without consuming F8's modes.
    private void ToggleBaselineVsConsolidated()
    {
        _openGlRenderer.TevInputPreviewMode =
            _openGlRenderer.TevInputPreviewMode == BrresOpenGlRenderer.SingleStageWithConsolidatedMode
                ? BrresOpenGlRenderer.BaselineMode
                : BrresOpenGlRenderer.SingleStageWithConsolidatedMode;
        string label = _openGlRenderer.TevInputPreviewMode == BrresOpenGlRenderer.BaselineMode
            ? "original WPF-parity OpenGL baseline (F9)"
            : "generalized consolidated TEV (F9; regular + water + projected + single-stage)";
        LogOpenGl("Step 6e: " + label);
        System.Diagnostics.Debug.WriteLine("Step 6e: " + label);
    }

    private void CycleWindowFrameMaterialExclusion()
    {
        string status = _openGlRenderer.CycleSuppressedRegularTevMaterial();
        LogOpenGl("Step 6e F10 material isolation: " + status);
        System.Diagnostics.Debug.WriteLine("Step 6e F10 material isolation: " + status);
    }

    private void CycleMaterial7StageTest()
    {
        string status = _openGlRenderer.CycleMaterial7WindowTest();
        LogOpenGl(status);
        System.Diagnostics.Debug.WriteLine(status);
    }

    private void CycleMaterial7InputTest()
    {
        string status = _openGlRenderer.CycleMaterial7InputTest();
        LogOpenGl(status);
        System.Diagnostics.Debug.WriteLine(status);
    }

    private void CycleMaterial7UvSamplingTest()
    {
        string status = _openGlRenderer.CycleMaterial7UvSamplingTest();
        LogOpenGl(status);
        System.Diagnostics.Debug.WriteLine(status);
    }

    private void GlKeyDown(object? sender, WF.KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == WF.Keys.D6)
        {
            CyclePlayerRefresh(); e.Handled = true; return;
        }
        if (e.Control && e.KeyCode == WF.Keys.D5)
        {
            _show3dPlayers = !_show3dPlayers;
            Sync3dFilterPanel();
            LogOpenGl("Step 6g.2 live player heads: " + (_show3dPlayers ? "ON" : "OFF"));
            e.Handled = true; return;
        }
        if (e.KeyCode == WF.Keys.F7)
        {
            CycleMaterial7UvSamplingTest();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == WF.Keys.F12)
        {
            CycleMaterial7InputTest();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == WF.Keys.F11)
        {
            CycleMaterial7StageTest();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == WF.Keys.F10)
        {
            CycleWindowFrameMaterialExclusion();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == WF.Keys.F9)
        {
            ToggleBaselineVsConsolidated();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == WF.Keys.F8)
        {
            CycleTevInputPreview();
            e.Handled = true;
            return;
        }
        if (!_cameraNavigationActive) return;
        if (e.KeyCode == WF.Keys.R)
        {
            _cameraTarget = new Point3D(0, 0, 0);
            Update3DCamera();
            e.Handled = true;
            return;
        }
        double forward = e.KeyCode switch
        {
            WF.Keys.Z or WF.Keys.W or WF.Keys.Up => 1,
            WF.Keys.S or WF.Keys.Down => -1,
            _ => 0
        };
        double sideways = e.KeyCode switch
        {
            WF.Keys.D or WF.Keys.Right => 1,
            WF.Keys.Q or WF.Keys.A or WF.Keys.Left => -1,
            _ => 0
        };
        if (forward == 0 && sideways == 0) return;
        double yaw = _cameraYaw * Math.PI / 180.0;
        Vector3D towardFocus = new(-Math.Sin(yaw), 0, -Math.Cos(yaw));
        Vector3D right = new(Math.Cos(yaw), 0, -Math.Sin(yaw));
        double speed = Math.Clamp(_cameraDistance * 0.045, 0.15, 100);
        if (e.Shift) speed *= 3;
        _cameraTarget += (towardFocus * forward + right * sideways) * speed;
        Update3DCamera();
        e.Handled = true;
    }

    private void Build3DPrototype()
    {
        TrackViewport3D.Children.Clear();
        _openGlRenderer.SetModel(_track?.CourseModel);
        if (_track?.CourseModel is not { } model)
        {
            StatusText.Text = "3D model unavailable · " + (_track?.Summary ?? "No track loaded");
            return;
        }

        MeshGeometry3D source = model.Geometry;
        double cx = (model.Minimum.X + model.Maximum.X) * 0.5;
        double cy = (model.Minimum.Y + model.Maximum.Y) * 0.5;
        double cz = (model.Minimum.Z + model.Maximum.Z) * 0.5;
        double span = Math.Max(model.Maximum.X - model.Minimum.X,
            Math.Max(model.Maximum.Y - model.Minimum.Y, model.Maximum.Z - model.Minimum.Z));
        if (span <= 0) span = 1;

        // Convert the game's world-unit scale to convenient WPF units and center the model.
        Transform3DGroup transform = new();
        transform.Children.Add(new TranslateTransform3D(-cx, -cy, -cz));
        transform.Children.Add(new ScaleTransform3D(0.001, 0.001, 0.001));
        Material material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(155, 163, 168)));
        Model3DGroup scene = new();
        BrresTiledRenderer.Result? textureResult = null;
        try
        {
            textureResult = BrresTiledRenderer.Build(model);
            textureResult.Models.Transform = transform;
            scene.Children.Add(textureResult.Models);
        }
        catch (Exception ex)
        {
            // Never lose a working track mesh because of texture rendering.
            GeometryModel3D geometry = new(source, material) { BackMaterial = material, Transform = transform };
            scene.Children.Add(geometry);
            StatusText.ToolTip = $"Textured renderer failed: {ex.Message}";
        }
        scene.Children.Add(new AmbientLight(Color.FromRgb(105, 105, 105)));
        scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.4, -1, -0.6)));
        TrackViewport3D.Children.Add(new ModelVisual3D { Content = scene });

        _cameraDistance = Math.Max(span * 0.001 * 1.8, 8);
        _cameraYaw = -35;
        _cameraPitch = 28;
        _cameraTarget = new Point3D(0, 0, 0);
        Update3DCamera();
        StatusText.Text = model.Summary;
        StatusText.ToolTip = string.Join(Environment.NewLine, new[]
        {
            $"Geometry: {model.DecodedPolygonCount}/{model.PolygonCount} polygons decoded; {model.SkippedPolygonCount} skipped.",
            $"Materials: {model.Materials.AssignedPolygons}/{model.Materials.PolygonCount} polygon assignments; textures {model.Materials.DecodedTextures}/{model.Materials.TextureCount} decoded.",
            string.Join(Environment.NewLine, model.MaterialLayerDiagnostics),
            $"UV0: {model.UvDecodedPolygons} polygons decoded; {model.UvMissingPolygons} without UV0; {model.UvFailedPolygons} UV failures (geometry preserved).",
            $"UV1: {model.Uv1DecodedPolygons} polygons decoded; {model.Uv1MissingPolygons} without UV1; {model.Uv1FailedPolygons} UV failures (geometry preserved; TEV rendering not yet enabled).",
            textureResult is null ? "Textured renderer unavailable; original grey mesh retained." :
                $"Textured rendering: {textureResult.TexturedPolygons} polygons textured, {textureResult.FallbackPolygons} grey fallback, {textureResult.TileTriangles} output triangles. " +
                $"Step 6c rendered: {textureResult.GeneratorPolygons} polygons using XF texgens ({textureResult.SrtPolygons} SRT-transformed), " +
                $"{textureResult.LegacyUvPolygons} using safe legacy UV0 fallback. " +
                $"Step 6d TEV: {textureResult.TevBakedPolygons} multi-stage polygons baked; " +
                $"{textureResult.TevFallbackPolygons} multi-stage polygons kept on Step 6c fallback. " +
                string.Join("; ", textureResult.TevReasons),
            model.Warnings.Count == 0 ? "No geometry or resource warnings." : string.Join(Environment.NewLine, model.Warnings.Take(40))
        });
    }

    private void Update3DCamera()
    {
        double yaw = _cameraYaw * Math.PI / 180.0;
        double pitch = Math.Clamp(_cameraPitch, -80, 80) * Math.PI / 180.0;
        double horizontal = _cameraDistance * Math.Cos(pitch);
        Vector3D offset = new(horizontal * Math.Sin(yaw), _cameraDistance * Math.Sin(pitch), horizontal * Math.Cos(yaw));
        Point3D position = _cameraTarget + offset;
        TrackViewport3D.Camera = new PerspectiveCamera
        {
            Position = position,
            LookDirection = _cameraTarget - position,
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 45,
            NearPlaneDistance = 0.01,
            FarPlaneDistance = Math.Max(10000, _cameraDistance * 100)
        };
    }

    private void TrackViewport3D_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        _cameraNavigationActive = true;
        System.Windows.Input.Keyboard.Focus((System.Windows.IInputElement)sender);
        _cameraDragging = true;
        _lastCameraMouse = e.GetPosition((System.Windows.IInputElement)sender);
        ((System.Windows.UIElement)sender).CaptureMouse();
    }

    private void TrackViewport3D_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_cameraDragging) return;
        Point current = e.GetPosition((System.Windows.IInputElement)sender);
        _cameraYaw += (current.X - _lastCameraMouse.X) * 0.55;
        _cameraPitch -= (current.Y - _lastCameraMouse.Y) * 0.45;
        _lastCameraMouse = current;
        Update3DCamera();
    }

    private void TrackViewport3D_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _cameraDragging = false;
        ((System.Windows.UIElement)sender).ReleaseMouseCapture();
    }

    private void TrackViewport3D_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        _cameraDistance = Math.Clamp(_cameraDistance * (e.Delta > 0 ? 0.88 : 1.14), 1, 50000);
        Update3DCamera();
    }

    // Camera-relative QZSD / WASD navigation. Focus the 3D viewport by clicking it.
    private void TrackMapPage_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true &&
            e.Key == System.Windows.Input.Key.D6 &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            CyclePlayerRefresh(); e.Handled = true; return;
        }

        if (View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true &&
            e.Key == System.Windows.Input.Key.D5 &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            _show3dPlayers = !_show3dPlayers;
            Sync3dFilterPanel();
            LogOpenGl("Step 6g.2 live player heads: " + (_show3dPlayers ? "ON" : "OFF"));
            e.Handled = true; return;
        }

        if (View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            int bit = e.Key switch
            {
                System.Windows.Input.Key.D1 or System.Windows.Input.Key.NumPad1 => 1,
                System.Windows.Input.Key.D2 or System.Windows.Input.Key.NumPad2 => 2,
                System.Windows.Input.Key.D3 or System.Windows.Input.Key.NumPad3 => 4,
                System.Windows.Input.Key.D4 or System.Windows.Input.Key.NumPad4 => 8,
                _ => 0
            };
            if (bit != 0 || e.Key is System.Windows.Input.Key.D0 or System.Windows.Input.Key.NumPad0)
            {
                if (bit == 0) _kmp3dLayers = _kmp3dLayers == 15 ? 0 : 15;
                else _kmp3dLayers ^= bit;
                Sync3dFilterPanel();
                LogOpenGl($"Step 6g.1 KMP filters: mask={_kmp3dLayers} CKPT={(_kmp3dLayers&1)!=0} ENPT={(_kmp3dLayers&2)!=0} ITPT={(_kmp3dLayers&4)!=0} GOBJ={(_kmp3dLayers&8)!=0}");
                e.Handled = true;
                return;
            }
        }
        if (View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true &&
            e.Key == System.Windows.Input.Key.F6)
        {
            _kclOverlayEnabled = !_kclOverlayEnabled;
            Sync3dFilterPanel();
            LogOpenGl("KCL Phase 5: F6 overlay " + (_kclOverlayEnabled ? "ON" : "OFF") +
                $"; triangles={_track?.CollisionTriangles?.Count ?? 0}");
            e.Handled = true;
            return;
        }

        if (View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true &&
            e.Key == System.Windows.Input.Key.F7)
        {
            CycleMaterial7UvSamplingTest();
            e.Handled = true;
            return;
        }
        if (View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true &&
            e.Key == System.Windows.Input.Key.F12)
        {
            CycleMaterial7InputTest();
            e.Handled = true;
            return;
        }
        if (View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true &&
            e.Key == System.Windows.Input.Key.F11)
        {
            CycleMaterial7StageTest();
            e.Handled = true;
            return;
        }
        if (View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true &&
            e.Key == System.Windows.Input.Key.F10)
        {
            CycleWindowFrameMaterialExclusion();
            e.Handled = true;
            return;
        }
        if (View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true &&
            e.Key == System.Windows.Input.Key.F9)
        {
            ToggleBaselineVsConsolidated();
            e.Handled = true;
            return;
        }
        if (View3DToggle.IsChecked == true && OpenGlToggle.IsChecked == true &&
            e.Key == System.Windows.Input.Key.F8)
        {
            CycleTevInputPreview();
            e.Handled = true;
            return;
        }
        if (View3DToggle.IsChecked != true || !_cameraNavigationActive)
            return;
        // Do not steal keystrokes from editable controls.
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
            || System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.PasswordBox)
            return;

        // Avoid intercepting keyboard shortcuts while an editable control has focus.
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key == System.Windows.Input.Key.R)
        {
            _cameraTarget = new Point3D(0, 0, 0);
            Update3DCamera();
            e.Handled = true;
            return;
        }

        double forward = key switch
        {
            System.Windows.Input.Key.Z or System.Windows.Input.Key.W or System.Windows.Input.Key.Up => 1,
            System.Windows.Input.Key.S or System.Windows.Input.Key.Down => -1,
            _ => 0
        };
        double sideways = key switch
        {
            System.Windows.Input.Key.D or System.Windows.Input.Key.Right => 1,
            System.Windows.Input.Key.Q or System.Windows.Input.Key.A or System.Windows.Input.Key.Left => -1,
            _ => 0
        };
        if (forward == 0 && sideways == 0) return;

        // Keep navigation parallel to the course plane, independent of camera pitch.
        double yaw = _cameraYaw * Math.PI / 180.0;
        Vector3D towardFocus = new(-Math.Sin(yaw), 0, -Math.Cos(yaw));
        Vector3D right = new(Math.Cos(yaw), 0, -Math.Sin(yaw));
        double speed = Math.Clamp(_cameraDistance * 0.045, 0.15, 100);
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
            speed *= 3;
        Vector3D move = (towardFocus * forward + right * sideways) * speed;
        _cameraTarget += move;
        Update3DCamera();
        e.Handled = true;
    }

    private void OverlayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_baseDrawn) return;
        while (TrackCanvas.Children.Count > _baseChildCount) TrackCanvas.Children.RemoveAt(TrackCanvas.Children.Count - 1);
        DrawSelectedOverlays();
    }

    private (double MinX, double MaxX, double MinZ, double MaxZ) GetTrackWorldBounds()
    {
        // GOBJ positions are intentionally excluded: isolated decorative objects
        // must never change the scale or framing of the playable course.
        List<float> xs = [];
        List<float> zs = [];

        if (_checkpointGraph is not null)
        {
            foreach (KmpCheckpoint checkpoint in _checkpointGraph.Checkpoints)
            {
                xs.Add(checkpoint.LeftX);
                xs.Add(checkpoint.RightX);
                zs.Add(checkpoint.LeftZ);
                zs.Add(checkpoint.RightZ);
            }
        }

        if (_track?.Kmp is KmpDocument kmp)
        {
            foreach (string magic in new[] { "ENPT", "ITPT" })
            {
                KmpSection? section = kmp.FindSection(magic);
                if (section is null) continue;

                foreach (byte[] entry in section.Entries)
                {
                    Vector3 position = KmpEntryDecoder.ReadVector3(entry);
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Z)) continue;
                    xs.Add(position.X);
                    zs.Add(position.Z);
                }
            }
        }

        if (xs.Count == 0 || zs.Count == 0) return (0, 1, 0, 1);
        return (xs.Min(), xs.Max(), zs.Min(), zs.Max());
    }

    private double GetWorldToCanvasScale()
    {
        (double minX, double maxX, double minZ, double maxZ) = GetTrackWorldBounds();
        const double margin = 45;
        return Math.Min(
            (TrackCanvas.Width - 2 * margin) / Math.Max(1, maxX - minX),
            (TrackCanvas.Height - 2 * margin) / Math.Max(1, maxZ - minZ));
    }

    private Point ProjectWorld(float x, float z, double minX, double maxX, double minZ, double maxZ)
    {
        // Every KMP layer uses the same checkpoint/route bounds.
        (minX, maxX, minZ, maxZ) = GetTrackWorldBounds();
        const double margin = 45;
        double sx = Math.Max(1, maxX - minX);
        double sz = Math.Max(1, maxZ - minZ);
        double scale = Math.Min((TrackCanvas.Width - 2 * margin) / sx,
                                (TrackCanvas.Height - 2 * margin) / sz);
        double ox = (TrackCanvas.Width - sx * scale) / 2;
        double oy = (TrackCanvas.Height - sz * scale) / 2;
        return new Point(ox + (x - minX) * scale,
                         TrackCanvas.Height - (oy + (z - minZ) * scale));
    }

    private bool IsObjectNearTrack(float x, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z)) return false;

        (double minX, double maxX, double minZ, double maxZ) = GetTrackWorldBounds();
        double trackWidth = Math.Max(1, maxX - minX);
        double trackDepth = Math.Max(1, maxZ - minZ);

        // Allow a 25% course-size buffer for roadside objects, while rejecting
        // isolated distant references such as the far-away sun/effect object.
        double margin = Math.Max(trackWidth, trackDepth) * 0.25;
        return x >= minX - margin && x <= maxX + margin &&
               z >= minZ - margin && z <= maxZ + margin;
    }

    private void DrawSelectedOverlays()
    {
        if (_track?.Kmp is null) { _staticChildCount=TrackCanvas.Children.Count; return; }
        if (CheckpointsToggle.IsChecked==true && _checkpointGraph is not null) DrawCheckpoints(_checkpointGraph);
        if (CpuRoutesToggle.IsChecked==true) DrawPointRoutes("ENPT","ENPH",Colors.DarkOrange);
        if (ItemRoutesToggle.IsChecked==true) DrawPointRoutes("ITPT","ITPH",Colors.LimeGreen);
        if (ObjectsToggle.IsChecked==true) DrawObjects();
        _staticChildCount=TrackCanvas.Children.Count;
    }

    private void DrawCheckpoints(KmpCheckpointGraph graph)
    {
        if (graph.Checkpoints.Count==0) return;
        double minX=graph.Checkpoints.Min(c=>Math.Min(c.LeftX,c.RightX)), maxX=graph.Checkpoints.Max(c=>Math.Max(c.LeftX,c.RightX));
        double minZ=graph.Checkpoints.Min(c=>Math.Min(c.LeftZ,c.RightZ)), maxZ=graph.Checkpoints.Max(c=>Math.Max(c.LeftZ,c.RightZ));
        foreach(var edge in graph.BuildRouteEdges()) { var a=graph.Checkpoints[edge.From]; var b=graph.Checkpoints[edge.To]; Point pa=ProjectWorld((float)a.CenterX,(float)a.CenterZ,minX,maxX,minZ,maxZ), pb=ProjectWorld((float)b.CenterX,(float)b.CenterZ,minX,maxX,minZ,maxZ); TrackCanvas.Children.Add(new Line { X1=pa.X,Y1=pa.Y,X2=pb.X,Y2=pb.Y,Stroke=Brushes.Cyan,StrokeThickness=2,Opacity=.85,IsHitTestVisible=false }); }
        foreach(var cp in graph.Checkpoints) { Point a=ProjectWorld(cp.LeftX,cp.LeftZ,minX,maxX,minZ,maxZ), b=ProjectWorld(cp.RightX,cp.RightZ,minX,maxX,minZ,maxZ); Color c=cp.IsLapCounter?Colors.Gold:cp.IsKeyCheckpoint?Colors.Magenta:Colors.DeepSkyBlue; TrackCanvas.Children.Add(new Line { X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y,Stroke=new SolidColorBrush(c),StrokeThickness=4,IsHitTestVisible=false }); }
    }

    private void DrawPointRoutes(string pointMagic,string groupMagic,Color color)
    {
        KmpSection? pts=_track?.Kmp?.FindSection(pointMagic), grps=_track?.Kmp?.FindSection(groupMagic); if(pts is null||grps is null||pts.Entries.Count==0)return;
        var pos=pts.Entries.Select(e=>KmpEntryDecoder.ReadVector3(e)).ToArray(); float minX=pos.Min(v=>v.X),maxX=pos.Max(v=>v.X),minZ=pos.Min(v=>v.Z),maxZ=pos.Max(v=>v.Z);
        foreach(byte[] g in grps.Entries) { int first=g[0],count=g[1]; if(count==0||first+count>pos.Length)continue; Polyline line=new(){Stroke=new SolidColorBrush(color),StrokeThickness=2.5,Opacity=.9,StrokeDashArray=new DoubleCollection{5,3},IsHitTestVisible=false}; for(int i=first;i<first+count;i++) line.Points.Add(ProjectWorld(pos[i].X,pos[i].Z,minX,maxX,minZ,maxZ)); TrackCanvas.Children.Add(line); for(int i=first;i<first+count;i++){Point q=ProjectWorld(pos[i].X,pos[i].Z,minX,maxX,minZ,maxZ); Ellipse dot=new(){Width=7,Height=7,Fill=new SolidColorBrush(color),Stroke=Brushes.Black,StrokeThickness=1,ToolTip=$"{pointMagic} #{i}"}; Canvas.SetLeft(dot,q.X-3.5);Canvas.SetTop(dot,q.Y-3.5);TrackCanvas.Children.Add(dot);} }
    }

    private void DrawObjects()
    {
        KmpSection? section = _track?.Kmp?.FindSection("GOBJ");
        if (section is null || section.Entries.Count == 0) return;

        for (int i = 0; i < section.Entries.Count; i++)
        {
            byte[] entry = section.Entries[i];
            if (entry.Length < 0x3C) continue;

            // Wii KMP GOBJ: object ID at 0x00, position vector at 0x04.
            ushort objectId = KmpEntryDecoder.ReadUInt16(entry, 0x00);
            Vector3 position = KmpEntryDecoder.ReadVector3(entry, 0x04);
            if (!IsObjectNearTrack(position.X, position.Z)) continue;

            Point point = ProjectWorld(position.X, position.Z, 0, 1, 0, 1);
            Ellipse marker = new()
            {
                Width = 11,
                Height = 11,
                Fill = Brushes.OrangeRed,
                Stroke = Brushes.White,
                StrokeThickness = 1,
                ToolTip = $"Object 0x{objectId:X4} · #{i} · X {position.X:F1}, Y {position.Y:F1}, Z {position.Z:F1}"
            };
            Canvas.SetLeft(marker, point.X - marker.Width / 2);
            Canvas.SetTop(marker, point.Y - marker.Height / 2);
            TrackCanvas.Children.Add(marker);
        }
    }

    private void DrawPlayers(IReadOnlyList<LivePlayerSnapshot> players)
    {
        if (!_baseDrawn) DrawTrackBase();

        // Keep the static map layers and replace only the live player markers.
        while (TrackCanvas.Children.Count > _staticChildCount)
            TrackCanvas.Children.RemoveAt(TrackCanvas.Children.Count - 1);

        double worldScale = GetWorldToCanvasScale();
        // Small courses retain the familiar 64 px icons. On very large courses,
        // scale the icon down with the map zoom so the markers do not hide the road.
        double iconSize = Math.Clamp(64.0 * Math.Sqrt(worldScale / 0.75), 24.0, 64.0);
        double borderSize = iconSize + 8;
        double labelFontSize = Math.Clamp(iconSize * 0.19, 8.0, 13.0);

        foreach (LivePlayerSnapshot player in players)
        {
            Point point = GetPlayerWorldPoint(player);
            Border marker = new()
            {
                Width = borderSize,
                Height = borderSize,
                CornerRadius = new CornerRadius(borderSize / 2),
                BorderThickness = new Thickness(Math.Clamp(iconSize * 0.045, 1.0, 3.0)),
                BorderBrush = EffectBrush(player),
                Background = new SolidColorBrush(Color.FromArgb(225, 25, 25, 25)),
                ToolTip = $"{player.Character} · P{player.PlayerId + 1} · Place {player.PositionText} · {player.EffectText}"
            };

            Grid content = new();
            if (player.CharacterImage is not null)
            {
                content.Children.Add(new Image
                {
                    Source = player.CharacterImage,
                    Width = iconSize,
                    Height = iconSize,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(2)
                });
            }
            else
            {
                content.Children.Add(new TextBlock
                {
                    Text = (player.PlayerId + 1).ToString(),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = Math.Max(10, iconSize * 0.32),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            marker.Child = content;
            Canvas.SetLeft(marker, point.X - borderSize / 2);
            Canvas.SetTop(marker, point.Y - borderSize / 2);
            TrackCanvas.Children.Add(marker);

            TextBlock label = new()
            {
                Text = $"{player.PositionText}. {player.Character}",
                Foreground = Brushes.White,
                FontSize = labelFontSize,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
                Padding = new Thickness(4, 2, 4, 2),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, point.X + borderSize / 2);
            Canvas.SetTop(label, point.Y - labelFontSize / 2);
            TrackCanvas.Children.Add(label);
        }
    }

    private Point GetPlayerWorldPoint(LivePlayerSnapshot player)
    {
        // Use the same course bounds as the map layers. Do not clamp live physics
        // coordinates: out-of-bounds positions must remain outside the course.
        if (_track?.Kmp is KmpDocument)
        {
            (double minX, double maxX, double minZ, double maxZ) = GetTrackWorldBounds();
            const double margin = 45;
            double width = Math.Max(1, maxX - minX);
            double depth = Math.Max(1, maxZ - minZ);
            double scale = Math.Min((TrackCanvas.Width - 2 * margin) / width,
                                    (TrackCanvas.Height - 2 * margin) / depth);
            double ox = (TrackCanvas.Width - width * scale) / 2;
            double oy = (TrackCanvas.Height - depth * scale) / 2;
            return new Point(ox + (player.WorldX - minX) * scale,
                             TrackCanvas.Height - (oy + (player.WorldZ - minZ) * scale));
        }

        return new Point(TrackCanvas.Width / 2, TrackCanvas.Height / 2);
    }

    private Point GetTrackPoint(float completion)
    {
        List<TrackWaypoint>? points = _track?.Waypoints;
        if (points is null || points.Count == 0) return new Point(TrackCanvas.Width / 2, TrackCanvas.Height / 2);
        if (points.Count == 1) return new Point(points[0].X * TrackCanvas.Width, points[0].Y * TrackCanvas.Height);
        double progress = double.IsFinite(completion) ? completion - Math.Floor(completion) : 0;
        double scaled = progress * points.Count;
        int index = (int)Math.Floor(scaled) % points.Count;
        double t = scaled - Math.Floor(scaled);
        TrackWaypoint a = points[index], b = points[(index + 1) % points.Count];
        return new Point((a.X + (b.X - a.X) * t) * TrackCanvas.Width, (a.Y + (b.Y - a.Y) * t) * TrackCanvas.Height);
    }

    private static Brush EffectBrush(LivePlayerSnapshot p)
    {
        if (p.StarTimer > 0) return Brushes.Gold;
        if (p.MegaTimer > 0) return Brushes.MediumPurple;
        if ((p.BoostTypes & (1 << 3)) != 0) return Brushes.OrangeRed; // unverified bullet candidate
        if (p.ShockTimer > 0) return Brushes.DeepSkyBlue; // small/shock hypothesis
        if (p.CrushTimer > 0) return Brushes.DimGray;
        if (p.RespawnState != 0) return Brushes.White;
        return Brushes.LimeGreen;
    }

    private void Back_Click(object sender, RoutedEventArgs e) { _cancellation?.Cancel(); _mainWindow.ShowMainMenu(); }

    private sealed class TrackDefinition
    {
        public TrackDefinition() { }
        public uint CourseId { get; set; }
        public string DisplayName { get; set; } = "Track Map";
        public string TrackImage { get; set; } = "";
        public string ArchivePath { get; set; } = "";
        public string Summary { get; set; } = "";
        public IReadOnlyList<Tools.KclTriangle>? CollisionTriangles { get; set; }
        public KmpDocument? Kmp { get; set; }
        public LexDocument? Lex { get; set; }
        public BrresModelReader.DecodeResult? CourseModel { get; set; }
        public double CanvasWidth { get; set; } = MapWidth;
        public double CanvasHeight { get; set; } = MapHeight;
        public List<TrackWaypoint> Waypoints { get; set; } = [];
        public static TrackDefinition CreateFallback(uint id) => new() { CourseId = id, DisplayName = $"Course 0x{id:X8}", Waypoints = [new(.5,.12),new(.85,.5),new(.5,.88),new(.15,.5)] };
    }
    public sealed class TrackWaypoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public TrackWaypoint() { }
        public TrackWaypoint(double x, double y) { X = x; Y = y; }
    }
}
