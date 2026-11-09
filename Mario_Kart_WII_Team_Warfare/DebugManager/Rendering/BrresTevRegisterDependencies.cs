using System;
using System.Collections.Generic;
using System.Linq;
using DebugManager.Services.TrackFormats;

namespace DebugManager.Rendering;

/// <summary>
/// Conservative, rendering-independent TEV register dependency analysis.
/// Tracks the four GX TEV registers (PREV, REG0, REG1, REG2) at RGBA channel
/// granularity. It does not fabricate missing BP values or change shader inputs.
///
/// Two complementary questions are answered:
/// 1. Forward: Was an initially unknown register channel read before a stage wrote it?
/// 2. Backward: Could that unknown channel influence the final PREV RGBA output?
///
/// The backward pass follows the same selector mapping as the current GLSL
/// implementation. All A/B/C/D selectors are treated as potentially active;
/// constant masks, alpha/depth testing, TEV exact integer arithmetic and
/// cross-draw GX state are not evaluated. Counts are upper bounds.
/// </summary>
internal static class BrresTevRegisterDependencies
{
    internal sealed record Result(
        int MaterialIndex,
        IReadOnlyList<string> UnknownReadBeforeWrite,
        IReadOnlyList<string> UnknownRelevantToOutput,
        int UnknownReadEvents,
        int DeadStageWrites,
        IReadOnlyList<string> StageFlow);

    private static readonly string[] RegisterNames = ["PREV", "REG0", "REG1", "REG2"];
    private static readonly char[] ChannelNames = ['R', 'G', 'B', 'A'];

    // TEV RGB input: 0..7 alternating register RGB / register A.
    // 8..15 are texture, raster, constants, KONST or ZERO, not TEV register reads.
    private static void ColorRead(int selector, int component, Action<int> add)
    {
        if (selector is < 0 or >= 8) return;
        int register = selector / 2;
        if ((selector & 1) != 0) add(register * 4 + 3);
        else add(register * 4 + component);
    }

    // TEV alpha input: 0=PREV.a, 1=REG0.a, 2=REG1.a, 3=REG2.a.
    // 4..7 are texture, raster, KONST or ZERO.
    private static void AlphaRead(int selector, Action<int> add)
    {
        if (selector is >= 0 and <= 3) add(selector * 4 + 3);
    }

    private static IEnumerable<int> ColorInputs(BrresTevDiagnostics.Stage stage, int component)
    {
        var reads = new HashSet<int>();
        ColorRead(stage.ColorA, component, ch => { reads.Add(ch); });
        ColorRead(stage.ColorB, component, ch => { reads.Add(ch); });
        ColorRead(stage.ColorC, component, ch => { reads.Add(ch); });
        ColorRead(stage.ColorD, component, ch => { reads.Add(ch); });
        return reads;
    }

    private static IEnumerable<int> AlphaInputs(BrresTevDiagnostics.Stage stage)
    {
        var reads = new HashSet<int>();
        AlphaRead(stage.AlphaA, ch => { reads.Add(ch); });
        AlphaRead(stage.AlphaB, ch => { reads.Add(ch); });
        AlphaRead(stage.AlphaC, ch => { reads.Add(ch); });
        AlphaRead(stage.AlphaD, ch => { reads.Add(ch); });
        return reads;
    }

    private static string Label(int channel) =>
        $"{RegisterNames[channel / 4]}.{ChannelNames[channel % 4]}";

    internal static Result Analyze(int materialId,
        IReadOnlyList<BrresTevDiagnostics.Stage> stages,
        BrresTevDiagnostics.InitialRegisters? initial)
    {
        var knownInitial = new bool[16];
        if (initial is not null)
            Array.Copy(initial.Known, knownInitial, Math.Min(16, initial.Known.Length));

        // Forward: each stage reads its register inputs before writing RGB/alpha
        // to independently selected register destinations.
        var alreadyWritten = new bool[16];
        var forwardUnknown = new HashSet<int>();
        int unknownReadEvents = 0;
        var stageFlow = new List<string>(stages.Count);
        foreach (var stage in stages)
        {
            var reads = new HashSet<int>();
            for (int component = 0; component < 3; component++)
                reads.UnionWith(ColorInputs(stage, component));
            reads.UnionWith(AlphaInputs(stage));
            var unknown = reads.Where(ch => !alreadyWritten[ch] && !knownInitial[ch])
                .OrderBy(ch => ch).ToArray();
            foreach (int channel in unknown) forwardUnknown.Add(channel);
            unknownReadEvents += unknown.Length;

            if (stage.ColorDestination is >= 0 and <= 3)
                for (int c = 0; c < 3; c++) alreadyWritten[stage.ColorDestination * 4 + c] = true;
            if (stage.AlphaDestination is >= 0 and <= 3)
                alreadyWritten[stage.AlphaDestination * 4 + 3] = true;
            stageFlow.Add($"s{stage.Index}:read={reads.Count}/unknown={unknown.Length}" +
                $"/writeRGB=R{stage.ColorDestination}/writeA=R{stage.AlphaDestination}");
        }

        // Backward liveness: the fragment shader emits regs[PREV] (RGBA).
        // A stage can contribute only if a register component that it writes
        // is live when that stage finishes. All channel reads are conservative.
        var live = new HashSet<int> { 0, 1, 2, 3 };
        int deadWrites = 0;
        for (int i = stages.Count - 1; i >= 0; --i)
        {
            var stage = stages[i];
            var rgbNeeded = new bool[3];
            bool alphaNeeded = false;
            if (stage.ColorDestination is >= 0 and <= 3)
                for (int c = 0; c < 3; ++c)
                    rgbNeeded[c] = live.Remove(stage.ColorDestination * 4 + c);
            if (stage.AlphaDestination is >= 0 and <= 3)
                alphaNeeded = live.Remove(stage.AlphaDestination * 4 + 3);
            if (!rgbNeeded.Any(v => v) && !alphaNeeded) deadWrites++;
            for (int c = 0; c < 3; ++c)
                if (rgbNeeded[c]) live.UnionWith(ColorInputs(stage, c));
            if (alphaNeeded) live.UnionWith(AlphaInputs(stage));
        }
        var relevantUnknown = live.Where(ch => !knownInitial[ch]).OrderBy(ch => ch).ToArray();
        return new Result(materialId,
            forwardUnknown.OrderBy(ch => ch).Select(Label).ToArray(),
            relevantUnknown.Select(Label).ToArray(),
            unknownReadEvents, deadWrites, stageFlow);
    }
}
