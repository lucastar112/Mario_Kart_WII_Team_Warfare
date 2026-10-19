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
}