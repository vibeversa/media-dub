namespace DubbingPlatform.Application.Configuration;

/// <summary>
/// Computes the deterministic execution-snapshot hash binding a run to its resolved
/// configuration: run config, route hash, sorted input hashes, sorted prompt hashes,
/// and policy hash. Null inputs are treated as empty (empty object, empty string, or
/// empty list) and do not throw. Secret stripping and canonicalization are inherited
/// from <see cref="ConfigurationHashCalculator"/>.
/// </summary>
public static class ExecutionSnapshotCalculator
{
    /// <summary>
    /// Computes the snapshot hash. Input and prompt hashes are sorted (ordinal) first
    /// so caller ordering does not affect the result.
    /// </summary>
    public static string Compute(
        object? runConfig,
        string? routeHash,
        IEnumerable<string>? inputHashes,
        IEnumerable<string>? promptHashes,
        string? policyHash)
    {
        var composite = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["inputHashes"] = (inputHashes ?? []).OrderBy(h => h, StringComparer.Ordinal).ToArray(),
            ["policyHash"] = policyHash ?? string.Empty,
            ["promptHashes"] = (promptHashes ?? []).OrderBy(h => h, StringComparer.Ordinal).ToArray(),
            ["routeHash"] = routeHash ?? string.Empty,
            ["runConfig"] = runConfig ?? new Dictionary<string, object?>(),
        };

        return ConfigurationHashCalculator.Compute(composite);
    }
}
