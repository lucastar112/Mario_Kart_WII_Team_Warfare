using DebugManager.Models;
using DebugManager.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace DebugManager.Pages;

public partial class RaceDataPage : Page, INotifyPropertyChanged
{
    private readonly MainWindow _mainWindow;
    private CancellationTokenSource? _raceDataCancellation;
    public ObservableCollection<RacePlayer> Players { get; } = [];

    private string _statusMessage = "Race data not initiated";
    private string _TeamColorStatus = "#303030";

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
                return;

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string TeamColorStatus
    {
        get => _TeamColorStatus;
        private set
        {
            if (_TeamColorStatus == value)
                return;

            _TeamColorStatus = value;
            OnPropertyChanged();
        }
    }

    public RaceDataPage(MainWindow mainWindow)
    {
        InitializeComponent();

        _mainWindow = mainWindow;

        DataContext = this;

        Loaded += RaceDataPage_Loaded;
        Unloaded += RaceDataPage_Unloaded;
    }

    private async void RaceDataPage_Loaded(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("[RaceDataPage] Loaded");

        RaceStatusText.Visibility = Visibility.Visible;
        PlayerScrollViewer.Visibility = Visibility.Collapsed;

        Players.Clear();

        _raceDataCancellation?.Cancel();
        _raceDataCancellation?.Dispose();

        _raceDataCancellation = new CancellationTokenSource();

        try
        {
            await MonitorRaceDataAsync(_raceDataCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[RaceDataPage] Loading cancelled.");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[RaceDataPage] Fatal error: {exception.Message}");

            StatusMessage = "Failed to load race data.";
        }
    }

    private void RaceDataPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("[RaceDataPage] Unloaded");
        _raceDataCancellation?.Cancel();
    }

    private async Task MonitorRaceDataAsync(CancellationToken cancellationToken)
    {
        await using GameManagerConnection gameManager = new();
        Console.WriteLine("[RaceDataPage] Connecting...");
        await gameManager.ConnectAsync(cancellationToken);
        Console.WriteLine("[RaceDataPage] Connected.");
        RaceDataService raceDataService = new(gameManager);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Console.WriteLine("[RaceDataPage] Checking race data...");

                List<RacePlayer> players = await raceDataService.GetAllPlayersAsync(cancellationToken);

                if (players.Count > 0)
                {
                    Console.WriteLine($"[RaceDataPage] Received {players.Count} players.");
                    Players.Clear();

                    foreach (RacePlayer player in players)
                        Players.Add(player);
                        

                    RaceStatusText.Visibility = Visibility.Collapsed;
                    PlayerScrollViewer.Visibility = Visibility.Visible;
                    StatusMessage = string.Empty;
                }
                else
                {
                    // Race data is currently unavailable.
                    Players.Clear();
                    RaceStatusText.Visibility = Visibility.Visible;
                    PlayerScrollViewer.Visibility = Visibility.Collapsed;
                    StatusMessage = "Waiting for race data...";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[RaceDataPage] Race data unavailable: {exception.Message}");
                StatusMessage = "Waiting for race data...";
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        Console.WriteLine("[RaceDataPage] Race data monitor stopped.");
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _raceDataCancellation?.Cancel();
        _mainWindow.ShowMainMenu();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}