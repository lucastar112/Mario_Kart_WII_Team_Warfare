using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;


namespace DebugManager.Models;

/// <summary>
/// Holds data for a given player to display on RaceDataPage.
/// </summary>
public sealed class RacePlayer : INotifyPropertyChanged
{
    public int PlayerId { get; init; }
    public byte CharacterId { get; init; }
    public byte VehicleId { get; init; }

    public string Character { get; init; } = string.Empty;
    public string Vehicle { get; init; } = string.Empty;

    public string Name => $"Player {PlayerId + 1}";
    public string MaxSpeedText => "Max Speed: --";
    public string CurrentSpeedText => "Current Speed: --";

    private uint _teamId = uint.MaxValue;

    public uint TeamId
    {
        get => _teamId;
        set
        {
            if (_teamId == value)
                return;

            _teamId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TeamColor));
        }
    }

    public string TeamColor => TeamId switch
    {
        0x00000000 => "#C62828", // Red team
        0x00000001 => "#1565C0", // Blue team
        _ => "#303030"           // Unknown team
    };

    public ImageSource? CharacterImage { get; init; }
    public ImageSource? VehicleImage { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}