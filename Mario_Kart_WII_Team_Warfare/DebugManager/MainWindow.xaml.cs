using DebugManager.Pages;
using System.Windows;

namespace DebugManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ShowMainMenu();
    }

    public void ShowMainMenu()
    {
        MainFrame.Navigate(new MainMenuPage(this));
    }

    public void ShowRaceData()
    {
        MainFrame.Navigate(new RaceDataPage(this));
    }

    public void ShowTrackMap()
    {
        MainFrame.Navigate(new TrackMapPage(this));
    }

    public void ShowPlayerStatus()
    {
        MainFrame.Navigate(new PlayerStatusPage(this));
    }

    public void ShowItems()
    {
        MainFrame.Navigate(new ItemsPage(this));
    }

    public void ShowLibraryValidation()
    {
        MainFrame.Navigate(new LibraryValidationPage(this));
    }
}