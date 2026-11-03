using GameManager.Dolphin;

namespace MTW.Memory
{
    public sealed class PlayerPointerResolver
    {
        private readonly DolphinConnection _dolphin;

        private delegate bool PointerResolver(int playerId, out IntPtr address);

        public PlayerPointerResolver(DolphinConnection dolphin)
        {
            _dolphin = dolphin;
        }

        private bool IsValidPlayerId(int playerId) =>
            playerId >= 0 && playerId < ConfirmedPointerChains.PlayerSlotCount;

        public bool TryResolvePlayerHolder(int playerId, out IntPtr holder)
        {
            holder = IntPtr.Zero;
            return IsValidPlayerId(playerId) &&
                   _dolphin.TryReadPointer(
                       new IntPtr(ConfirmedPointerChains.PlayerHolderInstancePointerAddress),
                       0,
                       out holder);
        }

        private bool TryResolvePlayer(int playerId, out IntPtr player)
        {
            player = IntPtr.Zero;
            if (!IsValidPlayerId(playerId))
                return false;

            if (!_dolphin.TryReadPointer(
                    new IntPtr(ConfirmedPointerChains.PlayerHolderInstancePointerAddress),
                    0,
                    out IntPtr holder))
                return false;

            if (!_dolphin.TryReadPointer(
                    holder,
                    ConfirmedPointerChains.PlayerHolderPlayersArrayOffset,
                    out IntPtr playersArray))
                return false;

            IntPtr slotAddress = new(
                playersArray.ToInt64() + playerId * ConfirmedPointerChains.PlayerPointerStride);

            return _dolphin.TryReadPointer(slotAddress, 0, out player);
        }

        public bool TryResolvePlayerPointers(int playerId, out IntPtr playerPointers)
        {
            playerPointers = IntPtr.Zero;
            if (!TryResolvePlayer(playerId, out IntPtr player))
                return false;

            // PlayerPointers is an inline structure, not a pointer.
            playerPointers = new IntPtr(
                player.ToInt64() + ConfirmedPointerChains.PlayerPointersInlineOffset);
            return true;
        }

        public bool TryResolvePlayerSub(int playerId, out IntPtr playerSub)
        {
            playerSub = IntPtr.Zero;
            return TryResolvePlayer(playerId, out IntPtr player) &&
                   _dolphin.TryReadPointer(
                       player,
                       ConfirmedPointerChains.PlayerSubOffset,
                       out playerSub);
        }

        public bool TryResolvePlayerSub10(int playerId, out IntPtr playerSub10)
        {
            playerSub10 = IntPtr.Zero;
            return TryResolvePlayerSub(playerId, out IntPtr playerSub) &&
                   _dolphin.TryReadPointer(
                       playerSub,
                       ConfirmedPointerChains.PlayerSub10Offset,
                       out playerSub10);
        }

        public bool TryResolvePlayerSub14(int playerId, out IntPtr playerSub14)
        {
            playerSub14 = IntPtr.Zero;
            return TryResolvePlayerSub(playerId, out IntPtr playerSub) &&
                   _dolphin.TryReadPointer(
                       playerSub,
                       ConfirmedPointerChains.PlayerSub14Offset,
                       out playerSub14);
        }

        public bool TryResolvePlayerSub18(int playerId, out IntPtr playerSub18)
        {
            playerSub18 = IntPtr.Zero;
            return TryResolvePlayerSub(playerId, out IntPtr playerSub) &&
                   _dolphin.TryReadPointer(
                       playerSub,
                       ConfirmedPointerChains.PlayerSub18Offset,
                       out playerSub18);
        }

        public bool TryResolvePlayerSub1c(int playerId, out IntPtr playerSub1c)
        {
            playerSub1c = IntPtr.Zero;
            return TryResolvePlayerSub(playerId, out IntPtr playerSub) &&
                   _dolphin.TryReadPointer(
                       playerSub,
                       ConfirmedPointerChains.PlayerSub1cOffset,
                       out playerSub1c);
        }

        public bool TryResolvePlayerParams(int playerId, out IntPtr playerParams)
        {
            playerParams = IntPtr.Zero;
            return TryResolvePlayerPointers(playerId, out IntPtr pointers) &&
                   _dolphin.TryReadPointer(
                       pointers,
                       ConfirmedPointerChains.PlayerParamsPointerOffset,
                       out playerParams);
        }

