using MTW.Cache;
using MTW.Memory;
using MTW.Race;
using GameManager.Dolphin;
using System.Diagnostics;
using GameManager.Shared.Client;

namespace MTW
{
    internal static class Program
    {
        private const string gameFolder = "games";
        private const string gameId = "MTW";
        private const string configFolder = "config";
        private const string cacheFile = "cache.json";

        public static async Task Main(string[] args)
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

            GameManagerClient gameManagerClient = new GameManagerClient();

            try
            {
                await gameManagerClient.ConnectAsync();

                Console.WriteLine("Connected to GameManager.");

                string cachePath = Path.Combine(
                    gameFolder,
                    gameId,
                    configuration,
                    configFolder,
                    cacheFile
                );

                DynamicCacheWriter cacheWriter = new DynamicCacheWriter(
                    cachePath,
                    gameManagerClient
                );

                CacheBuildController cacheBuildController = new CacheBuildController(
                    dolphin,
                    resolver,
                    cacheWriter
                );

                await cacheBuildController.Run(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MTW error: {ex}");
            }
            finally
            {
                // TODO: Dispose of resources properly
                //gameManagerClient.Dispose();
                dolphin.Dispose();
            }
        }
    }
}