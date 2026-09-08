using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Memory.Domain;

namespace Memory.Application;

public static partial class PortableSkillBundleValidator
{
    public const string ValidatorVersion = "1.0";
    public const long MaximumBundleBytes = 10 * 1024 * 1024;
    public const int MaximumFileCount = 256;
    public const int MaximumReasonTextLength = 1000;
    private static readonly string[] ForbiddenExtensions = [".pfx", ".p12", ".key", ".pem"];

    public static SkillImportPreviewResult Validate(SkillImportPreviewRequest request)
    {
        var issues = new List<SkillValidationIssue>();
        var decoded = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        long totalBytes = 0;

        ValidateIdentity(request, issues);
        if (request.Bundle.Files.Count == 0)
        {
            issues.Add(new("BundleEmpty", "The portable skill bundle is empty.", "Error"));
        }

        if (request.Bundle.Files.Count > MaximumFileCount)
        {
            issues.Add(new("TooManyFiles", $"A skill bundle cannot contain more than {MaximumFileCount} files.", "Error"));
        }

        foreach (var file in request.Bundle.Files)
        {
            var path = NormalizeRelativePath(file.Path);
            if (path is null)
            {
                issues.Add(new("UnsafePath", "Bundle paths must be normalized relative paths without traversal or rooted segments.", "Error", file.Path));
                continue;
            }

            if (file.IsSymbolicLink)
            {
                issues.Add(new("SymbolicLinkForbidden", "Symbolic links are not allowed in portable skill bundles.", "Error", path));
                continue;
            }

            if (!decoded.TryAdd(path, Decode(file.ContentBase64, path, issues)))
            {
                issues.Add(new("DuplicatePath", "Bundle paths must be unique using ordinal comparison.", "Error", path));
                continue;
            }

            var bytes = decoded[path];
            totalBytes += bytes.LongLength;
            if (totalBytes > MaximumBundleBytes)
            {
                issues.Add(new("BundleTooLarge", $"The decoded bundle exceeds {MaximumBundleBytes} bytes.", "Error"));
            }

            if (ForbiddenExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new("ForbiddenSecretFile", "Private-key and certificate-secret files cannot be stored in a skill bundle.", "Error", path));
            }

            if (file.Executable && !path.StartsWith("scripts/", StringComparison.Ordinal))
            {
                issues.Add(new("ExecutableOutsideScripts", "Executable files must be contained under scripts/.", "Error", path));
            }

            if (LooksLikeSecret(bytes))
            {
                issues.Add(new("SecretPatternDetected", "The file contains a credential or private-key pattern and cannot be published.", "Error", path));
            }
        }

        if (!decoded.TryGetValue("SKILL.md", out var skillMarkdownBytes))
        {
            issues.Add(new("MissingSkillMarkdown", "The bundle must contain SKILL.md at its root.", "Error", "SKILL.md"));
            skillMarkdownBytes = [];
        }

        var skillMarkdown = DecodeUtf8(skillMarkdownBytes, "SKILL.md", issues);
        ValidateSkillMarkdown(skillMarkdown, issues);
        var selfTest = ValidateDeclarativeSelfTest(decoded, issues);
        var hash = ComputeContentHash(decoded);
        var signatureVerified = VerifySignature(request.SignatureAlgorithm, request.SignatureValue, hash);
        if (!string.IsNullOrWhiteSpace(request.SignatureValue) && !signatureVerified)
        {
            issues.Add(new("SignatureInvalid", "The supplied bundle signature or attestation did not verify against the canonical content hash.", "Error"));
        }

        var requiresApproval = request.RiskLevel is SkillRiskLevel.High or SkillRiskLevel.Critical ||
                               request.RequiresNetwork ||
                               request.RequiresSecrets ||
                               request.Bundle.Files.Any(file => file.Executable);
        var checks = new List<string>
        {
            "portable-layout",
            "path-traversal",
            "symlink",
            "bundle-size",
            "secret-pattern",
            "frontmatter",
            "content-hash",
            "capability-declaration",
            "dependency-declaration",
            "provenance-license"
        };
        if (selfTest.Executed)
        {
            checks.Add("declarative-self-test");
        }