        public bool TryResolvePlayerGraphics(int playerId, out IntPtr playerGraphics)
        {
            playerGraphics = IntPtr.Zero;
            return TryResolvePlayerPointers(playerId, out IntPtr pointers) &&
                   _dolphin.TryReadPointer(
                       pointers,
                       ConfirmedPointerChains.PlayerGraphicsPointerOffset,
                       out playerGraphics);
        }

        public bool TryResolvePlayerPhysicsHolder(int playerId, out IntPtr physicsHolder)
        {
            physicsHolder = IntPtr.Zero;
            return TryResolvePlayerGraphics(playerId, out IntPtr graphics) &&
                   _dolphin.TryReadPointer(
                       graphics,
                       ConfirmedPointerChains.PlayerGraphicsPhysicsHolderOffset,
                       out physicsHolder);
        }

        public bool TryResolvePlayerPhysics(int playerId, out IntPtr playerPhysics)
        {
            playerPhysics = IntPtr.Zero;
            return TryResolvePlayerPhysicsHolder(playerId, out IntPtr holder) &&
                   _dolphin.TryReadPointer(
                       holder,
                       ConfirmedPointerChains.PlayerPhysicsHolderPhysicsPointerOffset,
                       out playerPhysics);
        }

        public bool TryResolvePlayerTrick(int playerId, out IntPtr playerTrick)
        {
            playerTrick = IntPtr.Zero;
            return TryResolvePlayerSub10(playerId, out IntPtr playerSub10) &&
                   _dolphin.TryReadPointer(playerSub10, ConfirmedPointerChains.PlayerSub10TrickOffset, out playerTrick);
        }

        public bool TryResolvePlayerZipper(int playerId, out IntPtr playerZipper)
        {
            playerZipper = IntPtr.Zero;
            return TryResolvePlayerSub10(playerId, out IntPtr playerSub10) &&
                   _dolphin.TryReadPointer(playerSub10, ConfirmedPointerChains.PlayerSub10ZipperOffset, out playerZipper);
        }

        public bool TryResolvePlayerSub10BikeExtension(int playerId, out IntPtr bikeExtension)
        {
            bikeExtension = IntPtr.Zero;
            if (!TryResolvePlayerSub10(playerId, out IntPtr playerSub10))
                return false;

            // This address is only a valid PlayerSub10Bike extension for bikes.
            bikeExtension = new IntPtr(
                playerSub10.ToInt64() + ConfirmedPointerChains.PlayerSub10BikeExtensionOffset);
            return true;
        }

        public bool TryResolveRaceData(int playerId, out IntPtr raceData)
        {
            raceData = IntPtr.Zero;
            if (!IsValidPlayerId(playerId) ||
                !_dolphin.TryReadPointer(
                    new IntPtr(ConfirmedPointerChains.RaceDataInstancePointerAddress),
                    0,
                    out IntPtr raceDataInstance))
                return false;

            raceData = new IntPtr(
                raceDataInstance.ToInt64()
                + ConfirmedPointerChains.RaceDataPlayerRecordsOffset
                + playerId * ConfirmedPointerChains.RaceDataPlayerRecordStride);

            return true;
        }

        public bool TryResolveRaceDataScenario(int playerId, out IntPtr scenario)
        {
            scenario = IntPtr.Zero;
            if (!IsValidPlayerId(playerId) ||
                !_dolphin.TryReadPointer(
                    new IntPtr(ConfirmedPointerChains.RaceDataInstancePointerAddress),
                    0,
                    out IntPtr raceDataInstance))
                return false;

            scenario = new IntPtr(
                raceDataInstance.ToInt64() + ConfirmedPointerChains.RaceDataScenarioOffset);
            return true;
        }

        public bool TryResolveRaceDataSettings(int playerId, out IntPtr settings)
        {
            settings = IntPtr.Zero;
            if (!TryResolveRaceDataScenario(playerId, out IntPtr scenario))
                return false;

            settings = new IntPtr(
                scenario.ToInt64() + ConfirmedPointerChains.RaceDataScenarioSettingsOffset);
            return true;
        }

