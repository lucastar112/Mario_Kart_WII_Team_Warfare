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

        private readonly IntPtr _stateAddress;
        private bool _cacheBuildHandled;
        private static readonly IntPtr CacheStateAddress = new IntPtr(0x80000CF6);

        public CacheBuildController(DolphinConnection dolphin, PlayerPointerResolver resolver, DynamicCacheWriter cacheWriter)
        {
            _dolphin = dolphin;
            _resolver = resolver;
            _cacheWriter = cacheWriter;
        }

        public void Run(CancellationToken cancellationToken)
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
                    BuildCache();
                }
                else if (state == 0)
                    _cacheBuildHandled = false;

                Thread.Sleep(50);
            }
        }

        private void BuildCache()
        {
            Console.WriteLine("Resolving player pointers...");
            Dictionary<int, IntPtr> players = _resolver.ResolveAllPlayers();

            if (players.Count != 12)
            {
                Console.WriteLine($"Cache build failed. Resolved {players.Count}/12 players.");
                return;
            }

            try
            {
                _cacheWriter.SavePlayerSub10(players);

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