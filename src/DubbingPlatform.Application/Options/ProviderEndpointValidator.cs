namespace DubbingPlatform.Application.Options;

/// <summary>
/// BaseUrl/endpoint allowlist shared by provider options validators and adapters.
/// Allowed: any <c>https</c> URL, or <c>http</c> only for loopback hosts
/// (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) so WireMock tests can run
/// without opening cleartext to the public internet. Secrets are never logged;
/// this helper only inspects the URL shape.
/// </summary>
public static class ProviderEndpointValidator
{
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) && IsLoopback(uri.Host))
        {
            return true;
        }

        return false;
    }

    public static string RequireAllowed(string? url, string property)
    {
        if (!IsAllowed(url))
        {
            throw new ArgumentException($"BaseUrl '{url}' for {property} is not allowed. Use https or http localhost for tests.", property);
        }

        return url!.Trim().TrimEnd('/');
    }

    private static bool IsLoopback(string host)
    {
        var trimmed = host.Trim().Trim('[', ']');
        return string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(trimmed, "::1", StringComparison.Ordinal);
    }
}