        public bool TryResolveRaceInfo(int playerId, out IntPtr raceInfo)
        {
            raceInfo = IntPtr.Zero;
            if (!IsValidPlayerId(playerId))
                return false;

            return _dolphin.TryReadPointer(
                new IntPtr(ConfirmedPointerChains.RaceInfoInstancePointerAddress),
                0,
                out raceInfo);
        }

        public bool TryResolveRaceInfoPlayer(int playerId, out IntPtr raceInfoPlayer)
        {
            raceInfoPlayer = IntPtr.Zero;
            if (!TryResolveRaceInfo(playerId, out IntPtr raceInfo) ||
                !_dolphin.TryReadPointer(
                    raceInfo,
                    ConfirmedPointerChains.RaceInfoPlayersArrayPointerOffset,
                    out IntPtr playersArray))
                return false;

            IntPtr playerAddress = new(playersArray.ToInt64() + playerId * 4);
            return _dolphin.TryReadPointer(playerAddress, 0, out raceInfoPlayer);
        }

        public bool TryResolveItemSlotData(int playerId, out IntPtr itemSlotData)
        {
            itemSlotData = IntPtr.Zero;
            return IsValidPlayerId(playerId) &&
                   _dolphin.TryReadPointer(
                       new IntPtr(ConfirmedPointerChains.ItemSlotDataInstancePointerAddress),
                       0,
                       out itemSlotData);
        }

        public bool TryResolveItemHandler(int playerId, out IntPtr itemHandler)
        {
            itemHandler = IntPtr.Zero;
            return IsValidPlayerId(playerId) &&
                   _dolphin.TryReadPointer(
                       new IntPtr(ConfirmedPointerChains.ItemHandlerInstancePointerAddress),
                       0,
                       out itemHandler);
        }

        public bool TryResolveItemPacket(int playerId, out IntPtr itemPacket)
        {
            itemPacket = IntPtr.Zero;
            if (!TryResolveItemHandler(playerId, out IntPtr itemHandler))
                return false;

            itemPacket = new IntPtr(
                itemHandler.ToInt64() + ConfirmedPointerChains.ItemHandlerRecvPacketsOffset +
                playerId * ConfirmedPointerChains.ItemPacketStride);
            return true;
        }

        public bool TryResolveItemHandlerPlayerTime(int playerId, out IntPtr itemTime)
        {
            itemTime = IntPtr.Zero;
            if (!TryResolveItemHandler(playerId, out IntPtr itemHandler))
                return false;

            itemTime = new IntPtr(
                itemHandler.ToInt64() + ConfirmedPointerChains.ItemHandlerTimesOffset +
                playerId * ConfirmedPointerChains.ItemHandlerTimeStride);
            return true;
        }


