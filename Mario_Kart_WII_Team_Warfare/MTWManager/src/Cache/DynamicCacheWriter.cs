using GameManager.Shared.Client;
using System.Text.Json;

namespace MTW.Cache
{
    public sealed class DynamicCacheWriter
    {
        private readonly string _cacheFile;
        private readonly GameManagerClient _gameManagerClient;

        public DynamicCacheWriter(string cacheFile, GameManagerClient gameManagerClient)
        {
            _cacheFile = cacheFile;
            _gameManagerClient = gameManagerClient;
        }

        public async Task SaveData(List<Dictionary<int, IntPtr>> playerPointerLists, List<string> resourceNames)
        {

            if (playerPointerLists.Count != resourceNames.Count)
                throw new ArgumentException("The number of player pointer lists must match the number of resource names.");

            DynamicCache cache = new() { Version = 1 };

            for (int i = 0; i < resourceNames.Count; i++)
            {
                DynamicResourceCache resource = new();

                foreach (KeyValuePair<int, IntPtr> player in playerPointerLists[i])
                {
                    resource.Players[player.Key] =
                        $"0x{player.Value.ToInt64():X8}";
                }

                cache.Resources[resourceNames[i]] = resource;
            }

            string? directory = Path.GetDirectoryName(_cacheFile);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(
                cache,
                new JsonSerializerOptions { WriteIndented = true }
            );

            await File.WriteAllTextAsync(_cacheFile, json);

            Console.WriteLine($"Dynamic cache saved: {_cacheFile}");

            // Notify GameManager only after the complete file has been written.
            await _gameManagerClient.ReloadDynamicCacheAsync();

            Console.WriteLine("Dynamic cache is ready in GameManager.");

        }
    }

    public sealed class DynamicCache
    {
        public int Version { get; set; }

        public Dictionary<string, DynamicResourceCache> Resources { get; set; } = new();
    }

    public sealed class DynamicResourceCache
    {
        public Dictionary<int, string> Players { get; set; } = new();
    }
}