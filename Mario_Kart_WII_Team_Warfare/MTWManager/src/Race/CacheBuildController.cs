using MTW.Cache;
using System.Diagnostics;
using MTW.Memory;
using GameManager.Dolphin;

namespace MTW.Race
{
    public sealed class CacheBuildController
    {
        private readonly DolphinConnection _dolphin;
        private readonly PlayerPointerResolver _resolver;
        private readonly DynamicCacheWriter _cacheWriter;
        private readonly List<string> _resourceNames = new()
        {
            "playerHolder",
            "player",
            "playerPointers",
            "playerSub",
            "playerSub10",
            "playerSub14",
            "playerSub18",
            "playerSub1c",
            "playerParams",
            "playerGraphics",
            "playerPhysicsHolder",
            "playerPhysics",
            "playerTrick",
            "playerZipper",
            "playerSub10BikeExtension",
            "raceData",
            "raceDataScenario",
            "raceDataSettings",
            "raceInfo",
            "raceInfoPlayer",
            "itemSlotData",
            "itemHandler",
            "itemPacket",
            "itemHandlerTime"
        };
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

            // This child owns its own DolphinConnection, so it must discover the
            // emulated RAM mapping itself (GameManager's connection is separate).
            while (!_dolphin.IsMemoryMapInitialized && !cancellationToken.IsCancellationRequested)
            {
                if (_dolphin.InitializeMemoryMap())
                    break;

                await Task.Delay(500, cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!TryReadState(out byte state))
                {
                    Console.WriteLine("Failed to read cache state.");
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                if (state == 1 && !_cacheBuildHandled)
                {
                    _cacheBuildHandled = true;
                    Console.WriteLine("Cache build requested.");
                    await BuildCache(cancellationToken);
                }
                else if (state == 0)
                    _cacheBuildHandled = false;

                await Task.Delay(50, cancellationToken);
            }
        }

        private async Task BuildCache(CancellationToken cancellationToken)
        {
            Stopwatch total = Stopwatch.StartNew();
            _dolphin.ResetReadStatistics();
            Console.WriteLine("[CacheProfile] Build started.");

            Stopwatch resolveTimer = Stopwatch.StartNew();
            var slotResults = new Dictionary<string, IntPtr>[ConfirmedPointerChains.PlayerSlotCount];
            var slotTimes = new long[ConfirmedPointerChains.PlayerSlotCount];
            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4)
            };

            Parallel.For(0, ConfirmedPointerChains.PlayerSlotCount, options, playerId =>
            {
                Stopwatch slotTimer = Stopwatch.StartNew();
                slotResults[playerId] = _resolver.ResolveAllResourcesForPlayer(playerId);
                slotTimes[playerId] = slotTimer.ElapsedMilliseconds;
            });
            resolveTimer.Stop();
            Console.WriteLine($"[CacheProfile] Player pointer resolution: {resolveTimer.ElapsedMilliseconds} ms (parallel, max workers={options.MaxDegreeOfParallelism}).");
            for (int i = 0; i < slotTimes.Length; i++)
                Console.WriteLine($"[CacheProfile]   Slot {i:D2}: {slotTimes[i]} ms, {slotResults[i].Count}/{_resourceNames.Count} resources.");

            Stopwatch aggregateTimer = Stopwatch.StartNew();
            List<Dictionary<int, IntPtr>> playerPointerLists = new(_resourceNames.Count);
            for (int resourceIndex = 0; resourceIndex < _resourceNames.Count; resourceIndex++)
            {
                string resourceName = _resourceNames[resourceIndex];
                Dictionary<int, IntPtr> resourcePlayers = new();
                for (int playerId = 0; playerId < slotResults.Length; playerId++)
                {
                    if (slotResults[playerId].TryGetValue(resourceName, out IntPtr address))
                        resourcePlayers[playerId] = address;
                }
                playerPointerLists.Add(resourcePlayers);

                if (resourcePlayers.Count != ConfirmedPointerChains.PlayerSlotCount)
                {
                    Console.WriteLine($"Cache build failed for '{resourceName}': resolved {resourcePlayers.Count}/{ConfirmedPointerChains.PlayerSlotCount} slots.");
                    return;
                }
            }
            aggregateTimer.Stop();
            Console.WriteLine($"[CacheProfile] Resource aggregation/validation: {aggregateTimer.ElapsedMilliseconds} ms.");

            try
            {
                Stopwatch saveTimer = Stopwatch.StartNew();
                await _cacheWriter.SaveData(playerPointerLists, _resourceNames);
                saveTimer.Stop();
                Console.WriteLine($"[CacheProfile] Cache serialization, disk write and GameManager reload: {saveTimer.ElapsedMilliseconds} ms.");

                Stopwatch stateTimer = Stopwatch.StartNew();
                if (!TryWriteState(2))
                {
                    Console.WriteLine("Cache was saved, but failed to write state 2.");
                    return;
                }
                stateTimer.Stop();
                var reads = _dolphin.GetReadStatistics();
                Console.WriteLine($"[CacheProfile] State update: {stateTimer.ElapsedMilliseconds} ms.");
                Console.WriteLine($"[CacheProfile] Dolphin reads: {reads.Calls} calls, {reads.PointerReads} pointer reads, {reads.Bytes} bytes, {reads.Failures} failures, {_dolphin.TotalReadTime.TotalMilliseconds:F1} ms inside TryRead (summed across threads).");
                Console.WriteLine($"[CacheProfile] TOTAL: {total.ElapsedMilliseconds} ms. State = 2.");
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