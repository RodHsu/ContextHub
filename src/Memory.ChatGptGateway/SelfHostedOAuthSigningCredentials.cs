using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Memory.ChatGptGateway;

internal sealed class SelfHostedOAuthSigningCredentials(IConfiguration configuration)
{
    public RsaSecurityKey Key { get; } = new(CreateRsa(configuration["ChatGptGateway:OAuth:SelfHostedRsaPrivateKey"])) { KeyId = "contexthub-rsa-1" };
    public SigningCredentials Credentials => new(Key, SecurityAlgorithms.RsaSha256);
    public object Jwks
    {
        get
        {
            var publicParameters = Key.Rsa.ExportParameters(false);
            return new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        alg = "RS256",
                        kid = Key.KeyId,
                        n = Base64Url(publicParameters.Modulus),
                        e = Base64Url(publicParameters.Exponent)
                    }
                }
            };
        }
    }

    private static RSA CreateRsa(string? base64PrivateKey)
    {
        if (string.IsNullOrWhiteSpace(base64PrivateKey)) throw new InvalidOperationException("ChatGptGateway:OAuth:SelfHostedRsaPrivateKey is required.");
        var value = RSA.Create(); value.ImportPkcs8PrivateKey(Convert.FromBase64String(base64PrivateKey.Trim()), out _); return value;
    }

    private static string Base64Url(byte[]? value) => Convert.ToBase64String(value ?? []).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
