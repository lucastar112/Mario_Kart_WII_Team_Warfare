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
using System.Windows.Shapes;

namespace DebugManager.Pages;

public partial class TrackMapPage : Page
{
    private readonly MainWindow _mainWindow;
    private CancellationTokenSource? _cancellation;
    private TrackDefinition? _track;
    private bool _baseDrawn;
    private int _baseChildCount;
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
            int checkpointCount = archive.Kmp.FindSection("CKPT")?.EntryCount ?? 0;
            int objectCount = archive.Kmp.FindSection("GOBJ")?.EntryCount ?? 0;
            int cpuRouteCount = archive.Kmp.FindSection("ENPT")?.EntryCount ?? 0;
            int itemRouteCount = archive.Kmp.FindSection("ITPT")?.EntryCount ?? 0;
            return new TrackDefinition
            {
                CourseId = courseId,
                DisplayName = $"Track {archive.TrackId}",
                ArchivePath = archive.SourcePath,
                Kmp = archive.Kmp,
                Lex = archive.Lex,
                Summary = $"KMP: {archive.Kmp.Sections.Count} sections · {checkpointCount} checkpoints · {objectCount} objects · {cpuRouteCount} CPU points · {itemRouteCount} item points · LEX: {archive.Lex?.Sections.Count ?? 0} sections",
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
    }

    private void DrawPlayers(IReadOnlyList<LivePlayerSnapshot> players)
    {
        if (!_baseDrawn) DrawTrackBase();
        // Keep the map and route as the first two canvas children; replace only live markers.
        while (TrackCanvas.Children.Count > _baseChildCount) TrackCanvas.Children.RemoveAt(TrackCanvas.Children.Count - 1);
        foreach (LivePlayerSnapshot player in players)
        {
            Point point = GetTrackPoint(player.Completion);
            Border marker = new() { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(3), BorderBrush = EffectBrush(player), Background = new SolidColorBrush(Color.FromArgb(225, 25, 25, 25)), ToolTip = $"{player.Character} · P{player.PlayerId + 1} · Place {player.PositionText} · {player.EffectText}" };
            Grid content = new();
            if (player.CharacterImage is not null)
                content.Children.Add(new Image { Source = player.CharacterImage, Width = 32, Height = 32, Stretch = Stretch.Uniform, Margin = new Thickness(2) });
            else
                content.Children.Add(new TextBlock { Text = (player.PlayerId + 1).ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            marker.Child = content;
            Canvas.SetLeft(marker, point.X - marker.Width / 2); Canvas.SetTop(marker, point.Y - marker.Height / 2);
            TrackCanvas.Children.Add(marker);
            TextBlock label = new() { Text = $"{player.PositionText}. {player.Character}", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold, Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), Padding = new Thickness(4, 2, 4, 2), IsHitTestVisible = false };
            Canvas.SetLeft(label, point.X + 20); Canvas.SetTop(label, point.Y - 9); TrackCanvas.Children.Add(label);
        }
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
