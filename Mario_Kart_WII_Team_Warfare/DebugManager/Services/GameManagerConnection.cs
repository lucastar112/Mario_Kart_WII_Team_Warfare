using GameManager.Shared.Client;
using System.Text.Json;

namespace DebugManager.Services;

public sealed class GameManagerConnection : IAsyncDisposable
{
    private readonly GameManagerClient _client = new();
    private List<Task<JsonElement>> _dataHolder = [];

    /// <summary>
    /// Register to GameManager
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(cancellationToken);
        await _client.RegisterAsync("DebugManager", cancellationToken);
        Console.WriteLine("DebugManager registered with GameManager.");
    }

    /// <summary>
    /// Get a ressource from the game
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="resource"></param>
    /// <param name="playerId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<T?> GetAsync<T>(string resource, int? playerId = null, CancellationToken cancellationToken = default)
    {
        return _client.GetAsync<T>(resource, playerId, cancellationToken);
    }

    /// <summary>
    /// Return's the player id for every player
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<JsonElement> GetPlayersIdAsync(CancellationToken cancellationToken = default)
    {
        return _client.GetAsync<JsonElement>("PlayersId", cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Return's the team id for every player
    /// </summary>
    /// <param name="numberOfPlayers"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public List<Task<JsonElement>> GetAllPlayersTeamIdAsync(int numberOfPlayers, CancellationToken cancellationToken = default)
    {
        _dataHolder.Clear();

        for (int i = 0; i < numberOfPlayers; i++)
            _dataHolder.Add(_client.GetAsync<JsonElement>("TeamId", playerId:i, cancellationToken: cancellationToken));

        return _dataHolder;
    }

    /// <summary>
    /// Return's the vehicle id for every player
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<JsonElement> GetVehiclesIdAsync(CancellationToken cancellationToken = default)
    {
        return _client.GetAsync<JsonElement>("VehiclesId", cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Disposes the connection
    /// </summary>
    /// <returns></returns>
    public ValueTask DisposeAsync()
    {
        return _client.DisposeAsync();
    }
}