        /// <summary>
        /// Resolves every cache address for one slot while reusing shared roots
        /// (PlayerHolder, RaceData, RaceInfo, ItemHandler and ItemSlotData).
        /// This avoids re-reading the same root pointer for every resource.
        /// </summary>
        public Dictionary<string, IntPtr> ResolveAllResourcesForPlayer(int playerId)
        {
            Dictionary<string, IntPtr> result = new(StringComparer.Ordinal);
            if (!IsValidPlayerId(playerId))
                return result;

            void Add(string name, IntPtr address)
            {
                if (address != IntPtr.Zero)
                    result[name] = address;
            }

            // PlayerHolder -> players[playerId]
            if (_dolphin.TryReadPointer(
                    new IntPtr(ConfirmedPointerChains.PlayerHolderInstancePointerAddress), 0, out IntPtr holder))
            {
                Add("playerHolder", holder);
                if (_dolphin.TryReadPointer(holder, ConfirmedPointerChains.PlayerHolderPlayersArrayOffset, out IntPtr playersArray))
                {
                    IntPtr slotAddress = new(playersArray.ToInt64() + playerId * ConfirmedPointerChains.PlayerPointerStride);
                    if (_dolphin.TryReadPointer(slotAddress, 0, out IntPtr player))
                    {
                        Add("player", player);
                        IntPtr pointers = new(player.ToInt64() + ConfirmedPointerChains.PlayerPointersInlineOffset);
                        Add("playerPointers", pointers);

                        if (_dolphin.TryReadPointer(player, ConfirmedPointerChains.PlayerSubOffset, out IntPtr sub))
                        {
                            Add("playerSub", sub);
                            if (_dolphin.TryReadPointer(sub, ConfirmedPointerChains.PlayerSub10Offset, out IntPtr sub10))
                            {
                                Add("playerSub10", sub10);
                                Add("playerTrick", ReadPointerOrZero(sub10, ConfirmedPointerChains.PlayerSub10TrickOffset));
                                Add("playerZipper", ReadPointerOrZero(sub10, ConfirmedPointerChains.PlayerSub10ZipperOffset));
                                Add("playerSub10BikeExtension", new IntPtr(sub10.ToInt64() + ConfirmedPointerChains.PlayerSub10BikeExtensionOffset));
                            }
                            Add("playerSub14", ReadPointerOrZero(sub, ConfirmedPointerChains.PlayerSub14Offset));
                            Add("playerSub18", ReadPointerOrZero(sub, ConfirmedPointerChains.PlayerSub18Offset));
                            Add("playerSub1c", ReadPointerOrZero(sub, ConfirmedPointerChains.PlayerSub1cOffset));
                        }

                        Add("playerParams", ReadPointerOrZero(pointers, ConfirmedPointerChains.PlayerParamsPointerOffset));
                        IntPtr graphics = ReadPointerOrZero(pointers, ConfirmedPointerChains.PlayerGraphicsPointerOffset);
                        Add("playerGraphics", graphics);
                        if (graphics != IntPtr.Zero)
                        {
                            IntPtr physicsHolder = ReadPointerOrZero(graphics, ConfirmedPointerChains.PlayerGraphicsPhysicsHolderOffset);
                            Add("playerPhysicsHolder", physicsHolder);
                            if (physicsHolder != IntPtr.Zero)
                                Add("playerPhysics", ReadPointerOrZero(physicsHolder, ConfirmedPointerChains.PlayerPhysicsHolderPhysicsPointerOffset));
                        }
                    }
                }
            }

            // RaceData and RaceInfo roots are shared by all 12 player slots.
            if (_dolphin.TryReadPointer(new IntPtr(ConfirmedPointerChains.RaceDataInstancePointerAddress), 0, out IntPtr raceData))
            {
                Add("raceData", new IntPtr(raceData.ToInt64() + ConfirmedPointerChains.RaceDataPlayerRecordsOffset + playerId * ConfirmedPointerChains.RaceDataPlayerRecordStride));
                IntPtr scenario = new(raceData.ToInt64() + ConfirmedPointerChains.RaceDataScenarioOffset);
                Add("raceDataScenario", scenario);
                Add("raceDataSettings", new IntPtr(scenario.ToInt64() + ConfirmedPointerChains.RaceDataScenarioSettingsOffset));
            }

            if (_dolphin.TryReadPointer(new IntPtr(ConfirmedPointerChains.RaceInfoInstancePointerAddress), 0, out IntPtr raceInfo))
            {
                Add("raceInfo", raceInfo);
                if (_dolphin.TryReadPointer(raceInfo, ConfirmedPointerChains.RaceInfoPlayersArrayPointerOffset, out IntPtr racePlayers))
                {
                    IntPtr playerAddress = new(racePlayers.ToInt64() + playerId * ConfirmedPointerChains.PlayerPointerStride);
                    Add("raceInfoPlayer", ReadPointerOrZero(playerAddress, 0));
                }
            }

            Add("itemSlotData", ReadPointerOrZero(new IntPtr(ConfirmedPointerChains.ItemSlotDataInstancePointerAddress), 0));
            IntPtr itemHandler = ReadPointerOrZero(new IntPtr(ConfirmedPointerChains.ItemHandlerInstancePointerAddress), 0);
            Add("itemHandler", itemHandler);
            if (itemHandler != IntPtr.Zero)
            {
                Add("itemPacket", new IntPtr(itemHandler.ToInt64() + ConfirmedPointerChains.ItemHandlerRecvPacketsOffset + playerId * ConfirmedPointerChains.ItemPacketStride));
                Add("itemHandlerTime", new IntPtr(itemHandler.ToInt64() + ConfirmedPointerChains.ItemHandlerTimesOffset + playerId * ConfirmedPointerChains.ItemHandlerTimeStride));
            }

            return result;
        }

