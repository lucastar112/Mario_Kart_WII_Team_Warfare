using DebugManager.Models;
using DebugManager.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace DebugManager.Pages;

public partial class PlayerStatusPage : Page
{
    private readonly MainWindow _mainWindow;
    private CancellationTokenSource? _cancellation;
    public ObservableCollection<LivePlayerSnapshot> Players { get; } = [];

    public PlayerStatusPage(MainWindow mainWindow)
    {
        InitializeComponent(); _mainWindow = mainWindow; DataContext = this;
        Loaded += OnLoaded; Unloaded += (_, _) => _cancellation?.Cancel();
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
        await using GameManagerConnection connection = new(); await connection.ConnectAsync(token);
        LiveRaceDataService service = new(connection);
        while (!token.IsCancellationRequested)
        {
            List<LivePlayerSnapshot> players = await service.GetPlayersAsync(token);
            Players.Clear(); foreach (LivePlayerSnapshot p in players) Players.Add(p);
            StatusText.Text = players.Count == 0 ? "Waiting for race data..." : $"{players.Count}/12 slots";
            await Task.Delay(750, token);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) { _cancellation?.Cancel(); _mainWindow.ShowMainMenu(); }
}
