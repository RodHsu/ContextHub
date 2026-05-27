using Memory.Application;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class RedisCachePolicy(IOptions<MemoryOptions> options) : IRedisCachePolicy
{
    private RedisCacheOptions Options => options.Value.RedisCache;

    public bool Enabled => Options.Enabled;
    public TimeSpan SearchTtl => Options.SearchTtl;
    public TimeSpan WorkingContextTtl => Options.WorkingContextTtl;
    public TimeSpan EmbeddingTtl => Options.EmbeddingTtl;
    public TimeSpan SemanticHitTtl => Options.SemanticHitTtl;
    public TimeSpan MetadataTtl => Options.MetadataTtl;
    public TimeSpan SecurityTtl => Options.SecurityTtl;
}
