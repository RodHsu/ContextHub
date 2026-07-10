using System.Text.Json;
using Npgsql;

namespace Memory.ChatGptGateway;

internal sealed class PostgresOAuthClientStore(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task UpsertAsync(RegisteredOAuthClient client, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO chatgpt_oauth_clients
                (client_id, redirect_uris, token_endpoint_auth_method, grant_types, response_types, registered_at, expires_at)
            VALUES (@clientId, @redirectUris, @tokenEndpointAuthMethod, @grantTypes, @responseTypes, @registeredAt, @expiresAt)
            ON CONFLICT (client_id) DO UPDATE SET
                redirect_uris = EXCLUDED.redirect_uris,
                token_endpoint_auth_method = EXCLUDED.token_endpoint_auth_method,
                grant_types = EXCLUDED.grant_types,
                response_types = EXCLUDED.response_types,
                registered_at = EXCLUDED.registered_at,
                expires_at = EXCLUDED.expires_at;
            """;
        command.Parameters.AddWithValue("clientId", client.ClientId);
        command.Parameters.AddWithValue("redirectUris", client.RedirectUris.ToArray());
        command.Parameters.AddWithValue("tokenEndpointAuthMethod", client.TokenEndpointAuthMethod);
        command.Parameters.AddWithValue("grantTypes", client.GrantTypes.ToArray());
        command.Parameters.AddWithValue("responseTypes", client.ResponseTypes.ToArray());
        command.Parameters.AddWithValue("registeredAt", client.RegisteredAt);
        command.Parameters.AddWithValue("expiresAt", expiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RegisteredOAuthClient?> GetAsync(string clientId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT client_id, redirect_uris, token_endpoint_auth_method, grant_types, response_types, registered_at
            FROM chatgpt_oauth_clients
            WHERE client_id = @clientId AND expires_at > NOW();
            """;
        command.Parameters.AddWithValue("clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RegisteredOAuthClient(
                reader.GetString(0), reader.GetFieldValue<string[]>(1), reader.GetString(2),
                reader.GetFieldValue<string[]>(3), reader.GetFieldValue<string[]>(4), reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }
}
