namespace MTW.Memory;

/// <summary>
/// Pointer-chain constants verified against the supplied PAL MTW race dump
/// (Game ID MTWP01, captured in single-player). PowerPC pointers are big-endian.
/// </summary>
public readonly record struct PointerChainDefinition(
    string Name,
    uint RootPointerAddress,
    IReadOnlyList<int> Offsets,
    bool FinalAddressContainsPointer,
    string Description);

public static class ConfirmedPointerChains
{
    // PlayerHolder::sInstance -> PlayerHolder -> players[]
    public const uint PlayerHolderInstancePointerAddress = 0x809C18F8;
    public const int PlayerHolderPlayersArrayOffset = 0x20;
    public const int PlayerPointerStride = 0x04;

    // Player -> PlayerSub -> sub-objects
    public const int PlayerSubOffset = 0x10;
    public const int PlayerPointersInlineOffset = 0x1C;
    public const int PlayerSub10Offset = 0x10;
    public const int PlayerSub10TrickOffset = 0x258;
    public const int PlayerSub10ZipperOffset = 0x25C;
    public const int PlayerSub10BikeExtensionOffset = 0x294;
    public const int PlayerSub14Offset = 0x14;
    public const int PlayerSub18Offset = 0x18;
    public const int PlayerSub1cOffset = 0x1C;

    // PlayerPointers is inline in Player at +0x1C.
    public const int PlayerParamsPointerOffset = 0x00;
    public const int PlayerGraphicsPointerOffset = 0x08;

    // PlayerGraphics -> PlayerPhysicsHolder -> PlayerPhysics
    public const int PlayerGraphicsPhysicsHolderOffset = 0x90;
    public const int PlayerPhysicsHolderPhysicsPointerOffset = 0x04;

    // Racedata::sInstance -> Racedata main -> scenario 0 -> players[12]
    public const uint RaceDataInstancePointerAddress = 0x809BD728;
    public const int RaceDataPlayerRecordsOffset = 0x28;
    public const int RaceDataPlayerRecordStride = 0xF0;
    public const int RaceDataScenarioOffset = 0x20;
    public const int RaceDataScenarioSettingsOffset = 0xB48;

    // Raceinfo::sInstance -> Raceinfo -> players[] -> RaceinfoPlayer
    public const uint RaceInfoInstancePointerAddress = 0x809BD730;
    public const int RaceInfoPlayersArrayPointerOffset = 0x0C;

    // ITEMHandler singleton and documented inline arrays (itemhandler.h).
    public const uint ItemHandlerInstancePointerAddress = 0x809C20F8;
    public const uint ItemSlotDataInstancePointerAddress = 0x809C3670;
    public const int ItemHandlerSendPacketsOffset = 0x00;
    public const int ItemHandlerRecvPacketsOffset = 0x10;
    public const int ItemHandlerTimesOffset = 0xA0;
    public const int ItemPacketStride = 0x08;
    public const int ItemHandlerTimeStride = 0x04;

    // ItemBehaviour::itemBehaviourTable[19] is a static table, not a pointer.
    public const uint ItemBehaviourTableAddress = 0x809C36A0;
    public const int ItemBehaviourTableCount = 19;
    public const int ItemBehaviourEntryStride = 0x1C;

    // ItemSlotData documented inline member offsets (itemslotdata.h).
    public const int ItemSlotDataPlayerChancesOffset = 0x10;
    public const int ItemSlotDataCpuChancesOffset = 0x18;
    public const int ItemSlotDataSpecialChancesOffset = 0x20;
    public const int ItemSlotDataItemsInWheelPointerOffset = 0x28;
    public const int ItemSlotDataSpecialBoxItemsInWheelPointerOffset = 0x2C;
    public const int ItemSlotDataSpawnTimersOffset = 0x34;
    public const int ItemSlotDataPlayerCountOffset = 0x44;
    public const int ItemSlotDataSize = 0x48;

    // The dump contains 12 valid PlayerHolder slots.
    public const int PlayerSlotCount = 12;

    // Offset order follows DolphinConnection.TryReadPointerChain:
    // dereference the root pointer first, then dereference every offset except
    // the final offset, which is the address of the target object.
    public static PointerChainDefinition PlayerObjectChain(int playerId) =>
        new("player", PlayerHolderInstancePointerAddress,
            new[] { PlayerHolderPlayersArrayOffset, playerId * PlayerPointerStride },
            true,
            "PlayerHolder::sInstance -> PlayerHolder + 0x20 -> players[playerId] slot (contains Player*)");

    public static PointerChainDefinition PlayerSub10Chain(int playerId) =>
        new("playerSub10", PlayerHolderInstancePointerAddress,
            new[] { PlayerHolderPlayersArrayOffset, playerId * PlayerPointerStride,
                    PlayerSubOffset, PlayerSub10Offset },
            true,
            "PlayerHolder::sInstance -> players[playerId] -> Player + 0x10 -> PlayerSub + 0x10 (contains PlayerSub10*)");

    public static PointerChainDefinition RaceDataPlayerChain(int playerId) =>
        new("raceData", RaceDataInstancePointerAddress,
            new[] { RaceDataPlayerRecordsOffset + playerId * RaceDataPlayerRecordStride },
            false,
            "Racedata::sInstance -> scenario 0 -> inline RacedataPlayer[playerId]");

    public static PointerChainDefinition RaceInfoPlayerChain(int playerId) =>
        new("raceInfoPlayer", RaceInfoInstancePointerAddress,
            new[] { RaceInfoPlayersArrayPointerOffset, playerId * PlayerPointerStride },
            true,
            "Raceinfo::sInstance -> Raceinfo::players[playerId] slot (contains RaceinfoPlayer*)");

    public static PointerChainDefinition ItemSlotDataChain() =>
        new("itemSlotData", ItemSlotDataInstancePointerAddress, Array.Empty<int>(), true,
            "ItemSlotData::sInstance -> ItemSlotData instance.");

    public static PointerChainDefinition ItemHandlerChain(int playerId) =>
        new("itemHandler", ItemHandlerInstancePointerAddress, Array.Empty<int>(), true,
            "ITEMHandler::sInstance -> ITEMHandler instance; same global instance for each player slot.");

    public static PointerChainDefinition ItemPacketChain(int playerId) =>
        new("itemPacket", ItemHandlerInstancePointerAddress,
            new[] { ItemHandlerRecvPacketsOffset + playerId * ItemPacketStride }, false,
            "ITEMHandler::sInstance -> recvPackets[playerId] inline 8-byte ITEMPacket.");

    public static PointerChainDefinition ItemHandlerTimeChain(int playerId) =>
        new("itemHandlerTime", ItemHandlerInstancePointerAddress,
            new[] { ItemHandlerTimesOffset + playerId * ItemHandlerTimeStride }, false,
            "ITEMHandler::sInstance -> times[playerId] inline uint32; semantic meaning not documented.");
}
