using System.Text.Json;

namespace MTW.Cache
{
    public sealed class DynamicCacheWriter
    {
        private readonly string _cacheFile;

        public DynamicCacheWriter(string cacheFile)
        {
            _cacheFile = cacheFile;
        }

        public void SavePlayerSub10(Dictionary<int, IntPtr> players)
        {
            DynamicCache cache = new() {Version = 1};

            DynamicResourceCache resource = new();

            foreach (KeyValuePair<int, IntPtr> player in players)
                resource.Players[player.Key] = $"0x{player.Value.ToInt64():X8}";

            cache.Resources["playerSub10"] = resource;

            string? directory =Path.GetDirectoryName(_cacheFile);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions{WriteIndented = true});

            File.WriteAllText(_cacheFile, json);

            Console.WriteLine($"Dynamic cache saved: {_cacheFile}");
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