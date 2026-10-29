using DebugManager.Data;
using DebugManager.Models;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.IO;

namespace DebugManager.Services;

public sealed class RaceDataService
{
    private readonly GameManagerConnection _gameManager;

    public RaceDataService(GameManagerConnection gameManager)
    {
        _gameManager = gameManager;
    }

    public async Task<List<RacePlayer>> GetAllPlayersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Console.WriteLine("[RaceData] Starting GetAllPlayersAsync");

            // ---------------------------------------------------------
            // Game state
            // ---------------------------------------------------------

            Console.WriteLine("[RaceData] Checking race data state...");

            JsonElement gameStateValue =
                await _gameManager.GetAsync<JsonElement>(
                    "GameState",
                    cancellationToken: cancellationToken);

            byte state;

            if (gameStateValue.ValueKind == JsonValueKind.Number &&
                gameStateValue.TryGetByte(out byte numericState))
            {
                state = numericState;
            }
            else
            {
                Console.WriteLine(
                    $"[RaceData] Invalid GameState value: {gameStateValue}");

                return [];
            }

            Console.WriteLine($"[RaceData] GameState: {state}");

            if (state != 2)
            {
                Console.WriteLine(
                    "[RaceData] Race data is not ready. Waiting for cache...");

                return [];
            }

            // ---------------------------------------------------------
            // Players / Vehicles / Teams
            // ---------------------------------------------------------

            const int maxPlayers = 12;

            List<RacePlayer> players = [];

            for (int playerSlot = 0; playerSlot < maxPlayers; playerSlot++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                JsonElement characterValue;
                JsonElement vehicleValue;
                JsonElement teamValue;

                try
                {
                    characterValue = await _gameManager.GetPlayersIdAsync(playerSlot, cancellationToken);

                    vehicleValue = await _gameManager.GetVehiclesIdAsync(playerSlot, cancellationToken);

                    teamValue = await _gameManager.GetTeamIdAsync(playerSlot, cancellationToken);
                }
                catch (Exception exception)
                    when (exception is not OperationCanceledException)
                {
                    Console.WriteLine($"[RaceData] Player slot {playerSlot} is unavailable: " + exception.Message);
                    continue;
                }

                if (!TryGetUInt32(characterValue, out uint characterId) ||
                    !TryGetUInt32(vehicleValue, out uint vehicleId) ||
                    !TryGetUInt32(teamValue, out uint teamId))
                {
                    Console.WriteLine($"[RaceData] Invalid or unavailable data for slot {playerSlot}.");
                    continue;
                }

                if (characterId > byte.MaxValue || vehicleId > byte.MaxValue)
                {
                    Console.WriteLine($"[RaceData] ID out of range for slot {playerSlot}.");
                    continue;
                }

                if (!ModCharacterDatabase.All.TryGetValue((byte)characterId, out CharacterDefinition? character))
                {
                    Console.WriteLine($"[RaceData] Unknown character ID {characterId} for slot {playerSlot}.");
                    continue;
                }

                if (!VehicleDatabase.All.TryGetValue((byte)vehicleId, out VehicleDefinition? vehicle))
                {
                    Console.WriteLine($"[RaceData] Unknown vehicle ID {vehicleId} for slot {playerSlot}.");
                    continue;
                }

                players.Add(new RacePlayer
                {
                    PlayerId = playerSlot,
                    CharacterId = character.Id,
                    VehicleId = vehicle.Id,
                    Character = character.Name,
                    Vehicle = vehicle.Name,
                    TeamId = teamId,
                    CharacterImage = LoadImage(
                        Path.Combine("Assets", "Characters", character.ImageFile)),
                    VehicleImage = LoadImage(
                        Path.Combine("Assets", "Vehicles", vehicle.ImageFile))
                });
            }

            Console.WriteLine($"[RaceData] Created {players.Count} players.");
            return players;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[RaceData] Race data unavailable: {exception.Message}");
            return [];
        }
    }

    private static bool TryGetUInt32(JsonElement value, out uint result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out result))
            return true;
        

        result = 0;
        return false;
    }

    private static BitmapImage? LoadImage(string relativePath)
    {
        string fullPath = Path.Combine(AppContext.BaseDirectory, relativePath);

        Console.WriteLine($"[RaceData] Loading image: {fullPath}");

        if (!File.Exists(fullPath))
        {
            Console.WriteLine($"[RaceData] Image file does not exist: {fullPath}");
            return null;
        }

        try
        {
            BitmapImage image = new();

            image.BeginInit();
            image.UriSource = new Uri(fullPath, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            Console.WriteLine($"[RaceData] Image loaded successfully: {fullPath}");
            return image;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[RaceData] Failed to load image '{fullPath}': " + exception.Message);
            return null;
        }
    }
}