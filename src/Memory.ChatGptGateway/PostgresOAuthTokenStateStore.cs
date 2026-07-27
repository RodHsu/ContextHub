using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Memory.ChatGptGateway;

/// <summary>
/// Persists one-time OAuth state so gateway or Redis restarts do not invalidate ChatGPT connections.
/// Token values are never stored; only their SHA-256 hashes are used as lookup keys.
/// </summary>
internal sealed class PostgresOAuthTokenStateStore(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task SetAuthorizationCodeAsync(string code, AuthorizationCodePayload payload, TimeSpan lifetime, CancellationToken cancellationToken)
        => SetAsync("authorization_code", code, payload, lifetime, cancellationToken);

    public Task<AuthorizationCodePayload?> TakeAuthorizationCodeAsync(string code, string clientId, CancellationToken cancellationToken)
        => TakeAsync("authorization_code", code, clientId, cancellationToken);

    public Task SetRefreshTokenAsync(string refreshToken, AuthorizationCodePayload payload, TimeSpan lifetime, CancellationToken cancellationToken)
        => SetAsync("refresh_token", refreshToken, payload, lifetime, cancellationToken);

    public Task<AuthorizationCodePayload?> TakeRefreshTokenAsync(string refreshToken, string clientId, CancellationToken cancellationToken)
        => TakeAsync("refresh_token", refreshToken, clientId, cancellationToken);

    private async Task SetAsync(
        string tokenKind,
        string token,
        AuthorizationCodePayload payload,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.CommandText = "DELETE FROM chatgpt_oauth_token_state WHERE expires_at <= NOW();";
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO chatgpt_oauth_token_state
                (token_hash, token_kind, client_id, payload_json, expires_at)
            VALUES (@tokenHash, @tokenKind, @clientId, CAST(@payloadJson AS jsonb), @expiresAt);
            """;
        command.Parameters.AddWithValue("tokenHash", TokenHash(token));
        command.Parameters.AddWithValue("tokenKind", tokenKind);
        command.Parameters.AddWithValue("clientId", payload.ClientId);
        command.Parameters.AddWithValue("payloadJson", JsonSerializer.Serialize(payload, SerializerOptions));
        command.Parameters.AddWithValue("expiresAt", DateTimeOffset.UtcNow.Add(lifetime));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<AuthorizationCodePayload?> TakeAsync(
        string tokenKind,
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM chatgpt_oauth_token_state
            WHERE token_hash = @tokenHash
              AND token_kind = @tokenKind
              AND client_id = @clientId
              AND expires_at > NOW()
            RETURNING payload_json::text;
            """;
        command.Parameters.AddWithValue("tokenHash", TokenHash(token));
        command.Parameters.AddWithValue("tokenKind", tokenKind);
        command.Parameters.AddWithValue("clientId", clientId);
        var payloadJson = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.IsNullOrWhiteSpace(payloadJson)
            ? null
            : JsonSerializer.Deserialize<AuthorizationCodePayload>(payloadJson, SerializerOptions);
    }

    private static byte[] TokenHash(string token)
        => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
