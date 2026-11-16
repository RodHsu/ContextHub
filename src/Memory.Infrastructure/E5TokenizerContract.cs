using System.Text.Json;
using System.Text.RegularExpressions;

namespace Memory.Infrastructure;

/// <summary>Versioned vocabulary IDs; legacy and corrected vectors never share a model key.</summary>
public static class E5TokenizerContract
{
    public const string Legacy = "sentencepiece-raw-v1";
    public const string Mapped = "sentencepiece-vocab-id-v2";
    public const string MappedModelKeySuffix = ":sentencepiece-vocab-id-v2";

    public static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? Legacy : value.Trim() switch
        {
            Legacy => Legacy,
            Mapped => Mapped,
            _ => throw new InvalidOperationException("Unsupported embedding tokenizer contract.")
        };

    public static string ResolveModelKey(string defaultKey, string? configuredKey, string contract,
        string? assetBundleSha256 = null, int dimensions = 0, int maxTokens = 0)
    {
        var mapped = Normalize(contract) == Mapped;
        var suffix = mapped
            ? $":assets:{E5AssetBundle.NormalizeSha256(assetBundleSha256)}:d{dimensions}:t{maxTokens}{MappedModelKeySuffix}"
            : string.Empty;
        if (mapped && (dimensions <= 0 || maxTokens < 2))
            throw new InvalidOperationException("Corrected E5 model contract requires its dimensions and token limit.");
        var key = string.IsNullOrWhiteSpace(configuredKey)
            ? defaultKey + suffix : configuredKey.Trim();
        if (mapped ? !key.EndsWith(suffix, StringComparison.Ordinal) : key.EndsWith(MappedModelKeySuffix, StringComparison.Ordinal))
            throw new InvalidOperationException("Embedding model key must match its tokenizer contract; use a separate versioned key.");
        return key;
    }
}

public sealed record E5EncodedToken(string Piece, int RawId);

