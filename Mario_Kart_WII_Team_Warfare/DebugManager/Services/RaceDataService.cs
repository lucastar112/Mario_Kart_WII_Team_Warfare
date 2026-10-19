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

            // Gamestate (0 = Not Ready, 1 = Loading, 2 = Ready)
            JsonElement gameStateValue = await _gameManager.GetAsync<JsonElement>("GameState", cancellationToken: cancellationToken);

            if (gameStateValue.ValueKind != JsonValueKind.String)
            {
                Console.WriteLine($"[RaceData] GameState has invalid type: {gameStateValue.ValueKind}");
                return [];
            }

            string? gameStateBase64 = gameStateValue.GetString();

            if (string.IsNullOrEmpty(gameStateBase64))
            {
                Console.WriteLine("[RaceData] GameState is empty.");
                return [];
            }

            byte[] gameState;

            try
            {
                gameState = Convert.FromBase64String(gameStateBase64);
            }
            catch (FormatException)
            {
                Console.WriteLine("[RaceData] GameState is not valid Base64.");
                return [];
            }

            if (gameState.Length == 0)
            {
                Console.WriteLine("[RaceData] GameState contains no data.");
                return [];
            }

            byte state = gameState[0];

            Console.WriteLine($"[RaceData] GameState: {state}");

            if (state != 2)
            {
                Console.WriteLine("[RaceData] Race data is not ready.");
                return [];
            }

            // ---------------------------------------------------------
            // Players
            // ---------------------------------------------------------

            Console.WriteLine("[RaceData] Requesting PlayersId...");

            // See (Mod)CharacterDatabase for the mapping of IDs to definitions
            JsonElement playersValue = await _gameManager.GetPlayersIdAsync(cancellationToken: cancellationToken);

            if (playersValue.ValueKind != JsonValueKind.String)
            {
                Console.WriteLine($"[RaceData] PlayersId has invalid type: {playersValue.ValueKind}");
                return [];
            }

            string? playersBase64 = playersValue.GetString();

            if (string.IsNullOrEmpty(playersBase64))
            {
                Console.WriteLine("[RaceData] PlayersId is empty.");
                return [];
            }

            byte[] characterIds;

            try
            {
                characterIds = Convert.FromBase64String(playersBase64);
            }
            catch (FormatException)
            {
                Console.WriteLine("[RaceData] PlayersId is not valid Base64.");
                return [];
            }

            Console.WriteLine($"[RaceData] PlayersId received: {playersBase64}");



            Console.WriteLine("[RaceData] Requesting TeamsId...");

            // Methode liefert List<Task<JsonElement>>
            List<Task<JsonElement>> teamsTasks = _gameManager.GetAllPlayersTeamIdAsync(12, cancellationToken: cancellationToken);

            // Wait for task to complet
            JsonElement[] teamsResults;
            try
            {
                teamsResults = await Task.WhenAll(teamsTasks);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[RaceData] Failed to get TeamsId tasks: {exception.Message}");
                return new List<RacePlayer>();
            }

            // Combine data to Byte arrays
            List<byte> teamIdsList = new List<byte>();

            foreach (var teamId in teamsResults)
            {
                if (teamId.ValueKind != JsonValueKind.String)
                {
                    Console.WriteLine($"[RaceData] One of TeamsId elements has invalid type: {teamId.ValueKind}");
                    continue;
                }

                string? base64 = teamId.GetString();
                if (string.IsNullOrEmpty(base64))
                {
                    Console.WriteLine("[RaceData] One of TeamsId elements is empty.");
                    continue;
                }

                try
                {
                    byte[] part = Convert.FromBase64String(base64);
                    teamIdsList.AddRange(part);
                }
                catch (FormatException)
                {
                    Console.WriteLine("[RaceData] One of TeamsId elements is not valid Base64.");
                    continue;
                }
            }

            byte[] teamIds = teamIdsList.ToArray();
            Console.WriteLine($"[RaceData] TeamsId received: length={teamIds.Length}");

            // ---------------------------------------------------------
            // Vehicles
            // ---------------------------------------------------------

            Console.WriteLine("[RaceData] Requesting VehiclesId...");

            // See VehicleDatabase for the mapping of IDs to definitions
            JsonElement vehiclesValue = await _gameManager.GetVehiclesIdAsync(cancellationToken: cancellationToken);

            if (vehiclesValue.ValueKind != JsonValueKind.String)
            {
                Console.WriteLine($"[RaceData] VehiclesId has invalid type: {vehiclesValue.ValueKind}");
                return [];
            }

            string? vehiclesBase64 = vehiclesValue.GetString();

            if (string.IsNullOrEmpty(vehiclesBase64))
            {
                Console.WriteLine("[RaceData] VehiclesId is empty.");
                return [];
            }

            byte[] vehicleIds;

            try
            {
                vehicleIds = Convert.FromBase64String(vehiclesBase64);
            }
            catch (FormatException)
            {
                Console.WriteLine("[RaceData] VehiclesId is not valid Base64.");
                return [];
            }

            Console.WriteLine($"[RaceData] VehiclesId received: {vehiclesBase64}");

            // ---------------------------------------------------------
            // Player count
            // ---------------------------------------------------------

            int playerCount = Math.Min(characterIds.Length, vehicleIds.Length);

            Console.WriteLine($"[RaceData] Character count: {characterIds.Length}");

            Console.WriteLine($"[RaceData] Vehicle count: {vehicleIds.Length}");

            Console.WriteLine($"[RaceData] Player count: {playerCount}");

            if (playerCount == 0)
            {
                return [];
            }

            // ---------------------------------------------------------
            // Create players
            // ---------------------------------------------------------

            List<RacePlayer> players = [];

            for (int playerId = 0; playerId < playerCount; playerId++)
            {
                byte characterId = characterIds[playerId];
                byte vehicleId = vehicleIds[playerId];

                Console.WriteLine($"[RaceData] Creating player {playerId + 1}: Character={characterId:X2}, Vehicle={vehicleId:X2}");

                // -----------------------------------------------------
                // Character database
                // -----------------------------------------------------

                if (!ModCharacterDatabase.All.TryGetValue(characterId, out CharacterDefinition? character))
                {
                    Console.WriteLine($"[RaceData] Unknown character ID: {characterId:X2}");
                    continue;
                }

                // -----------------------------------------------------
                // Vehicle database
                // -----------------------------------------------------

                if (!VehicleDatabase.All.TryGetValue(vehicleId, out VehicleDefinition? vehicle))
                {
                    Console.WriteLine($"[RaceData] Unknown vehicle ID: {vehicleId:X2}");
                    continue;
                }

                Console.WriteLine($"[RaceData] Character: {character.Name}");

                Console.WriteLine($"[RaceData] Vehicle: {vehicle.Name}");

                // -----------------------------------------------------
                // Images
                // -----------------------------------------------------

                BitmapImage? characterImage = LoadImage(Path.Combine("Assets", "Characters", character.ImageFile));
                BitmapImage? vehicleImage = LoadImage(Path.Combine("Assets", "Vehicles", vehicle.ImageFile));

                // -----------------------------------------------------
                // RacePlayer
                // -----------------------------------------------------

                players.Add(new RacePlayer
                {
                    PlayerId = playerId,

                    CharacterId = character.Id,
                    VehicleId = vehicle.Id,

                    Character = character.Name,
                    Vehicle = vehicle.Name,

                    CharacterImage = characterImage,
                    VehicleImage = vehicleImage
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