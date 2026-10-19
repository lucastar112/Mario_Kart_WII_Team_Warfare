namespace DebugManager.Data;

using DebugManager.Models;

public static class CharacterDatabase
{
    /// <summary>
    /// Define every vanilla characters ingame
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, CharacterDefinition> All = new Dictionary<byte, CharacterDefinition>
        {
            [0x00] = new(0x00, "mr", "Mario", "Mario.png"),
            [0x01] = new(0x01, "bpc", "Baby Peach", "BabyPeach.png"),
            [0x02] = new(0x02, "wl", "Waluigi", "Waluigi.png"),
            [0x03] = new(0x03, "kp", "Bowser", "Bowser.png"),
            [0x04] = new(0x04, "bds", "Baby Daisy", "BabyDaisy.png"),
            [0x05] = new(0x05, "ka", "Dry Bones", "DryBones.png"),
            [0x06] = new(0x06, "bmr", "Baby Mario", "BabyMario.png"),
            [0x07] = new(0x07, "lg", "Luigi", "Luigi.png"),
            [0x08] = new(0x08, "ko", "Toad", "Toad.png"),
            [0x09] = new(0x09, "dk", "Donkey Kong", "Donkey.png"),
            [0x0A] = new(0x0A, "ys", "Yoshi", "Yoshi.png"),
            [0x0B] = new(0x0B, "wr", "Wario", "Wario.png"),
            [0x0C] = new(0x0C, "blg", "Baby Luigi", "BabyLuigi.png"),
            [0x0D] = new(0x0D, "kk", "Toadette", "Toadette.png"),
            [0x0E] = new(0x0E, "nk", "Koopa Troopa", "Koopa.png"),
            [0x0F] = new(0x0F, "ds", "Daisy", "Daisy.png"),
            [0x10] = new(0x10, "pc", "Peach", "Peach.png"),
            [0x11] = new(0x11, "ca", "Birdo", "Birdo.png"),
            [0x12] = new(0x12, "dd", "Diddy Kong", "Diddy.png"),
            [0x13] = new(0x13, "kt", "King Boo", "Boo.png"),
            [0x14] = new(0x14, "jn", "Bowser Jr.", "BowserJr.png"),
            [0x15] = new(0x15, "bk", "Dry Bowser", "DryBowser.png"),
            [0x16] = new(0x16, "fk", "Funky Kong", "Funky.png"),
            [0x17] = new(0x17, "rs", "Rosalina", "Rosalina.png"),

            [0x18] = new(0x18, "sa_mii_m", "Small Mii Outfit A (Male)", "MrM.png"),
            [0x19] = new(0x19, "sa_mii_f", "Small Mii Outfit A (Female)", "MrL.png"),
            [0x1A] = new(0x1A, "sb_mii_m", "Small Mii Outfit B (Male)", "MrM.png"),
            [0x1B] = new(0x1B, "sb_mii_f", "Small Mii Outfit B (Female)", "MrL.png"),

            [0x1C] = new(0x1C, "sc_mii_m", "Small Mii Outfit C (Male)", "MrM.png"),
            [0x1D] = new(0x1D, "sc_mii_f", "Small Mii Outfit C (Female)", "MrL.png"),

            [0x1E] = new(0x1E, "ma_mii_m", "Medium Mii Outfit A (Male)", "MrM.png"),
            [0x1F] = new(0x1F, "ma_mii_f", "Medium Mii Outfit A (Female)", "MrL.png"),
            [0x20] = new(0x20, "mb_mii_m", "Medium Mii Outfit B (Male)", "MrM.png"),
            [0x21] = new(0x21, "mb_mii_f", "Medium Mii Outfit B (Female)", "MrL.png"),
            [0x22] = new(0x22, "mc_mii_m", "Medium Mii Outfit C (Male)", "MrM.png"),
            [0x23] = new(0x23, "mc_mii_f", "Medium Mii Outfit C (Female)", "MrL.png"),

            [0x24] = new(0x24, "la_mii_m", "Large Mii Outfit A (Male)", "MrM.png"),
            [0x25] = new(0x25, "la_mii_f", "Large Mii Outfit A (Female)", "MrL.png"),
            [0x26] = new(0x26, "lb_mii_m", "Large Mii Outfit B (Male)", "MrM.png"),
            [0x27] = new(0x27, "lb_mii_f", "Large Mii Outfit B (Female)", "MrL.png"),
            [0x28] = new(0x28, "lc_mii_m", "Large Mii Outfit C (Male)", "MrM.png"),
            [0x29] = new(0x29, "lc_mii_f", "Large Mii Outfit C (Female)", "MrL.png"),

            [0x2A] = new(0x2A, "m_mii", "Medium Mii", "MrM.png"),
            [0x2B] = new(0x2B, "s_mii", "Small Mii", "MrM.png"),
            [0x2C] = new(0x2C, "l_mii", "Large Mii", "MrM.png"),

            // Menu-only identifiers. Not race character IDs.
            [0x2D] = new(0x2D, "pc_menu", "Peach (Menu)", "Peach.png"),
            [0x2E] = new(0x2E, "ds_menu", "Daisy (Menu)", "Daisy.png"),
            [0x2F] = new(0x2F, "rs_menu", "Rosalina (Menu)", "Rosalina.png"),
        };
}