namespace DebugManager.Models;

/// <summary>
/// Structure for details for a given player
/// </summary>
/// <param name="Id"></param>
/// <param name="Code"></param>
/// <param name="Name"></param>
/// <param name="ImageFile"></param>
public sealed record CharacterDefinition(
    byte Id,
    string Code,
    string Name,
    string ImageFile
);