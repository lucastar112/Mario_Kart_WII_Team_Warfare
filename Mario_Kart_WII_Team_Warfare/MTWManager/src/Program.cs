using MTW.Cache;
using MTW.Memory;
using MTW.Race;
using GameManager.Dolphin;
using System.Diagnostics;

namespace MTW
{
    internal static class Program
    {
        private const string gameFolder = "games";
        private const string gameId = "MTW";
        private const string configFolder = "config";
        private const string cacheFile = "cache.json";

        public static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: MTW.exe <DolphinPID>");
                return;
            }

            if (!int.TryParse(args[0], out int dolphinPid))
            {
                Console.WriteLine($"Invalid Dolphin PID: {args[0]}");
                return;
            }

            Process dolphinProcess;

            try
            {
                dolphinProcess = Process.GetProcessById(dolphinPid);
            }
            catch
            {
                Console.WriteLine($"Could not find Dolphin process {dolphinPid}.");
                return;
            }

            Console.WriteLine($"Connected to Dolphin process {dolphinProcess.Id}.");

            DolphinConnection dolphin = new DolphinConnection(dolphinProcess);
            PlayerPointerResolver resolver = new PlayerPointerResolver(dolphin);

            #if DEBUG
                string configuration = "DEV";
            #else
                string configuration = "REL";
            #endif

            DynamicCacheWriter cacheWriter = new DynamicCacheWriter(Path.Combine(gameFolder, gameId, configuration, configFolder, cacheFile));
            CacheBuildController cacheBuildController = new CacheBuildController(dolphin, resolver, cacheWriter);
            cacheBuildController.Run(CancellationToken.None);
            dolphin.Dispose();
        }
    }
}