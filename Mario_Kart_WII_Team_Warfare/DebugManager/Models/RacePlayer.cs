using System.Windows.Media;

namespace DebugManager.Models;

/// <summary>
/// Holdes data for a given player to show in RaceDataPage
/// </summary>
public sealed class RacePlayer
{
    public int PlayerId { get; init; }
    public byte CharacterId { get; init; }
    public byte VehicleId { get; init; }
    public string Character { get; init; } = string.Empty;
    public string Vehicle { get; init; } = string.Empty;
    public string Name => $"Player {PlayerId + 1}";
    public string MaxSpeedText => "Max Speed: --";
    public string CurrentSpeedText => "Current Speed: --";
    public string TeamColor => "#303030";
    public ImageSource? CharacterImage { get; init; }
    public ImageSource? VehicleImage { get; init; }
}