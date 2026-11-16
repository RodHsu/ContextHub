using System.Text.Json;
using FluentAssertions;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class E5TokenizerContractTests
{
    [Fact]
    public async Task Bundle_identity_is_order_independent_and_changes_when_asset_bytes_change()
    {
        var directory = Path.Combine(Path.GetTempPath(), "contexthub-asset-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "model.bin"), "synthetic model A");
            await File.WriteAllTextAsync(Path.Combine(directory, "tokenizer.json"), "synthetic tokenizer");
            EmbeddingAssetFile[] assets = [new("model.bin", "model.bin"), new("tokenizer.json", "tokenizer.json")];
            var before = await E5AssetBundle.ComputeAsync(directory, assets, default);
            (await E5AssetBundle.ComputeAsync(directory, assets.Reverse().ToArray(), default)).Should().Be(before);
            await File.WriteAllTextAsync(Path.Combine(directory, "model.bin"), "synthetic model B");
            (await E5AssetBundle.ComputeAsync(directory, assets, default)).Should().NotBe(before);
        }
        finally
        {
            // This exact, newly created fixture directory is owned exclusively by this test.
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Corrected_profile_requires_its_own_model_key_and_legacy_cannot_use_it()
    {
        var legacy = EmbeddingProfileResolver.Resolve(new());
        var bundle = new string('a', 64);
        var mapped = EmbeddingProfileResolver.Resolve(new() { TokenizerContract = E5TokenizerContract.Mapped, AssetBundleSha256 = bundle });
        legacy.TokenizerContract.Should().Be(E5TokenizerContract.Legacy);
        mapped.ModelKey.Should().Be(legacy.ModelKey + $":assets:{bundle}:d384:t512" + E5TokenizerContract.MappedModelKeySuffix);
        mapped.Dimensions.Should().Be(legacy.Dimensions);
        var mixOld = () => EmbeddingProfileResolver.Resolve(new() { TokenizerContract = E5TokenizerContract.Mapped, AssetBundleSha256 = bundle, ModelKey = legacy.ModelKey });
        var mixNew = () => EmbeddingProfileResolver.Resolve(new() { ModelKey = mapped.ModelKey });
        mixOld.Should().Throw<InvalidOperationException>();
        mixNew.Should().Throw<InvalidOperationException>();
        var unpinned = () => EmbeddingProfileResolver.Resolve(new() { TokenizerContract = E5TokenizerContract.Mapped });
        unpinned.Should().Throw<InvalidOperationException>();
        EmbeddingProfileResolver.Resolve(new() { TokenizerContract = E5TokenizerContract.Mapped, AssetBundleSha256 = bundle, MaxTokens = 256 })
            .ModelKey.Should().NotBe(mapped.ModelKey);
    }

    [Fact]
    public void Vocabulary_ids_padding_unknown_and_truncated_eos_follow_the_declared_contract()
    {
        var vocabulary = Vocabulary();
        E5EncodedToken[] pieces = [new("<s>", 1), new("hello", 3), new("world", 4), new("</s>", 2)];
        vocabulary.PrepareIds(pieces, 512).Should().Equal(0, 4, 5, 2);
        vocabulary.PrepareIds(pieces, 3).Should().Equal(0, 4, 2);
        vocabulary.PrepareIds([new("<s>", 1), new("unknown-rune", 0), new("</s>", 2)], 512).Should().Equal(0, 3, 2);
        vocabulary.PaddingId.Should().Be(1);
        var unknownNonzero = () => vocabulary.PrepareIds([new("<s>", 1), new("missing", 999), new("</s>", 2)], 512);
        unknownNonzero.Should().Throw<InvalidOperationException>();
        var noBoundaryRoom = () => vocabulary.PrepareIds(pieces, 1);
        noBoundaryRoom.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Conflicting_asset_special_ids_are_rejected()
    {
        var invalid = () => Vocabulary(specialId: 999);
        invalid.Should().Throw<InvalidOperationException>();
        var invalidContract = () => EmbeddingProfileResolver.Resolve(new() { TokenizerContract = "unknown" });
        invalidContract.Should().Throw<InvalidOperationException>();
    }

    private static E5TokenizerVocabulary Vocabulary(int specialId = 0)
    {
        using var document = JsonDocument.Parse($$"""
            {"model":{"type":"Unigram","vocab":[["<s>",0],["<pad>",0],["</s>",0],["<unk>",0],["hello",0],["world",0],["<mask>",0]]},
             "normalizer":{"type":"Sequence","normalizers":[{"type":"Precompiled","precompiled_charsmap":"QUJD"},{"type":"Replace","pattern":{"Regex":" {2,}"},"content":" "}]},
             "pre_tokenizer":{"type":"Metaspace","replacement":"▁","add_prefix_space":true},
             "added_tokens":[{"content":"<s>","id":{{specialId}},"special":true,"normalized":false,"single_word":false,"lstrip":false,"rstrip":false},
                 {"content":"<pad>","id":1,"special":true,"normalized":false,"single_word":false,"lstrip":false,"rstrip":false},
                 {"content":"</s>","id":2,"special":true,"normalized":false,"single_word":false,"lstrip":false,"rstrip":false},
                 {"content":"<unk>","id":3,"special":true,"normalized":false,"single_word":false,"lstrip":false,"rstrip":false},
                 {"content":"<mask>","id":6,"special":true,"normalized":false,"single_word":false,"lstrip":true,"rstrip":false}]}
            """);
        return E5TokenizerVocabulary.Read(document.RootElement);
    }

    [Fact]
    public void Special_token_strip_policy_preserves_space_before_other_tokens_and_removes_it_before_mask()
    {
        var fragments = new List<string>();
        var vocabulary = Vocabulary();
        var tokens = vocabulary.EncodePieces("hello  <s>world</s> <pad> <mask>", text => text, text =>
        {
            fragments.Add(text);
            return Array.Empty<E5EncodedToken>();
        });
        fragments.Should().Equal("▁hello▁", "▁world", "▁");
        tokens.Select(token => token.Piece).Should().Equal("<s>", "<s>", "</s>", "<pad>", "<mask>", "</s>");
    }

    [Fact]
    public void Model_adapter_changes_only_whitespace_flags_and_preserves_vocabulary_and_unknown_fields()
    {
        byte[] model = [10, 3, 10, 1, 65, 26, 11, 18, 3, 65, 66, 67, 24, 1, 32, 1, 40, 1, 240, 6, 7];
        byte[] expected = [10, 3, 10, 1, 65, 26, 11, 18, 3, 65, 66, 67, 24, 0, 32, 0, 40, 0, 240, 6, 7];
        E5SentencePieceModel.ForDeclaredNormalizer(model, [65, 66, 67]).Should().Equal(expected);
        model[13].Should().Be(1, "the pinned source asset is never changed");
        var wrongMap = () => E5SentencePieceModel.ForDeclaredNormalizer(model, [0]);
        wrongMap.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(new byte[] { 26, 128 })]
    [InlineData(new byte[] { 26, 5, 18, 10, 65 })]
    [InlineData(new byte[] { 0 })]
    public void Malformed_model_frames_are_rejected(byte[] model)
    {
        var transform = () => E5SentencePieceModel.ForDeclaredNormalizer(model, [65]);
        transform.Should().Throw<InvalidOperationException>();
    }
}
