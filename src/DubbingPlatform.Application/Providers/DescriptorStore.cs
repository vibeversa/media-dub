using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Singleton descriptor store. Config descriptors are global templates stamped
/// with the requesting tenant at resolve time; DB rows are tenant overrides
/// loaded per call via a scope (never captured). When no descriptor exists for
/// a capability, a mock unbounded descriptor is synthesized so default
/// mock-only deployments route without config. DB failures degrade to config
/// (operators see storage health, routing keeps flowing).
/// </summary>
public sealed class DescriptorStore : IDescriptorStore
{
    private readonly IOptions<ProviderOptions> _options;
    private readonly IServiceScopeFactory _scopes;

    public DescriptorStore(IOptions<ProviderOptions> options, IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopes);
        _options = options;
        _scopes = scopes;
    }

    public async Task<IReadOnlyList<ProviderCapabilityDescriptor>> GetCandidatesAsync(
        ProviderCapability capability,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new Domain.Exceptions.DomainException("TenantId must not be empty.");
        }

        var merged = new Dictionary<ProviderType, ProviderCapabilityDescriptor>();
        foreach (var option in _options.Value.Descriptors ?? [])
        {
            if (option is null)
            {
                continue;
            }

            if (!ProviderOptionNames.TryParseProvider(option.Provider, out var provider))
            {
                continue;
            }

            if (!Enum.TryParse<ProviderCapability>(option.Capability, ignoreCase: true, out var parsed)
                || parsed != capability)
            {
                continue;
            }

            var entity = FromOption(option, provider, parsed, tenantId);
            if (!merged.TryGetValue(provider, out var existing) || entity.Version >= existing.Version)
            {
                merged[provider] = entity;
            }
        }

        foreach (var row in await LoadDbDescriptorsAsync(capability, tenantId, cancellationToken).ConfigureAwait(false))
        {
            if (!merged.TryGetValue(row.Provider, out var existing) || row.Version >= existing.Version)
            {
                merged[row.Provider] = row;
            }
        }

        if (merged.Count == 0)
        {
            merged[ProviderType.Mock] = SynthesizeMock(capability, tenantId);
        }

        return merged.Values.ToList();
    }

    public bool IsCompatible(ProviderCapabilityDescriptor descriptor, ProviderRoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);
        return IsCompatibleCore(descriptor, request);
    }

    internal static bool IsCompatibleCore(ProviderCapabilityDescriptor descriptor, ProviderRoutingRequest request)
    {
        if (!IsLanguageSupported(descriptor.SupportedLanguages, request.Language))
        {
            return false;
        }

        if (!IsFormatSupported(descriptor.SupportedFormats, request.MediaFormat))
        {
            return false;
        }

        if (descriptor.MaxInputBytes > 0 && request.InputBytes > descriptor.MaxInputBytes)
        {
            return false;
        }

        if (descriptor.MaxDurationMs > 0 && request.DurationMs > descriptor.MaxDurationMs)
        {
            return false;
        }

        if (request.RequiresWordTimestamps && !descriptor.WordTimestamps)
        {
            return false;
        }

        if (request.RequiresDiarization && !descriptor.Diarization)
        {
            return false;
        }

        if (request.RequiresVoiceCloning && !descriptor.VoiceCloning)
        {
            return false;
        }

        return true;
    }

    private static bool IsLanguageSupported(string[] supported, string language)
    {
        if (supported.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        foreach (var entry in supported)
        {
            if (string.Equals(entry, "*", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(entry?.Trim(), language.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFormatSupported(string[] supported, string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return true;
        }

        if (supported.Length == 0)
        {
            return true;
        }

        foreach (var entry in supported)
        {
            if (string.Equals(entry?.Trim(), format.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ProviderCapabilityDescriptor FromOption(
        ProviderDescriptorOption option,
        ProviderType provider,
        ProviderCapability capability,
        Guid tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ProviderCapabilityDescriptor(
            Guid.NewGuid(),
            tenantId,
            provider,
            capability,
            option.SupportedLanguages ?? [],
            option.SupportedFormats ?? [],
            Math.Max(0, option.MaxInputBytes),
            Math.Max(0, option.MaxDurationMs),
            false,
            false,
            option.WordTimestamps,
            option.Diarization,
            [],
            option.VoiceCloning,
            [],
            "model",
            "{}",
            "{}",
            string.IsNullOrWhiteSpace(option.PrivacyClass) ? "standard" : option.PrivacyClass,
            string.IsNullOrWhiteSpace(option.Region) ? "global" : option.Region,
            Math.Max(1, option.Version),
            now);
    }

    private static ProviderCapabilityDescriptor SynthesizeMock(ProviderCapability capability, Guid tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ProviderCapabilityDescriptor(
            Guid.NewGuid(),
            tenantId,
            ProviderType.Mock,
            capability,
            [],
            [],
            0,
            0,
            false,
            false,
            true,
            true,
            [],
            true,
            [],
            "mock-deterministic",
            "{}",
            "{}",
            "standard",
            "global",
            1,
            now);
    }

    private async Task<IReadOnlyList<ProviderCapabilityDescriptor>> LoadDbDescriptorsAsync(
        ProviderCapability capability,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var factories = scope.ServiceProvider.GetService(typeof(IStageExecutionContextFactory)) as IStageExecutionContextFactory;
            if (factories is null)
            {
                return [];
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = factories.CreateDbContext();
                return await db.Set<ProviderCapabilityDescriptor>()
                    .AsNoTracking()
                    .Where(d => d.Capability == capability)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            return [];
        }
    }
}