        private IntPtr ReadPointerOrZero(IntPtr address, int offset) =>
            _dolphin.TryReadPointer(address, offset, out IntPtr pointer) ? pointer : IntPtr.Zero;

        private static Dictionary<int, IntPtr> ResolveAll(PointerResolver resolver)
        {
            Dictionary<int, IntPtr> result = new();

            for (int playerId = 0; playerId < ConfirmedPointerChains.PlayerSlotCount; playerId++)
            {
                if (resolver(playerId, out IntPtr address) && address != IntPtr.Zero)
                    result[playerId] = address;
            }

            return result;
        }

        public Dictionary<int, IntPtr> ResolveAllPlayersPlayerHolder() =>
            ResolveAll(TryResolvePlayerHolder);

        public Dictionary<int, IntPtr> ResolveAllPlayers() =>
            ResolveAll(TryResolvePlayer);

        public Dictionary<int, IntPtr> ResolveAllPlayersPointers() =>
            ResolveAll(TryResolvePlayerPointers);

        public Dictionary<int, IntPtr> ResolveAllPlayersSub() =>
            ResolveAll(TryResolvePlayerSub);

        public Dictionary<int, IntPtr> ResolveAllPlayersSub10() =>
            ResolveAll(TryResolvePlayerSub10);

        public Dictionary<int, IntPtr> ResolveAllPlayersSub14() =>
            ResolveAll(TryResolvePlayerSub14);

        public Dictionary<int, IntPtr> ResolveAllPlayersSub18() =>
            ResolveAll(TryResolvePlayerSub18);

        public Dictionary<int, IntPtr> ResolveAllPlayersSub1c() =>
            ResolveAll(TryResolvePlayerSub1c);

        public Dictionary<int, IntPtr> ResolveAllPlayersParams() =>
            ResolveAll(TryResolvePlayerParams);

        public Dictionary<int, IntPtr> ResolveAllPlayersGraphics() =>
            ResolveAll(TryResolvePlayerGraphics);

        public Dictionary<int, IntPtr> ResolveAllPlayersPhysicsHolder() =>
            ResolveAll(TryResolvePlayerPhysicsHolder);

        public Dictionary<int, IntPtr> ResolveAllPlayersPhysics() =>
            ResolveAll(TryResolvePlayerPhysics);

        public Dictionary<int, IntPtr> ResolveAllPlayersTrick() =>
            ResolveAll(TryResolvePlayerTrick);

        public Dictionary<int, IntPtr> ResolveAllPlayersZipper() =>
            ResolveAll(TryResolvePlayerZipper);

        public Dictionary<int, IntPtr> ResolveAllPlayersSub10BikeExtension() =>
            ResolveAll(TryResolvePlayerSub10BikeExtension);

        public Dictionary<int, IntPtr> ResolveAllPlayersRaceData() =>
            ResolveAll(TryResolveRaceData);

        public Dictionary<int, IntPtr> ResolveAllPlayersRaceDataScenario() =>
            ResolveAll(TryResolveRaceDataScenario);

        public Dictionary<int, IntPtr> ResolveAllPlayersRaceDataSettings() =>
            ResolveAll(TryResolveRaceDataSettings);

        public Dictionary<int, IntPtr> ResolveAllPlayersRaceInfo() =>
            ResolveAll(TryResolveRaceInfo);

        public Dictionary<int, IntPtr> ResolveAllPlayersRaceInfoPlayer() =>
            ResolveAll(TryResolveRaceInfoPlayer);

        public Dictionary<int, IntPtr> ResolveAllItemSlotData() =>
            ResolveAll(TryResolveItemSlotData);

        public Dictionary<int, IntPtr> ResolveAllItemHandlers() =>
            ResolveAll(TryResolveItemHandler);

        public Dictionary<int, IntPtr> ResolveAllItemPackets() =>
            ResolveAll(TryResolveItemPacket);

        public Dictionary<int, IntPtr> ResolveAllItemHandlerPlayerTimes() =>
            ResolveAll(TryResolveItemHandlerPlayerTime);
    }
}
