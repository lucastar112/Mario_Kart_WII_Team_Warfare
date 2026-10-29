using GameManager.Dolphin;

namespace MTW.Memory
{
    public sealed class PlayerPointerResolver
    {
        // PlayerSub10 offsets
        private static readonly IntPtr RaceInstanceAddress = new IntPtr(0x809C18F8);

        private static readonly int RaceInstanceOffset = 0x00;
        private static readonly int PlayerArrayOffset = 0x20;

        private static readonly int PlayerPointerStep = 0x04;

        private static readonly int PlayerSub10Offset1 = 0x00;
        private static readonly int PlayerSub10Offset2 = 0x10;
        private static readonly int PlayerSub10Offset3 = 0x10;

        // Racedata offsets
        private static readonly IntPtr RaceDataSInstance = new IntPtr(0x809bd728);
        private static readonly int RaceSInstanceOffset = 0x00;
<<<<<<< Updated upstream
        private static readonly int RaceDataPlayersArray = 0x08;
        private static readonly int RaceDataPlyerOffset = 0xF0;
=======
        private static readonly int RaceDataPlayersArray = 0x8 + 0x20;
        private static readonly int RaceDataPlayerOffset = 0xF0;
>>>>>>> Stashed changes

        private readonly DolphinConnection _dolphin;

        public PlayerPointerResolver(DolphinConnection dolphin)
        {
            _dolphin = dolphin;
        }

        public bool TryResolvePlayerSub10(int playerId, out IntPtr playerSub10)
        {
            playerSub10 = IntPtr.Zero;

            if (playerId < 0 || playerId >= 12)
                return false;

            Console.WriteLine($"Resolving MTW player {playerId}...");

            if (!_dolphin.TryReadPointer(RaceInstanceAddress, RaceInstanceOffset, out IntPtr raceInstance))
            {
                Console.WriteLine("Failed to resolve race instance.");
                return false;
            }

            Console.WriteLine($"Race instance: 0x{raceInstance.ToInt64():X8}");

            if (!_dolphin.TryReadPointer(raceInstance, PlayerArrayOffset, out IntPtr playerArray))
            {
                Console.WriteLine("Failed to resolve player pointer array.");
                return false;
            }

            Console.WriteLine($"Player pointer array: 0x{playerArray.ToInt64():X8}");

            IntPtr playerPointerAddress = new IntPtr(playerArray.ToInt64() + playerId * PlayerPointerStep);

            Console.WriteLine($"Player {playerId} pointer address: 0x{playerPointerAddress.ToInt64():X8}");

            if (!_dolphin.TryReadPointer(playerPointerAddress, PlayerSub10Offset1, out IntPtr player))
            {
                Console.WriteLine($"Failed to resolve player {playerId}.");
                return false;
            }

            Console.WriteLine($"Player {playerId}: 0x{player.ToInt64():X8}");

            if (!_dolphin.TryReadPointer(player, PlayerSub10Offset2, out IntPtr intermediate))
            {
                Console.WriteLine($"Failed to resolve player {playerId} intermediate pointer.");
                return false;
            }

            Console.WriteLine($"Player {playerId} intermediate: 0x{intermediate.ToInt64():X8}");

            if (!_dolphin.TryReadPointer(intermediate, PlayerSub10Offset3, out playerSub10))
            {
                Console.WriteLine($"Failed to resolve player {playerId} playerSub10.");
                return false;
            }

            Console.WriteLine($"Player {playerId} playerSub10: 0x{playerSub10.ToInt64():X8}");
            return playerSub10 != IntPtr.Zero;
        }

        public bool TryResolveRaceData(int playerId, out IntPtr raceData)
        {
            raceData = IntPtr.Zero;

            if (playerId < 0 || playerId >= 12)
                return false;

            Console.WriteLine($"Resolving MTW player {playerId}...");

            if (!_dolphin.TryReadPointer(RaceDataSInstance, RaceSInstanceOffset, out IntPtr raceSInstance))
            {
                Console.WriteLine("Failed to resolve race instance.");
                return false;
            }

            Console.WriteLine($"Race instance: 0x{raceSInstance.ToInt64():X8}");

<<<<<<< Updated upstream
            if (!_dolphin.TryReadPointer(raceSInstance, RaceDataPlayersArray, out IntPtr playerArray))
            {
                Console.WriteLine("Failed to resolve player pointer array.");
                return false;
            }

            Console.WriteLine($"Player pointer array: 0x{playerArray.ToInt64():X8}");

            raceData = new IntPtr(playerArray.ToInt64() + playerId * RaceDataPlyerOffset);
=======
            raceData = new IntPtr(raceSInstance.ToInt64() + RaceDataPlayersArray + playerId * RaceDataPlayerOffset);
>>>>>>> Stashed changes

            Console.WriteLine($"Player {playerId} raceData address: 0x{raceData.ToInt64():X8}");

            return raceData != IntPtr.Zero;
        }


        public Dictionary<int, IntPtr> ResolveAllPlayersSub10()
        {
            Dictionary<int, IntPtr> players = new();

            for (int playerId = 0; playerId < 12; playerId++)
            {
                if (TryResolvePlayerSub10(playerId, out IntPtr playerSub10))
                    players[playerId] = playerSub10;
            }
            return players;
        }

        public Dictionary<int, IntPtr> ResolveAllPlayersRaceData()
        {
            Dictionary<int, IntPtr> players = new();

            for (int playerId = 0; playerId < 12; playerId++)
            {
                if (TryResolveRaceData(playerId, out IntPtr raceData))
                    players[playerId] = raceData;
            }
            return players;
        }
    }
}