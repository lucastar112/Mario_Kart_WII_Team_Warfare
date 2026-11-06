using DebugManager.Models;
using DebugManager.Services;
using DebugManager.Services.TrackFormats;
using System.IO;
using IOPath = System.IO.Path;
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
    private bool _cameraDragging;
    private const double MapWidth = 1100;
    private const double MapHeight = 700;

    public TrackMapPage(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _cancellation?.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
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
                StatusText.Text = _track?.Summary is { Length: > 0 } summary
                    ? summary
                    : players.Count == 0 ? "Waiting for race data..." : $"{players.Count} slots · LE-CODE slot 0x{courseId:X}";
                HeaderText.Text = _track?.DisplayName ?? "Track Map";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusText.Text = "Waiting for race data · " + ex.Message;
            }
            await Task.Delay(500, token);
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
            return new TrackDefinition
            {
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
        if (show3D && TrackViewport3D.Children.Count == 0) Build3DPrototype();
    }

    private void Build3DPrototype()
    {
        TrackViewport3D.Children.Clear();
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
        GeometryModel3D geometry = new(source, material) { BackMaterial = material, Transform = transform };
        Model3DGroup scene = new();
        scene.Children.Add(geometry);
        scene.Children.Add(new AmbientLight(Color.FromRgb(105, 105, 105)));
        scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.4, -1, -0.6)));
        TrackViewport3D.Children.Add(new ModelVisual3D { Content = scene });

        _cameraDistance = Math.Max(span * 0.001 * 1.8, 8);
        _cameraYaw = -35;
        _cameraPitch = 28;
        Update3DCamera();
        StatusText.Text = model.Summary;
    }

    private void Update3DCamera()
    {
        double yaw = _cameraYaw * Math.PI / 180.0;
        double pitch = Math.Clamp(_cameraPitch, -80, 80) * Math.PI / 180.0;
        double horizontal = _cameraDistance * Math.Cos(pitch);
        Vector3D offset = new(horizontal * Math.Sin(yaw), _cameraDistance * Math.Sin(pitch), horizontal * Math.Cos(yaw));
        Point3D position = new(offset.X, offset.Y, offset.Z);
        TrackViewport3D.Camera = new PerspectiveCamera
        {
            Position = position,
            LookDirection = new Vector3D(-position.X, -position.Y, -position.Z),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 45,
            NearPlaneDistance = 0.01,
            FarPlaneDistance = Math.Max(10000, _cameraDistance * 100)
        };
    }

    private void TrackViewport3D_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        _cameraDragging = true;
        _lastCameraMouse = e.GetPosition(TrackViewport3D);
        TrackViewport3D.CaptureMouse();
    }

    private void TrackViewport3D_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_cameraDragging) return;
        Point current = e.GetPosition(TrackViewport3D);
        _cameraYaw += (current.X - _lastCameraMouse.X) * 0.55;
        _cameraPitch -= (current.Y - _lastCameraMouse.Y) * 0.45;
        _lastCameraMouse = current;
        Update3DCamera();
    }

    private void TrackViewport3D_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _cameraDragging = false;
        TrackViewport3D.ReleaseMouseCapture();
    }

    private void TrackViewport3D_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        _cameraDistance = Math.Clamp(_cameraDistance * (e.Delta > 0 ? 0.88 : 1.14), 1, 50000);
        Update3DCamera();
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
                    HorizontalAlignment = HorizontalAlignment.Center,
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
