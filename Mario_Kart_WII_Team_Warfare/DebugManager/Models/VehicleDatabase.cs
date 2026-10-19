namespace DebugManager.Data;

using DebugManager.Models;

public static class VehicleDatabase
{
    /// <summary>
    /// Define every vehicle ingame
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, VehicleDefinition> All = new Dictionary<byte, VehicleDefinition>
        {
            [0x00] = new(0x00, "Standard Kart S", "StandardKartS.png"),
            [0x01] = new(0x01, "Standard Kart M", "StandardKartM.png"),
            [0x02] = new(0x02, "Standard Kart L", "StandardKartL.png"),
            [0x03] = new(0x03, "Booster Seat", "BoosterSeat.png"),
            [0x04] = new(0x04, "Classic Dragster", "ClassicDragster.png"),
            [0x05] = new(0x05, "Offroader", "Offroader.png"),
            [0x06] = new(0x06, "Mini Beast", "MiniBeast.png"),
            [0x07] = new(0x07, "Wild Wing", "WildWing.png"),
            [0x08] = new(0x08, "Flame Flyer", "FlameFlyer.png"),
            [0x09] = new(0x09, "Cheep Charger", "CheepCharger.png"),
            [0x0A] = new(0x0A, "Super Blooper", "SuperBlooper.png"),
            [0x0B] = new(0x0B, "Piranha Prowler", "PiranhaProwler.png"),
            [0x0C] = new(0x0C, "Tiny Titan", "TinyTitan.png"),
            [0x0D] = new(0x0D, "Daytripper", "Daytripper.png"),
            [0x0E] = new(0x0E, "Jetsetter", "Jetsetter.png"),
            [0x0F] = new(0x0F, "Blue Falcon", "BlueFalcon.png"),
            [0x10] = new(0x10, "Sprinter", "Sprinter.png"),
            [0x11] = new(0x11, "Honeycoupe", "Honeycoupe.png"),
            [0x12] = new(0x12, "Standard Bike S", "StandardBikeS.png"),
            [0x13] = new(0x13, "Standard Bike M", "StandardBikeM.png"),
            [0x14] = new(0x14, "Standard Bike L", "StandardBikeL.png"),
            [0x15] = new(0x15, "Bullet Bike", "BulletBike.png"),
            [0x16] = new(0x16, "Mach Bike", "MachBike.png"),
            [0x17] = new(0x17, "Flame Runner", "FlameRunner.png"),
            [0x18] = new(0x18, "Bit Bike", "BitBike.png"),
            [0x19] = new(0x19, "Sugarscoot", "Sugarscoot.png"),
            [0x1A] = new(0x1A, "Wario Bike", "WarioBike.png"),
            [0x1B] = new(0x1B, "Quacker", "Quacker.png"),
            [0x1C] = new(0x1C, "Zip Zip", "ZipZip.png"),
            [0x1D] = new(0x1D, "Shooting Star", "ShootingStar.png"),
            [0x1E] = new(0x1E, "Magikruiser", "Magikruiser.png"),
            [0x1F] = new(0x1F, "Sneakster", "Sneakster.png"),
            [0x20] = new(0x20, "Spear", "Spear.png"),
            [0x21] = new(0x21, "Jet Bubble", "JetBubble.png"),
            [0x22] = new(0x22, "Dolphin Dasher", "DolphinDasher.png"),
            [0x23] = new(0x23, "Phantom", "Phantom.png"),
        };
}