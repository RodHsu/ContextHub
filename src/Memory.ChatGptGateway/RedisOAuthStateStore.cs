using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Memory.ChatGptGateway;

internal sealed class RedisOAuthStateStore(
    IConnectionMultiplexer redis,
    IOptions<Memory.Infrastructure.MemoryOptions> memoryOptions)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IDatabase database = redis.GetDatabase();
    private readonly string keyPrefix = $"memory:{memoryOptions.Value.Namespace}:chatgpt-oauth:";

    public Task SetRegisteredClientAsync(string clientId, RegisteredOAuthClient client, TimeSpan lifetime)
        => SetAsync("registered-client:", clientId, client, lifetime);

    public Task<RegisteredOAuthClient?> GetRegisteredClientAsync(string clientId)
        => GetAsync<RegisteredOAuthClient>("registered-client:", clientId);

    public Task SetAuthorizationCodeAsync(string code, AuthorizationCodePayload payload, TimeSpan lifetime)
        => SetAsync("code:", code, payload, lifetime);

    public Task<AuthorizationCodePayload?> TakeAuthorizationCodeAsync(string code)
        => TakeAsync<AuthorizationCodePayload>("code:", code);

    public Task SetRefreshTokenAsync(string refreshToken, AuthorizationCodePayload payload, TimeSpan lifetime)
        => SetAsync("refresh:", refreshToken, payload, lifetime);

    public Task<AuthorizationCodePayload?> TakeRefreshTokenAsync(string refreshToken)
        => TakeAsync<AuthorizationCodePayload>("refresh:", refreshToken);

    private async Task SetAsync<T>(string kind, string id, T value, TimeSpan lifetime)
    {
        var payload = JsonSerializer.Serialize(value, SerializerOptions);
        await database.StringSetAsync(Key(kind, id), payload, lifetime);
    }

    private async Task<T?> GetAsync<T>(string kind, string id)
    {
        var value = await database.StringGetAsync(Key(kind, id));
        return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value.ToString(), SerializerOptions);
    }

    private async Task<T?> TakeAsync<T>(string kind, string id)
    {
        var value = await database.StringGetDeleteAsync(Key(kind, id));
        return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value.ToString(), SerializerOptions);
    }

    private RedisKey Key(string kind, string id) => keyPrefix + kind + id;
}
