using GameManager.Shared.Client;
using System.Text.Json;

namespace DebugManager.Services;

public sealed class GameManagerConnection : IAsyncDisposable
{
    private readonly GameManagerClient _client = new();

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
    public Task<JsonElement> GetPlayersIdAsync(int playerId, CancellationToken cancellationToken = default)
    {
        return _client.GetAsync<JsonElement>("PlayersId", playerId, cancellationToken);
    }

    /// <summary>
    /// Return's the team id for every player
    /// </summary>
    /// <param name="numberOfPlayers"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<JsonElement> GetTeamIdAsync(int playerId, CancellationToken cancellationToken = default)
    {
        return _client.GetAsync<JsonElement>("TeamId", playerId, cancellationToken);
    }


    /// <summary>
    /// Return's the vehicle id for every player
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<JsonElement> GetVehiclesIdAsync(int playerId, CancellationToken cancellationToken = default)
    {
        return _client.GetAsync<JsonElement>("VehiclesId", playerId, cancellationToken);
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