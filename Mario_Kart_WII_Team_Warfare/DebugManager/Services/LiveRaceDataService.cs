using DebugManager.Data;
using DebugManager.Models;
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
    private const int MaxConcurrentPlayerReads = 4;
    private static readonly ConcurrentDictionary<string, BitmapImage> ImageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly GameManagerConnection _connection;

    public LiveRaceDataService(GameManagerConnection connection) => _connection = connection;

    public async Task<List<LivePlayerSnapshot>> GetPlayersAsync(CancellationToken token = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        JsonElement state = await _connection.GetAsync<JsonElement>("GameState", cancellationToken: token);
        if (ReadNumber(state) != 2) return [];

        // Each slot is fetched independently. Four slots may be in flight at once;
        // each slot's resource reads are concurrent over the multiplexed named pipe.
        using SemaphoreSlim slots = new(MaxConcurrentPlayerReads);
        Task<LivePlayerSnapshot?>[] reads = Enumerable.Range(0, PlayerSlotCount)
            .Select(async slot =>
            {
                await slots.WaitAsync(token);
                try { return await ReadPlayerAsync(slot, token); }
                finally { slots.Release(); }
            })
            .ToArray();

        LivePlayerSnapshot?[] results = await Task.WhenAll(reads);
        List<LivePlayerSnapshot> players = results.Where(player => player is not null).Cast<LivePlayerSnapshot>().ToList();
        Console.WriteLine($"[LiveRaceData] Read {players.Count}/{PlayerSlotCount} slots in {stopwatch.ElapsedMilliseconds} ms.");
        return players;
    }

    private async Task<LivePlayerSnapshot?> ReadPlayerAsync(int slot, CancellationToken token)
    {
        try
        {
            JsonElement[] values = await Task.WhenAll(
                Read("CharacterId", slot, token), Read("RacePosition", slot, token),
                Read("RaceCompletion", slot, token), Read("CurrentLapCandidate", slot, token),
                Read("MaxLap", slot, token), Read("VehicleSpeed", slot, token),
                Read("DriftState", slot, token), Read("MtCharge", slot, token),
                Read("SmtCharge", slot, token), Read("StarTimer", slot, token),
                Read("MegaTimer", slot, token), Read("ShockTimer", slot, token),
                Read("CrushTimer", slot, token), Read("RespawnState", slot, token),
                Read("BoostTypes", slot, token), Read("PlayerType", slot, token),
                Read("PreRespawnTimer", slot, token), Read("SolidOutOfBoundsTimer", slot, token));

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
                SolidOutOfBoundsTimer = (int)ReadNumber(values[17])
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[LiveRaceData] Slot {slot} unavailable: {ex.Message}");
            return null;
        }
    }

    private Task<JsonElement> Read(string name, int player, CancellationToken token) =>
        _connection.GetAsync<JsonElement>(name, player, token);

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
