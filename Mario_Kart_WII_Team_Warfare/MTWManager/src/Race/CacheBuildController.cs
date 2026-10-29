using MTW.Cache;
using MTW.Memory;
using GameManager.Dolphin;

namespace MTW.Race
{
    public sealed class CacheBuildController
    {
        private readonly DolphinConnection _dolphin;
        private readonly PlayerPointerResolver _resolver;
        private readonly DynamicCacheWriter _cacheWriter;
        private readonly List<string> _resourceNames = new List<string> { "playerSub10", "raceData" };
        private bool _cacheBuildHandled;
        private static readonly IntPtr CacheStateAddress = new IntPtr(0x80000CF6);

        public CacheBuildController(DolphinConnection dolphin, PlayerPointerResolver resolver, DynamicCacheWriter cacheWriter)
        {
            _dolphin = dolphin;
            _resolver = resolver;
            _cacheWriter = cacheWriter;
        }

        public async Task Run(CancellationToken cancellationToken)
        {
            Console.WriteLine("MTW cache controller started.");

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!TryReadState(out byte state))
                {
                    Console.WriteLine("Failed to read cache state.");
                    Thread.Sleep(100);
                    continue;
                }

                if (state == 1 && !_cacheBuildHandled)
                {
                    _cacheBuildHandled = true;
                    Console.WriteLine("Cache build requested.");
                    await BuildCache();
                }
                else if (state == 0)
                    _cacheBuildHandled = false;

                Thread.Sleep(50);
            }
        }

        private async Task BuildCache()
        {
            Console.WriteLine("Resolving player pointers...");
            Dictionary<int, IntPtr> playerSub10s = _resolver.ResolveAllPlayersSub10();
            Dictionary<int, IntPtr> raceDataPlayers = _resolver.ResolveAllPlayersRaceData();
            List<Dictionary<int, IntPtr>> playerPointerLists = new List<Dictionary<int, IntPtr>> { playerSub10s, raceDataPlayers };

            if (playerSub10s.Count != 12)
            {
                Console.WriteLine($"Cache build failed. Resolved {playerSub10s.Count}/12 players.");
                return;
            }

            try
            {
                await _cacheWriter.SaveData(playerPointerLists, _resourceNames);

                if (!TryWriteState(2))
                {
                    Console.WriteLine("Cache was saved, but failed to write state 2.");
                    return;
                }

                Console.WriteLine("Cache build completed. State = 2.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cache build failed: {ex}");
            }
        }

        private bool TryReadState(out byte state)
        {
            state = 0;
            return _dolphin.TryReadByte(CacheStateAddress, out state);
        }

        private bool TryWriteState(byte state)
        {
            return _dolphin.TryWriteByte(CacheStateAddress, state);
        }
    }
}