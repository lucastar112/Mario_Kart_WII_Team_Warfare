using GameManager.Dolphin;

namespace MTW.Memory
{
    public sealed class PlayerPointerResolver
    {
        private static readonly IntPtr RaceInstanceAddress = new IntPtr(0x809C18F8);

        private static readonly int RaceInstanceOffset = 0x00;
        private static readonly int PlayerArrayOffset = 0x20;

        private static readonly int PlayerPointerStep = 0x04;

        private static readonly int PlayerSub10Offset1 = 0x00;
        private static readonly int PlayerSub10Offset2 = 0x10;
        private static readonly int PlayerSub10Offset3 = 0x10;

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

            if (!_dolphin.TryReadPointer(new IntPtr(0x809C18F8), 0x00, out IntPtr raceInstance))
            {
                Console.WriteLine("Failed to resolve race instance.");
                return false;
            }

            Console.WriteLine($"Race instance: 0x{raceInstance.ToInt64():X8}");

            if (!_dolphin.TryReadPointer(raceInstance, 0x20, out IntPtr playerArray))
            {
                Console.WriteLine("Failed to resolve player pointer array.");
                return false;
            }

            Console.WriteLine($"Player pointer array: 0x{playerArray.ToInt64():X8}");

            IntPtr playerPointerAddress = new IntPtr(playerArray.ToInt64() + playerId * 0x04);

            Console.WriteLine($"Player {playerId} pointer address: 0x{playerPointerAddress.ToInt64():X8}");

            if (!_dolphin.TryReadPointer(playerPointerAddress, 0x00, out IntPtr player))
            {
                Console.WriteLine($"Failed to resolve player {playerId}.");
                return false;
            }

            Console.WriteLine($"Player {playerId}: 0x{player.ToInt64():X8}");

            if (!_dolphin.TryReadPointer(player, 0x10, out IntPtr intermediate))
            {
                Console.WriteLine($"Failed to resolve player {playerId} intermediate pointer.");
                return false;
            }

            Console.WriteLine($"Player {playerId} intermediate: 0x{intermediate.ToInt64():X8}");

            if (!_dolphin.TryReadPointer(intermediate, 0x10, out playerSub10))
            {
                Console.WriteLine($"Failed to resolve player {playerId} playerSub10.");
                return false;
            }

            Console.WriteLine($"Player {playerId} playerSub10: 0x{playerSub10.ToInt64():X8}");
            return playerSub10 != IntPtr.Zero;
        }


        public Dictionary<int, IntPtr> ResolveAllPlayers()
        {
            Dictionary<int, IntPtr> players = new();

            for (int playerId = 0; playerId < 12; playerId++)
            {
                if (TryResolvePlayerSub10(playerId, out IntPtr playerSub10))
                    players[playerId] = playerSub10;
            }
            return players;
        }
    }
}