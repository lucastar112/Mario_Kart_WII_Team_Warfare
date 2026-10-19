namespace DebugManager.Models;

/// <summary>
/// Structure for details for a given player
/// </summary>
/// <param name="Id"></param>
/// <param name="Name"></param>
/// <param name="ImageFile"></param>
public sealed record VehicleDefinition(
    byte Id,
    string Name,
    string ImageFile
);