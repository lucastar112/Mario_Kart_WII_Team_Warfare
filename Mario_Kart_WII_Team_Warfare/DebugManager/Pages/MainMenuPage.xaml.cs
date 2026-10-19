using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DebugManager.Pages;

public partial class MainMenuPage : Page
{
    private readonly MainWindow _mainWindow;

    public MainMenuPage(MainWindow mainWindow)
    {
        InitializeComponent();

        _mainWindow = mainWindow;
    }

    private void RaceData_Click(object sender, MouseButtonEventArgs e)
    {
        _mainWindow.ShowRaceData();
    }
}