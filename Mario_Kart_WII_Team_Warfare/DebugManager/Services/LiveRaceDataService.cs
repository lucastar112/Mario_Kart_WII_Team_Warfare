using DebugManager.Data;
using DebugManager.Models;
using GameManager.Shared.Ipc;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace DebugManager.Services;

public sealed class LiveRaceDataService
{
    private const int PlayerSlotCount = 12;
    private static readonly ConcurrentDictionary<string, BitmapImage> ImageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly GameManagerConnection _connection;

    public LiveRaceDataService(GameManagerConnection connection) => _connection = connection;

    public async Task<List<LivePlayerSnapshot>> GetPlayersAsync(CancellationToken token = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        JsonElement state = await _connection.GetAsync<JsonElement>("GameState", cancellationToken: token);
        if (ReadNumber(state) != 2) return [];

        // One IPC round trip for every resource of every slot (12 x 18 reads).
        List<IpcResourceRequest> requests = new(PlayerSlotCount * Resources.Length);
        for (int slot = 0; slot < PlayerSlotCount; slot++)
            foreach (string resource in Resources)
                requests.Add(new IpcResourceRequest { Resource = resource, PlayerId = slot });

        IReadOnlyList<IpcResourceResult> results = await _connection.GetManyAsync(requests, token);

        List<LivePlayerSnapshot> players = [];
        for (int slot = 0; slot < PlayerSlotCount; slot++)
        {
            LivePlayerSnapshot? player = BuildPlayer(slot, results, slot * Resources.Length);
            if (player is not null) players.Add(player);
        }

        Console.WriteLine($"[LiveRaceData] Read {players.Count}/{PlayerSlotCount} slots in {stopwatch.ElapsedMilliseconds} ms.");
        return players;
    }

    // Order matters: BuildPlayer indexes into this list.
    private static readonly string[] Resources =
    [
        "CharacterId", "RacePosition", "RaceCompletion", "CurrentLapCandidate",
        "MaxLap", "VehicleSpeed", "DriftState", "MtCharge",
        "SmtCharge", "StarTimer", "MegaTimer", "ShockTimer",
        "CrushTimer", "RespawnState", "BoostTypes", "PlayerType",
        "PreRespawnTimer", "SolidOutOfBoundsTimer", "PhysicsPositionX", "PhysicsPositionY", "PhysicsPositionZ"
    ];

    private LivePlayerSnapshot? BuildPlayer(int slot, IReadOnlyList<IpcResourceResult> results, int offset)
    {
        if (results.Count < offset + Resources.Length) return null;

        JsonElement[] values = new JsonElement[Resources.Length];
        for (int i = 0; i < Resources.Length; i++)
        {
            IpcResourceResult result = results[offset + i];
            if (!result.Success || result.Value is not JsonElement element)
            {
                Console.WriteLine($"[LiveRaceData] Slot {slot} unavailable: {result.Error ?? Resources[i]}");
                return null;
            }
            values[i] = element;
        }

        uint charId = (uint)Math.Max(0, ReadNumber(values[0]));
        if (!ModCharacterDatabase.All.TryGetValue((byte)charId, out CharacterDefinition? character))
            CharacterDatabase.All.TryGetValue((byte)charId, out character);

        return new LivePlayerSnapshot
        {
            PlayerId = slot, CharacterId = charId,
            Character = character?.Name ?? $"Character {charId:X2}",
            CharacterImage = character is null ? null : LoadImage(character.ImageFile),
            Position = (int)ReadNumber(values[1]), Completion = ReadFloat(values[2]),
            CurrentLap = (int)ReadNumber(values[3]), MaxLap = (int)ReadNumber(values[4]),
            Speed = ReadFloat(values[5]), DriftState = (int)ReadNumber(values[6]),
            MtCharge = (int)ReadNumber(values[7]), SmtCharge = (int)ReadNumber(values[8]),
            StarTimer = (int)ReadNumber(values[9]), MegaTimer = (int)ReadNumber(values[10]),
            ShockTimer = (int)ReadNumber(values[11]), CrushTimer = (int)ReadNumber(values[12]),
            RespawnState = (int)ReadNumber(values[13]), BoostTypes = (ushort)ReadNumber(values[14]),
            PlayerType = (int)ReadNumber(values[15]), PreRespawnTimer = (int)ReadNumber(values[16]),
            SolidOutOfBoundsTimer = (int)ReadNumber(values[17]),
            WorldX = ReadFloat(values[18]), WorldY = ReadFloat(values[19]), WorldZ = ReadFloat(values[20])
        };
    }

    private static long ReadNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n)) return n;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return 0;
    }

    private static float ReadFloat(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float f)) return f;
        if (value.ValueKind == JsonValueKind.String && float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
        return 0;
    }

    private static BitmapImage? LoadImage(string imageFile)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Characters", imageFile);
        if (!File.Exists(path)) return null;
        return ImageCache.GetOrAdd(path, static file =>
        {
            BitmapImage image = new();
            image.BeginInit(); image.UriSource = new Uri(file); image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit(); image.Freeze();
            return image;
        });
    }
}
