using System;
using System.Collections.Generic;
using System.Linq;

namespace DebugManager.Services.TrackFormats;

/// <summary>
/// Decodes the CKPT and CKPH sections of a Mario Kart Wii KMP.
/// CKPT coordinates are X/Z pairs (the game world is X/Y/Z, with Y vertical).
/// </summary>
public static class KmpCheckpointDecoder
{
    public static KmpCheckpointGraph Decode(KmpDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        KmpSection? checkpointSection = document.FindSection("CKPT");
        KmpSection? groupSection = document.FindSection("CKPH");
        if (checkpointSection is null || groupSection is null)
            return new KmpCheckpointGraph(Array.Empty<KmpCheckpoint>(), Array.Empty<KmpCheckpointGroup>());

        if (checkpointSection.FixedEntrySize != 0x14 || checkpointSection.Entries.Count != checkpointSection.EntryCount)
            throw new InvalidOperationException("CKPT section was not parsed as 0x14-byte records.");
        if (groupSection.FixedEntrySize != 0x10 || groupSection.Entries.Count != groupSection.EntryCount)
            throw new InvalidOperationException("CKPH section was not parsed as 0x10-byte records.");

        var checkpoints = new List<KmpCheckpoint>(checkpointSection.EntryCount);
        for (int i = 0; i < checkpointSection.Entries.Count; i++)
        {
            byte[] entry = checkpointSection.Entries[i];
            checkpoints.Add(new KmpCheckpoint(
                Index: i,
                LeftX: KmpEntryDecoder.ReadFloat(entry, 0x00),
                LeftZ: KmpEntryDecoder.ReadFloat(entry, 0x04),
                RightX: KmpEntryDecoder.ReadFloat(entry, 0x08),
                RightZ: KmpEntryDecoder.ReadFloat(entry, 0x0C),
                RespawnIndex: entry[0x10],
                Type: entry[0x11],
                PreviousCheckpoint: entry[0x12],
                NextCheckpoint: entry[0x13]));
        }

        var groups = new List<KmpCheckpointGroup>(groupSection.EntryCount);
        for (int i = 0; i < groupSection.Entries.Count; i++)
        {
            byte[] entry = groupSection.Entries[i];
            groups.Add(new KmpCheckpointGroup(
                Index: i,
                FirstCheckpoint: entry[0],
                CheckpointCount: entry[1],
                PreviousGroups: entry.AsSpan(0x02, 6).ToArray(),
                NextGroups: entry.AsSpan(0x08, 6).ToArray(),
                Padding: KmpEntryDecoder.ReadUInt16(entry, 0x0E)));
        }

        ValidateGroups(checkpoints.Count, groups);
        return new KmpCheckpointGraph(checkpoints, groups);
    }

    private static void ValidateGroups(int checkpointCount, IReadOnlyList<KmpCheckpointGroup> groups)
    {
        foreach (KmpCheckpointGroup group in groups)
        {
            if (group.CheckpointCount == 0)
                throw new InvalidOperationException($"CKPH group {group.Index} has no checkpoints.");
            if (group.FirstCheckpoint + group.CheckpointCount > checkpointCount)
                throw new InvalidOperationException(
                    $"CKPH group {group.Index} references checkpoints outside CKPT ({group.FirstCheckpoint}+{group.CheckpointCount}>{checkpointCount}).");

            foreach (byte link in group.PreviousGroups.Concat(group.NextGroups))
            {
                if (link != 0xFF && link >= groups.Count)
                    throw new InvalidOperationException($"CKPH group {group.Index} references missing group {link}.");
            }
        }
    }
}

public sealed record KmpCheckpoint(
    int Index,
    float LeftX,
    float LeftZ,
    float RightX,
    float RightZ,
    byte RespawnIndex,
    byte Type,
    byte PreviousCheckpoint,
    byte NextCheckpoint)
{
    public double CenterX => (LeftX + RightX) * 0.5;
    public double CenterZ => (LeftZ + RightZ) * 0.5;
    public bool IsLapCounter => Type == 0;
    public bool IsKeyCheckpoint => Type is > 0 and < 0xFF;
    public bool IsNormal => Type == 0xFF;
}

public sealed record KmpCheckpointGroup(
    int Index,
    byte FirstCheckpoint,
    byte CheckpointCount,
    byte[] PreviousGroups,
    byte[] NextGroups,
    ushort Padding)
{
    public int LastCheckpoint => FirstCheckpoint + CheckpointCount - 1;
}

public sealed class KmpCheckpointGraph
{
    public IReadOnlyList<KmpCheckpoint> Checkpoints { get; }
    public IReadOnlyList<KmpCheckpointGroup> Groups { get; }

    public KmpCheckpointGraph(IReadOnlyList<KmpCheckpoint> checkpoints, IReadOnlyList<KmpCheckpointGroup> groups)
    {
        Checkpoints = checkpoints;
        Groups = groups;
    }

    /// <summary>
    /// Returns directed checkpoint-to-checkpoint route edges. Intra-group edges
    /// come from CKPT.next; inter-group edges come from CKPH.next.
    /// </summary>
    public IReadOnlyList<(int From, int To)> BuildRouteEdges()
    {
        var edges = new HashSet<(int From, int To)>();
        foreach (KmpCheckpoint checkpoint in Checkpoints)
        {
            if (checkpoint.NextCheckpoint != 0xFF && checkpoint.NextCheckpoint < Checkpoints.Count)
                edges.Add((checkpoint.Index, checkpoint.NextCheckpoint));
        }

        foreach (KmpCheckpointGroup group in Groups)
        {
            foreach (byte nextGroupIndex in group.NextGroups)
            {
                if (nextGroupIndex == 0xFF || nextGroupIndex >= Groups.Count)
                    continue;
                KmpCheckpointGroup nextGroup = Groups[nextGroupIndex];
                if (group.LastCheckpoint < Checkpoints.Count && nextGroup.FirstCheckpoint < Checkpoints.Count)
                    edges.Add((group.LastCheckpoint, nextGroup.FirstCheckpoint));
            }
        }

        return edges.OrderBy(edge => edge.From).ThenBy(edge => edge.To).ToArray();
    }
}