public sealed class E5TokenizerVocabulary
{
    private readonly IReadOnlyDictionary<string, int> _pieces;
    private readonly IReadOnlyDictionary<string, SpecialToken> _specialRules;
    private readonly Regex _specialPattern;
    private static readonly Regex RepeatedSpaces = new(" {2,}", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    private E5TokenizerVocabulary(IReadOnlyDictionary<string, int> pieces, IReadOnlyDictionary<string, int> specialTokens,
        IReadOnlyDictionary<string, SpecialToken> specialRules, byte[] characterMap)
    {
        _pieces = pieces;
        SpecialTokens = specialTokens;
        _specialRules = specialRules;
        CharacterMap = characterMap;
        _specialPattern = new(string.Join('|', specialRules.Keys.OrderByDescending(key => key.Length).Select(Regex.Escape)),
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    }

    public IReadOnlyDictionary<string, int> SpecialTokens { get; }
    public int PaddingId => 1;
    public byte[] CharacterMap { get; }

    public static E5TokenizerVocabulary Read(JsonElement root)
    {
        var model = root.GetProperty("model");
        if (model.GetProperty("type").GetString() != "Unigram")
            throw new InvalidOperationException("Unsupported E5 tokenizer vocabulary type.");
        var normalizer = root.GetProperty("normalizer");
        var steps = normalizer.GetProperty("normalizers").EnumerateArray().ToArray();
        var preTokenizer = root.GetProperty("pre_tokenizer");
        if (normalizer.GetProperty("type").GetString() != "Sequence" || steps.Length != 2 ||
            steps[0].GetProperty("type").GetString() != "Precompiled" ||
            steps[1].GetProperty("type").GetString() != "Replace" ||
            steps[1].GetProperty("pattern").GetProperty("Regex").GetString() != " {2,}" ||
            steps[1].GetProperty("content").GetString() != " " ||
            preTokenizer.GetProperty("type").GetString() != "Metaspace" ||
            preTokenizer.GetProperty("replacement").GetString() != "▁" ||
            !preTokenizer.GetProperty("add_prefix_space").GetBoolean())
            throw new InvalidOperationException("Unsupported E5 tokenizer normalization pipeline.");
        var characterMap = Convert.FromBase64String(steps[0].GetProperty("precompiled_charsmap").GetString()!);
        var pieces = model.GetProperty("vocab").EnumerateArray()
            .Select((entry, id) => (Piece: entry[0].GetString()!, Id: id))
            .ToDictionary(x => x.Piece, x => x.Id, StringComparer.Ordinal);
        if (pieces.GetValueOrDefault("<s>", -1) != 0 || pieces.GetValueOrDefault("<pad>", -1) != 1 ||
            pieces.GetValueOrDefault("</s>", -1) != 2 || pieces.GetValueOrDefault("<unk>", -1) != 3 ||
            pieces.GetValueOrDefault("<mask>", -1) != pieces.Count - 1)
            throw new InvalidOperationException("E5 tokenizer special IDs do not match the versioned vocabulary contract.");
        var specials = new Dictionary<string, int>(StringComparer.Ordinal);
        var specialRules = new Dictionary<string, SpecialToken>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("added_tokens").EnumerateArray().Where(x => x.GetProperty("special").GetBoolean()))
        {
            var piece = entry.GetProperty("content").GetString()!;
            var id = entry.GetProperty("id").GetInt32();
            if (!pieces.TryGetValue(piece, out var expected) || id != expected || !specials.TryAdd(piece, id))
                throw new InvalidOperationException("E5 added token IDs conflict with its model vocabulary.");
            if (entry.GetProperty("normalized").GetBoolean() || entry.GetProperty("single_word").GetBoolean())
                throw new InvalidOperationException("Unsupported E5 added token matching policy.");
            specialRules.Add(piece, new(id, entry.GetProperty("lstrip").GetBoolean(), entry.GetProperty("rstrip").GetBoolean()));
        }
        return new(pieces, specials, specialRules, characterMap);
    }

    public IReadOnlyList<E5EncodedToken> EncodePieces(string text, Func<string, string> normalize,
        Func<string, IEnumerable<E5EncodedToken>> encode)
    {
        var tokens = new List<E5EncodedToken> { new("<s>", 1) };
        var start = 0;
        foreach (Match match in _specialPattern.Matches(text))
        {
            var rule = _specialRules[match.Value];
            var fragment = text[start..match.Index];
            Append(rule.LeftStrip ? fragment.TrimEnd() : fragment);
            tokens.Add(new(match.Value, rule.Id));
            start = match.Index + match.Length;
            if (rule.RightStrip)
                while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        }
        Append(text[start..]);
        tokens.Add(new("</s>", 2));
        return tokens;

        void Append(string fragment)
        {
            if (fragment.Length == 0) return;
            var normalized = RepeatedSpaces.Replace(normalize(fragment), " ").Replace(' ', '▁');
            if (normalized.Length == 0) return;
            if (normalized[0] != '▁') normalized = "▁" + normalized;
            tokens.AddRange(encode(normalized));
        }
    }

    public long[] PrepareIds(IReadOnlyList<E5EncodedToken> pieces, int maxTokens)
    {
        if (maxTokens < 2 || pieces.Count < 2)
            throw new InvalidOperationException("E5 input must have room for both boundary tokens.");
        var ids = pieces.Select(piece => _pieces.TryGetValue(piece.Piece, out var id) ? (long)id
            : piece.RawId == 0 ? 3L
            : throw new InvalidOperationException("Tokenizer produced a piece absent from its declared model vocabulary.")).ToArray();
        if (ids[0] != 0 || ids[^1] != 2)
            throw new InvalidOperationException("E5 input boundary tokens do not match the tokenizer contract.");
        if (ids.Length <= maxTokens) return ids;
        var truncated = ids.Take(maxTokens).ToArray();
        truncated[^1] = 2;
        return truncated;
    }

    private sealed record SpecialToken(int Id, bool LeftStrip, bool RightStrip);
}
