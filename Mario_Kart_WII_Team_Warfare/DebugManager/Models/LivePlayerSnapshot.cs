using System.Windows.Media;

namespace DebugManager.Models;

public sealed class LivePlayerSnapshot
{
    public int PlayerId { get; init; }
    public uint CharacterId { get; init; }
    public string Character { get; init; } = "Unknown";
    public ImageSource? CharacterImage { get; init; }
    public int Position { get; init; }
    public float Completion { get; init; }
    public int CurrentLap { get; init; }
    public int MaxLap { get; init; }
    public float Speed { get; init; }
    public int DriftState { get; init; }
    public int MtCharge { get; init; }
    public int SmtCharge { get; init; }
    public int StarTimer { get; init; }
    public int MegaTimer { get; init; }
    public int ShockTimer { get; init; }
    public int CrushTimer { get; init; }
    public int RespawnState { get; init; }
    public int PreRespawnTimer { get; init; }
    public int SolidOutOfBoundsTimer { get; init; }
    public ushort BoostTypes { get; init; }
    public int PlayerType { get; init; }

    public string DriftText => DriftState switch { 1 => "Charging MT", 2 => "MT ready", 3 => "SMT ready", _ => "None" };
    public string EffectText
    {
        get
        {
            if (StarTimer > 0) return "Star";
            if (MegaTimer > 0) return "Mega";
            if ((BoostTypes & (1 << 3)) != 0) return "Bullet? (unverified flag)";
            if (ShockTimer > 0) return "Small / shocked?";
            if (CrushTimer > 0) return "Crushed";
            if (RespawnState != 0 || PreRespawnTimer > 0 || SolidOutOfBoundsTimer > 0) return "Falling / respawning";
            return "Normal";
        }
    }
    public string PlayerTypeText => PlayerType switch { 0 => "Local", 1 => "CPU", 2 => "Unknown 2", 3 => "Ghost", 4 => "Online", 5 => "None", _ => $"Type {PlayerType}" };
    // RaceinfoPlayer::position is 1-based in the supplied dump (1..12).
    public string PositionText => Position >= 1 && Position <= 12 ? Position.ToString() : "--";
    public string LapText => MaxLap > 0 ? $"{CurrentLap}/{MaxLap}" : "--";
    public string SpeedText => float.IsFinite(Speed) ? Speed.ToString("0.00") : "--";
    public string CompletionText => float.IsFinite(Completion) ? Completion.ToString("0.000") : "--";
}