        var validation = new SkillPublishEvidenceResult(
            ValidatorVersion,
            hash,
            issues.All(issue => !string.Equals(issue.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
            selfTest.Executed,
            selfTest.Passed,
            signatureVerified,
            checks,
            issues);

        return new(
            NormalizeStableKey(request.StableKey),
            request.Version.Trim(),
            hash,
            totalBytes,
            skillMarkdown,
            decoded.Keys.ToArray(),
            SkillText.Tokenize($"{request.Name} {request.Description} {request.WhenToUse} {string.Join(' ', request.Tags ?? [])} {skillMarkdown}"),
            validation,
            validation.StaticValidationPassed && (!validation.SelfTestExecuted || validation.SelfTestPassed),
            requiresApproval);
    }

    public static string ComputeMetadataHash(string stableKey, string name, string description, string whenToUse, IEnumerable<string> tags, IEnumerable<string> aliases, SkillRiskLevel riskLevel, long metadataVersion)
    {
        var canonical = string.Join('\n',
            NormalizeStableKey(stableKey),
            name.Trim(),
            description.Trim(),
            whenToUse.Trim(),
            string.Join(',', tags.Select(SkillText.NormalizeToken).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            string.Join(',', aliases.Select(SkillText.NormalizeToken).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            riskLevel.ToString(),
            metadataVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string? NormalizeRelativePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var path = rawPath.Replace('\\', '/').Trim();
        if (path.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(path) ||
            path.Contains('\0') ||
            path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return null;
        }

        return path;
    }

    public static string NormalizeStableKey(string value)
        => StableKeyInvalidCharacters().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');

    public static string BoundAndRedact(string? value, int maximumLength = MaximumReasonTextLength)
    {
        var bounded = (value ?? string.Empty).Trim();
        bounded = SecretAssignment().Replace(bounded, "$1=[REDACTED]");
        bounded = BearerToken().Replace(bounded, "Bearer [REDACTED]");
        return bounded.Length <= maximumLength ? bounded : bounded[..maximumLength];
    }

    public static string ComputeContentHash(PortableSkillBundle bundle)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in bundle.Files)
        {
            var path = NormalizeRelativePath(file.Path)
                ?? throw new InvalidOperationException("Cannot hash a portable bundle with an unsafe path.");
            if (file.IsSymbolicLink)
            {
                throw new InvalidOperationException("Cannot hash a portable bundle containing a symbolic link.");
            }
            if (!files.TryAdd(path, Convert.FromBase64String(file.ContentBase64)))
            {
                throw new InvalidOperationException("Cannot hash a portable bundle containing duplicate paths.");
            }
        }

        return ComputeContentHash(files);
    }

    private static void ValidateIdentity(SkillImportPreviewRequest request, List<SkillValidationIssue> issues)
    {
        if (NormalizeStableKey(request.StableKey).Length is < 2 or > 120)
        {
            issues.Add(new("InvalidStableKey", "StableKey must normalize to 2-120 characters.", "Error"));
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160)
        {
            issues.Add(new("InvalidName", "Name is required and cannot exceed 160 characters.", "Error"));
        }

        if (!SemanticVersion.TryParse(request.Version, out _))
        {
            issues.Add(new("InvalidVersion", "Version must be a valid semantic version.", "Error"));
        }

        if (string.IsNullOrWhiteSpace(request.License))
        {
            issues.Add(new("LicenseRequired", "A license identifier is required before import.", "Error"));
        }

        if (string.IsNullOrWhiteSpace(request.SourceRef))
        {
            issues.Add(new("ProvenanceRequired", "SourceRef is required for provenance and drift detection.", "Error"));
        }
    }

    private static void ValidateSkillMarkdown(string markdown, List<SkillValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        if (!markdown.StartsWith("---", StringComparison.Ordinal))
        {
            issues.Add(new("FrontmatterRequired", "SKILL.md must begin with YAML frontmatter.", "Error", "SKILL.md"));
            return;
        }

        var closing = markdown.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closing < 0)
        {
            issues.Add(new("FrontmatterUnterminated", "SKILL.md YAML frontmatter is not terminated.", "Error", "SKILL.md"));
            return;
        }

        var frontmatter = markdown[3..closing];
        foreach (var required in new[] { "name:", "description:" })
        {
            if (!frontmatter.Contains(required, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("FrontmatterFieldMissing", $"SKILL.md frontmatter must include '{required[..^1]}'.", "Error", "SKILL.md"));
            }
        }
    }

    private static (bool Executed, bool Passed) ValidateDeclarativeSelfTest(
        IReadOnlyDictionary<string, byte[]> files,
        List<SkillValidationIssue> issues)
    {
        if (!files.TryGetValue(".contexthub/self-test.json", out var bytes))
        {
            return (false, false);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var requiredFiles = root.TryGetProperty("requiredFiles", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
                : [];
            var missing = requiredFiles.Where(path => NormalizeRelativePath(path) is not { } normalized || !files.ContainsKey(normalized)).ToArray();
            if (missing.Length > 0)
            {
                issues.Add(new("SelfTestFailed", $"Declarative self-test is missing required files: {string.Join(", ", missing)}.", "Error", ".contexthub/self-test.json"));
                return (true, false);
            }

            return (true, true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            issues.Add(new("SelfTestInvalid", "Declarative self-test manifest is invalid JSON.", "Error", ".contexthub/self-test.json"));
            return (true, false);
        }
    }

    private static byte[] Decode(string value, string path, List<SkillValidationIssue> issues)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            issues.Add(new("InvalidBase64", "File content must be valid base64.", "Error", path));
            return [];
        }
    }

    private static string DecodeUtf8(byte[] bytes, string path, List<SkillValidationIssue> issues)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            issues.Add(new("InvalidUtf8", "Text files must use valid UTF-8.", "Error", path));
            return string.Empty;
        }
    }

    private static bool LooksLikeSecret(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(bytes);
        return PrivateKeyHeader().IsMatch(text) ||
               CloudAccessKey().IsMatch(text) ||
               SecretAssignment().IsMatch(text);
    }

    private static string ComputeContentHash(IReadOnlyDictionary<string, byte[]> files)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, bytes) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var pathBytes = Encoding.UTF8.GetBytes(path);
            incremental.AppendData(BitConverter.GetBytes(pathBytes.Length));
            incremental.AppendData(pathBytes);
            incremental.AppendData(BitConverter.GetBytes(bytes.Length));
            incremental.AppendData(bytes);
        }

        return Convert.ToHexStringLower(incremental.GetHashAndReset());
    }

    private static bool VerifySignature(string? algorithm, string? signature, string hash)
        => string.Equals(algorithm?.Trim(), "sha256", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(signature?.Trim(), hash, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[^a-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex StableKeyInvalidCharacters();

    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyHeader();

    [GeneratedRegex("\\b(?:AKIA|ASIA)[A-Z0-9]{16}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex CloudAccessKey();

    [GeneratedRegex("(?im)\\b(api[_-]?key|secret|password|token)\\s*[:=]\\s*['\"]?[^\\s'\"]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();
}

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = Regex.Match(value.Trim(), "^(?<major>0|[1-9]\\d*)\\.(?<minor>0|[1-9]\\d*)\\.(?<patch>0|[1-9]\\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, out var patch))
        {
            return false;
        }

        version = new(major, minor, patch, match.Groups["pre"].Value);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core != 0) return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (Prerelease.Length == 0 && other.Prerelease.Length > 0) return 1;
        if (Prerelease.Length > 0 && other.Prerelease.Length == 0) return -1;
        return string.Compare(Prerelease, other.Prerelease, StringComparison.Ordinal);
    }
}

public static class SemanticVersionConstraint
{
    public static bool IsSatisfied(string version, string? constraint)
    {
        if (!SemanticVersion.TryParse(version, out var candidate))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(constraint) || constraint.Trim() is "*" or "latest")
        {
            return true;
        }

        return constraint.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(part => Matches(candidate, part));
    }

    private static bool Matches(SemanticVersion candidate, string part)
    {
        var (op, raw) = part.StartsWith(">=", StringComparison.Ordinal) ? (">=", part[2..]) :
            part.StartsWith("<=", StringComparison.Ordinal) ? ("<=", part[2..]) :
            part.StartsWith('>') ? (">", part[1..]) :
            part.StartsWith('<') ? ("<", part[1..]) :
            part.StartsWith('^') ? ("^", part[1..]) :
            part.StartsWith('~') ? ("~", part[1..]) :
            part.StartsWith('=') ? ("=", part[1..]) : ("=", part);
        if (!SemanticVersion.TryParse(raw, out var expected))
        {
            return false;
        }

        var comparison = candidate.CompareTo(expected);
        return op switch
        {
            ">=" => comparison >= 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            "<" => comparison < 0,
            "^" => comparison >= 0 && candidate.Major == expected.Major,
            "~" => comparison >= 0 && candidate.Major == expected.Major && candidate.Minor == expected.Minor,
            _ => comparison == 0
        };
    }
}

public static partial class SkillText
{
    public static IReadOnlyList<string> Tokenize(string? value)
        => TokenMatcher().Matches(value ?? string.Empty)
            .Select(match => NormalizeToken(match.Value))
            .Where(token => token.Length > 1)
            .Distinct(StringComparer.Ordinal)
            .Take(512)
            .ToArray();

    public static string NormalizeToken(string value) => value.Trim().ToLowerInvariant();

    public static decimal KeywordScore(IReadOnlyCollection<string> queryTerms, IReadOnlyCollection<string> documentTerms)
    {
        if (queryTerms.Count == 0 || documentTerms.Count == 0)
        {
            return 0m;
        }

        var matched = queryTerms.Count(term => documentTerms.Contains(term, StringComparer.Ordinal));
        return decimal.Divide(matched, queryTerms.Count);
    }

    public static decimal CosineScore(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return 0m;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        if (leftMagnitude == 0 || rightMagnitude == 0)
        {
            return 0m;
        }

        var cosine = dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
        return Math.Clamp((decimal)((cosine + 1d) / 2d), 0m, 1m);
    }

    [GeneratedRegex("[\\p{L}\\p{N}][\\p{L}\\p{N}._+-]*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenMatcher();
}
