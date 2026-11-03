using DebugManager.Services;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace DebugManager.Pages;

public partial class ItemsPage : Page
{
    private readonly MainWindow _mainWindow;
    private CancellationTokenSource? _cancellation;
    public ObservableCollection<PacketRow> Packets { get; } = [];
    public ObservableCollection<BehaviourRow> Behaviours { get; } = [];

    public ItemsPage(MainWindow mainWindow)
    {
        InitializeComponent(); _mainWindow = mainWindow;
        PacketGrid.ItemsSource = Packets; BehaviourGrid.ItemsSource = Behaviours;
        Loaded += OnLoaded; Unloaded += (_, _) => _cancellation?.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = new();
        try { await MonitorAsync(_cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        await using GameManagerConnection connection = new(); await connection.ConnectAsync(token);
        LoadBehaviourTable(await connection.GetAsync<JsonElement>("ItemBehaviourTableRaw", cancellationToken: token));
        try
        {
            JsonElement slotData = await connection.GetAsync<JsonElement>("ItemSlotDataRaw", 0, token);
            JsonElement playerCount = await connection.GetAsync<JsonElement>("ItemSlotDataPlayerCount", 0, token);
            JsonElement timers = await connection.GetAsync<JsonElement>("ItemSlotDataSpawnTimersRaw", 0, token);
            byte[] slotBytes = ReadBytes(slotData);
            byte[] timerBytes = ReadBytes(timers);
            string timerText = timerBytes.Length >= 16
                ? string.Join(" / ", Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadUInt32BigEndian(timerBytes.AsSpan(i * 4, 4))))
                : "unavailable";
            string tableSummary = slotBytes.Length >= 0x30
                ? $"Player chances: {BinaryPrimitives.ReadUInt32BigEndian(slotBytes.AsSpan(0x10, 4))} cols @ 0x{BinaryPrimitives.ReadUInt32BigEndian(slotBytes.AsSpan(0x14, 4)):X8} · " +
                  $"CPU: {BinaryPrimitives.ReadUInt32BigEndian(slotBytes.AsSpan(0x18, 4))} cols @ 0x{BinaryPrimitives.ReadUInt32BigEndian(slotBytes.AsSpan(0x1C, 4)):X8} · " +
                  $"Special: {BinaryPrimitives.ReadUInt32BigEndian(slotBytes.AsSpan(0x20, 4))} cols @ 0x{BinaryPrimitives.ReadUInt32BigEndian(slotBytes.AsSpan(0x24, 4)):X8} · " +
                  $"Items wheel @ 0x{BinaryPrimitives.ReadUInt32BigEndian(slotBytes.AsSpan(0x28, 4)):X8} · Special wheel @ 0x{BinaryPrimitives.ReadUInt32BigEndian(slotBytes.AsSpan(0x2C, 4)):X8}"
                : "table pointers unavailable";
            ItemSlotSummary.Text = $"{tableSummary} · Player count: {ReadUnsigned(playerCount)} · Spawn timers (frames; Lightning / Blue Shell / Blooper / POW): {timerText}";
        }
        catch (Exception ex) { ItemSlotSummary.Text = "ItemSlotData unavailable: " + ex.Message; }
        while (!token.IsCancellationRequested)
        {
            Packets.Clear();
            for (int slot = 0; slot < 12; slot++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    JsonElement raw = await connection.GetAsync<JsonElement>("ItemPacketRaw", slot, token);
                    JsonElement timer = await connection.GetAsync<JsonElement>("ItemHandlerPlayerTime", slot, token);
                    byte[] bytes = ReadBytes(raw);
                    Packets.Add(new PacketRow(slot + 1, Convert.ToHexString(bytes), ReadUnsigned(timer)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Packets.Add(new PacketRow(slot + 1, "Unavailable: " + ex.Message, "--"));
                }
            }
            StatusText.Text = $"{Packets.Count}/12 packet slots · raw decoding";
            await Task.Delay(750, token);
        }
    }

    private void LoadBehaviourTable(JsonElement value)
    {
        byte[] bytes = ReadBytes(value);
        Behaviours.Clear();
        const int stride = 0x1C;
        if (bytes.Length < stride * 19) { StatusText.Text = $"Behaviour table too short ({bytes.Length} bytes)"; return; }
        for (int i = 0; i < 19; i++)
        {
            int offset = i * stride;
            uint objectId = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 0x04, 4));
            uint numberOfItems = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 0x08, 4));
            uint useType = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 0x14, 4));
            uint useFunction = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 0x18, 4));
            Behaviours.Add(new BehaviourRow(i, $"0x{objectId:X8}", numberOfItems, useType, $"0x{useFunction:X8}"));
        }
    }

    private static byte[] ReadBytes(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) return [];
            try { return Convert.FromBase64String(text); }
            catch { try { return Convert.FromHexString(text); } catch { return []; } }
        }
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray();
        return [];
    }

    private static string ReadUnsigned(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint n)) return n.ToString();
        return value.ToString();
    }

    private void Back_Click(object sender, RoutedEventArgs e) { _cancellation?.Cancel(); _mainWindow.ShowMainMenu(); }

    public sealed record PacketRow(int Slot, string Hex, string Timer);
    public sealed record BehaviourRow(int Index, string ObjectId, uint NumberOfItems, uint UseType, string UseFunction);
}
