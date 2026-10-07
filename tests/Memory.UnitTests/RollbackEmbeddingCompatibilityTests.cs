using Memory.Application;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class RollbackEmbeddingCompatibilityTests
{
    [Theory]
    [InlineData("compact", "intfloat/multilingual-e5-small", 384)]
    [InlineData("balanced", "intfloat/multilingual-e5-base", 768)]
    public void Compatible_reader_keeps_the_existing_profile_and_asset_identity(string profile, string modelKey, int dimensions)
    {
        var resolved = EmbeddingProfileResolver.Resolve(new EmbeddingOptions { Profile = profile });

        Assert.Equal(modelKey, resolved.ModelKey);
        Assert.Equal(dimensions, resolved.Dimensions);
        Assert.Equal(512, resolved.MaxTokens);
        Assert.Equal("sentencepiece.bpe.model", resolved.TokenizerFile);
        Assert.Contains(resolved.AssetFiles, file => file.LocalPath == "sentencepiece.bpe.model");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("intfloat/multilingual-e5-small")]
    public void Replay_uses_the_configured_generation(string? requested)
    {
        Assert.Equal("intfloat/multilingual-e5-small",
            EmbeddingModelIdentity.RequireConfigured(requested, "intfloat/multilingual-e5-small"));
    }

    [Theory]
    [InlineData("intfloat/multilingual-e5-small@sentencepiece-vocab-id-v2")]
    [InlineData("INTFLOAT/MULTILINGUAL-E5-SMALL")]
    [InlineData("intfloat/multilingual-e5-base")]
    public void Replay_cannot_relabel_another_generation_as_the_legacy_profile(string requested)
    {
        Assert.Throws<InvalidOperationException>(() =>
            EmbeddingModelIdentity.RequireConfigured(requested, "intfloat/multilingual-e5-small"));
    }
}
