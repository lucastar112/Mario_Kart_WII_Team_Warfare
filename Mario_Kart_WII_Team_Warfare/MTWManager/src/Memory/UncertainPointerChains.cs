namespace MTW.Memory;

/// <summary>
/// Deliberately unverified multiplayer assumptions. The underlying player-slot
/// pointer layout is confirmed for all 12 slots, but the supplied dump has only
/// one real local player; slots 1-3 are CPUs in this capture.
/// </summary>
public static class UncertainPointerChains
{
    // Hypothesis only: in split-screen, local players 2-4 occupy PlayerHolder
    // slots 1-3 respectively, using the same PlayerHolder -> players[] chain.
    // The pointer chain itself works for CPU slots 1-3 in the supplied dump;
    // the local-player identity mapping must be checked in a multiplayer race.
    public const int LocalPlayer2CandidateSlot = 1;
    public const int LocalPlayer3CandidateSlot = 2;
    public const int LocalPlayer4CandidateSlot = 3;

    // Candidate pointer-chain records. The slot numbers are hypotheses; the
    // underlying chain templates are the confirmed PlayerHolder/PlayerSub10
    // chains used by the resolver.
    public static PointerChainDefinition LocalPlayer2PlayerChain =>
        ConfirmedPointerChains.PlayerObjectChain(LocalPlayer2CandidateSlot);
    public static PointerChainDefinition LocalPlayer3PlayerChain =>
        ConfirmedPointerChains.PlayerObjectChain(LocalPlayer3CandidateSlot);
    public static PointerChainDefinition LocalPlayer4PlayerChain =>
        ConfirmedPointerChains.PlayerObjectChain(LocalPlayer4CandidateSlot);

    public static PointerChainDefinition LocalPlayer2PlayerSub10Chain =>
        ConfirmedPointerChains.PlayerSub10Chain(LocalPlayer2CandidateSlot);
    public static PointerChainDefinition LocalPlayer3PlayerSub10Chain =>
        ConfirmedPointerChains.PlayerSub10Chain(LocalPlayer3CandidateSlot);
    public static PointerChainDefinition LocalPlayer4PlayerSub10Chain =>
        ConfirmedPointerChains.PlayerSub10Chain(LocalPlayer4CandidateSlot);

    // PlayerSub14's pointer resolves for all 12 slots, but mkw-structures
    // documents only its total size (0x100), not its internal field layout.
    // Do not add named PlayerSub14 fields until offsets are independently found.

    // PlayerSub1c::trickableTimer has contradictory comments in player.h
    // (0xA4 is named, but the surrounding unknown range also includes 0xA4).
    public const int PlayerSub1cTrickableTimerCandidateOffset = 0xA4;

    // Bike-only PlayerSub10 extension offsets are derived from inheritance and
    // the declared class size; validate against live gameplay before relying on
    // wheelie timers/flags or turnParams.

    // PlayerTrick::playerSub10 at +0x4C is inconsistent for local slot 0 in the
    // supplied dump (the stored value does not look like a MEM1/MEM2 pointer).
    public const int PlayerTrickPlayerSub10PointerCandidateOffset = 0x4C;

    // PlayerSub10 +0x294..+0x2C3 is the bike-only extension. It is valid for
    // bike players (vtable 0x808B5EE8 in this dump), not for karts.

    // ITEMHandler::recvPackets[playerId] addresses and the 8-byte stride are
    // documented and the raw packet bytes are readable in the dump. However,
    // ITEMPacket's member layout is not included in the supplied structures;
    // do not interpret any byte as an item ID until independently verified.
    public static PointerChainDefinition ItemPacketRawChain(int playerId) =>
        ConfirmedPointerChains.ItemPacketChain(playerId);

    // ITEMHandler::times[playerId] is a documented uint32 array, but its
    // runtime meaning has not been established from the available source.
    public static PointerChainDefinition ItemHandlerTimeRawChain(int playerId) =>
        ConfirmedPointerChains.ItemHandlerTimeChain(playerId);

    // PlayerSub10::boost.types bit 3 is described as unused/intended for bullet
    // in player.h. It is only a candidate flag, not a confirmed Bullet Bill state.
    public const int BulletBoostFlagCandidateBit = 3;

    // PlayerSub10::shockTimer is documented for shock effects. Treating it as
    // the small/shrunk visual state is a useful hypothesis, not a proven enum.
}
