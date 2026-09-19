using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// One voice candidate from the TTS inventory union.
/// </summary>
public sealed record VoiceCandidate(
    string Provider,
    string VoiceId,
    string Language,
    VoiceType Type,
    string VoiceVersion);

/// <summary>
/// Outcome of one speaker assignment. <see cref="IsNew"/> is false when the
/// call returned the existing row for (run, speaker) (idempotent retry);
/// true when a new row was created (including per-run copies of a prior
/// project-stable voice).
/// </summary>
public sealed record VoiceAssignmentResult(
    Guid SpeakerId,
    Guid AssignmentId,
    Guid VoiceProfileId,
    string Provider,
    string VoiceId,
    string Reason,
    string PolicyHash,
    bool IsNew);

/// <summary>
/// Stable policy-aware voice assignment per speaker with consent enforcement.
/// Stability model (documented decision): assignments are stored per run
/// (<c>speaker_voice_assignments</c> unique on (tenant, run, speaker)), but the
/// voice is stable per project — the first run's voice is reused as a per-run
/// copy for later runs (same <c>VoiceProfileId</c>, new assignment row, fresh
/// <c>PolicyHash</c>). Retries for the same run return the existing row
/// unless <c>settings.voiceOverrides</c> changed the resolved voice (then the
/// old row is replaced in the same transaction). Consent is re-validated live
/// on every call (including idempotent returns): a revoked consent blocks even
/// a retry, while already-persisted artifacts stay immutable (they are never
/// deleted here). Privacy participates via <see cref="PolicyChecker"/>: voices
/// from policy-blocked providers never enter the inventory union.
/// No biometric audio is stored: only <c>voiceId</c> references. Never logs
/// speaker keys beyond ids, voice audio, subject identity, or evidence.
/// </summary>
public sealed class VoiceAssignmentService
{
    /// <summary>Reason when <c>settings.voiceOverrides</c> selected the voice.</summary>
    public const string ReasonOverride = "override";

    /// <summary>Reason for deterministic hash-mod selection.</summary>
    public const string ReasonDeterministic = "deterministic";

    /// <summary>Reason when a cloned voice was selected with valid consent.</summary>
    public const string ReasonClonedConsented = "cloned-consented";

    /// <summary>Cloned-voice naming prefix (case-insensitive).</summary>
    public const string ClonedVoicePrefix = "cloned-";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IDescriptorStore _descriptors;
    private readonly IProcessingPolicyProvider _policies;
    private readonly AuditService _audit;
    private readonly VoiceOptions _voices;
    private readonly ILogger<VoiceAssignmentService> _logger;

    public VoiceAssignmentService(
        IStageExecutionContextFactory contextFactory,
        IDescriptorStore descriptors,
        IProcessingPolicyProvider policies,
        AuditService audit,
        IOptions<VoiceOptions> voiceOptions,
        ILogger<VoiceAssignmentService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(voiceOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _descriptors = descriptors;
        _policies = policies;
        _audit = audit;
        _voices = voiceOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Parses <c>settings.voiceOverrides</c> (<c>{speakerKey: voiceId}</c>).
    /// Pure; invalid JSON or shapes fail closed to empty. Keys and values are
    /// trimmed; empty keys/values and non-string values are ignored. Lookup is
    /// <c>Ordinal</c> on the exact <c>Speaker.SpeakerKey</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseVoiceOverrides(string? settingsJson)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return result;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException)
        {
            return result;
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "voiceOverrides", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind is not JsonValueKind.Object)
                {
                    return result;
                }

                foreach (var entry in property.Value.EnumerateObject())
                {
                    var key = entry.Name.Trim();
                    if (key.Length == 0
                        || entry.Value.ValueKind is not JsonValueKind.String)
                    {
                        continue;
                    }

                    var voiceId = entry.Value.GetString()?.Trim() ?? string.Empty;
                    if (voiceId.Length == 0)
                    {
                        continue;
                    }

                    result[key] = voiceId;
                }

                return result;
            }

            return result;
        }
    }

    /// <summary>
    /// Infers the voice type from a voiceId when no <c>VoiceProfile</c> row
    /// exists yet. Pure. Ids starting with <c>cloned-</c>
    /// (case-insensitive) are <c>Cloned</c>; everything else is <c>Stock</c>.
    /// Persisted rows are the source of truth when present; this heuristic
    /// only seeds new rows deterministically.
    /// </summary>
    public static VoiceType InferVoiceType(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            throw new DomainException("VoiceId must not be empty.");
        }

        return voiceId.Trim().StartsWith(ClonedVoicePrefix, StringComparison.OrdinalIgnoreCase)
            ? VoiceType.Cloned
            : VoiceType.Stock;
    }

    /// <summary>
    /// Deterministic voice-profile id for (tenant, provider, voiceId,
    /// language). Pure. Provider and language are lowercased/trimmed (case
    /// insensitive); voiceId keeps its case (voice ids are case sensitive).
    /// </summary>
    public static Guid VoiceProfileIdFor(Guid tenantId, string provider, string voiceId, string language)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        var key = string.Concat(
            "voice:", tenantId.ToString("N"), ":",
            provider.Trim().ToLowerInvariant(), ":",
            voiceId.Trim(), ":",
            language.Trim().ToLowerInvariant());
        return GuidUtility.From(key);
    }

    /// <summary>
    /// Deterministic pick: filters <paramref name="candidates"/> by
    /// <paramref name="targetLanguage"/> (case-insensitive; empty candidate
    /// language counts as universal), sorts by <c>VoiceId</c> ordinal then
    /// <c>Provider</c> ordinal, and returns entry
    /// <c>SHA256(speakerKey)[0..4] mod count</c>. Pure. Throws
    /// <c>PROVIDER_CONFIGURATION_ERROR</c> when no candidate supports the
    /// language.
    /// </summary>
    public static VoiceCandidate SelectCandidate(
        string speakerKey,
        IReadOnlyList<VoiceCandidate> candidates,
        string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(speakerKey))
        {
            throw new DomainException("SpeakerKey must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(candidates);
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new DomainException("TargetLanguage must not be empty.");
        }

        var target = targetLanguage.Trim();
        var eligible = candidates
            .Where(c => c is not null
                && !string.IsNullOrWhiteSpace(c.VoiceId)
                && !string.IsNullOrWhiteSpace(c.Provider)
                && (string.IsNullOrWhiteSpace(c.Language)
                    || string.Equals(c.Language.Trim(), target, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.VoiceId, StringComparer.Ordinal)
            .ThenBy(c => c.Provider, StringComparer.Ordinal)
            .ToList();

        if (eligible.Count == 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"No voice inventory supports language '{target}' for voice assignment.");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(speakerKey));
        var slot = (uint)((hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3]);
        return eligible[(int)(slot % (uint)eligible.Count)];
    }

    /// <summary>
    /// Resolves the voice for a speaker: override wins (validated against the
    /// inventory, else <c>PROVIDER_CONFIGURATION_ERROR</c>), otherwise the
    /// deterministic <see cref="SelectCandidate"/> pick. Pure. Returns the
    /// candidate plus the reason (<c>override</c> or <c>deterministic</c>;
    /// callers upgrade deterministic to <c>cloned-consented</c> after the
    /// consent check passes for a cloned voice).
    /// When an override voiceId exists under multiple providers, the
    /// <paramref name="defaultProvider"/> match wins; otherwise the first
    /// sorted provider wins (deterministic).
    /// </summary>
    public static (VoiceCandidate Candidate, string Reason) ResolveVoice(
        string speakerKey,
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyList<VoiceCandidate> candidates,
        string targetLanguage,
        string defaultProvider)
    {
        if (string.IsNullOrWhiteSpace(speakerKey))
        {
            throw new DomainException("SpeakerKey must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(candidates);
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new DomainException("TargetLanguage must not be empty.");
        }

        if (overrides.TryGetValue(speakerKey, out var overrideVoiceId)
            && !string.IsNullOrWhiteSpace(overrideVoiceId))
        {
            var wanted = overrideVoiceId.Trim();
            var matches = candidates
                .Where(c => c is not null
                    && string.Equals(c.VoiceId, wanted, StringComparison.Ordinal)
                    && (string.IsNullOrWhiteSpace(c.Language)
                        || string.Equals(c.Language.Trim(), targetLanguage.Trim(), StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => string.Equals(c.Provider?.Trim(), defaultProvider?.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Provider, StringComparer.Ordinal)
                .ToList();

            if (matches.Count == 0)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderConfigurationError,
                    $"Voice override '{wanted}' for speaker is not in the provider inventory for language '{targetLanguage.Trim()}'.");
            }

            return (matches[0], ReasonOverride);
        }

        return (SelectCandidate(speakerKey, candidates, targetLanguage), ReasonDeterministic);
    }

    /// <summary>
    /// Live cloned-use gate (pure part). No-op for stock voices. For cloned
    /// voices requires <c>cloningEnabled</c> (else <c>CONSENT_REQUIRED</c>)
    /// plus <see cref="ConsentService.ValidateForVoice"/> (which throws
    /// <c>CONSENT_REQUIRED</c> or <c>POLICY_DENIED</c>). Pure.
    /// </summary>
    public static void ValidateClonedUse(
        VoiceCandidate chosen,
        bool cloningEnabled,
        ConsentRecord? consent,
        Guid projectId,
        Guid? voiceProfileId,
        ProcessingPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        if (chosen.Type != VoiceType.Cloned)
        {
            return;
        }

        if (!cloningEnabled)
        {
            throw new ErrorCodeException(ErrorCodes.ConsentRequired, "Cloned voices require Voices:CloningEnabled=true plus a granted consent.");
        }

        ConsentService.ValidateForVoice(consent, projectId, voiceProfileId, policy);
    }

    /// <summary>
    /// Policy hash over the tenant policy snapshot plus the TTS inventory
    /// versions. Pure. Secrets never enter the hash (only provider names,
    /// sorted voice ids, versions, regions, and the non-secret policy
    /// snapshot). Used as <c>SpeakerVoiceAssignment.PolicyHash</c> for audit.
    /// </summary>
    public static string ComputePolicyHash(
        ProcessingPolicy? policy,
        IReadOnlyList<ProviderCapabilityDescriptor> descriptors,
        string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new DomainException("TargetLanguage must not be empty.");
        }

        var inventory = descriptors
            .Where(d => d is not null)
            .OrderBy(d => d.Provider.ToString(), StringComparer.Ordinal)
            .Select(d => new
            {
                provider = d.Provider.ToString(),
                voices = (d.VoiceInventory ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).OrderBy(v => v, StringComparer.Ordinal).ToArray(),
                version = d.Version,
                region = d.Region,
                voiceCloning = d.VoiceCloning,
            })
            .ToArray();

        return ConfigurationHashCalculator.Compute(new
        {
            target = targetLanguage.Trim().ToLowerInvariant(),
            externalProvidersAllowed = policy?.ExternalProvidersAllowed,
            allowedProviders = (policy?.AllowedProviders ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            residency = policy?.ResidencyConstraint?.Trim(),
            voicePolicy = policy?.VoicePolicy,
            inventory,
        });
    }

    /// <summary>
    /// Assigns a stable policy-aware voice to one speaker. See class docs for
    /// the stability/consent contract. Throws <c>PROVIDER_CONFIGURATION_ERROR</c>
    /// for unknown overrides or missing language inventory,
    /// <c>CONSENT_REQUIRED</c> for cloned voices without valid live consent,
    /// and <c>POLICY_DENIED</c> only for jurisdiction mismatches.
    /// </summary>
    public async Task<VoiceAssignmentResult> AssignAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid speakerId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));
        RequireId(speakerId, nameof(speakerId));

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        await EnsureRunActiveAsync(tenantId, runId, projectId, cancellationToken).ConfigureAwait(false);
        var speaker = await LoadSpeakerAsync(tenantId, projectId, speakerId, cancellationToken).ConfigureAwait(false);

        var overrides = ParseVoiceOverrides(project.SettingsJson);
        var target = project.TargetLanguage.Trim();
        var policy = await _policies.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var descriptors = await _descriptors.GetCandidatesAsync(ProviderCapability.Tts, tenantId, cancellationToken).ConfigureAwait(false);
        var policyHash = ComputePolicyHash(policy, descriptors, target);

        var candidates = await BuildCandidatesAsync(tenantId, target, descriptors, policy, cancellationToken).ConfigureAwait(false);
        var defaultProvider = string.IsNullOrWhiteSpace(_voices.DefaultProvider) ? "mock" : _voices.DefaultProvider.Trim();

        // Current-run idempotency first (stable retry).
        var existing = await LoadAssignmentAsync(tenantId, runId, speakerId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var assignedProfile = await LoadVoiceProfileAsync(tenantId, existing.VoiceProfileId, cancellationToken).ConfigureAwait(false);
            if (assignedProfile is not null)
            {
                // Override change replaces; otherwise live consent re-check then return.
                if (overrides.TryGetValue(speaker.SpeakerKey, out var wanted) && !string.IsNullOrWhiteSpace(wanted))
                {
                    if (!string.Equals(assignedProfile.VoiceId, wanted.Trim(), StringComparison.Ordinal))
                    {
                        return await ReplaceForOverrideAsync(
                            tenantId, projectId, runId, speaker, existing, assignedProfile,
                            wanted.Trim(), candidates, target, defaultProvider, policy, policyHash,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                if (assignedProfile.Type == VoiceType.Cloned)
                {
                    await EnsureClonedConsentLiveAsync(
                        tenantId, projectId, assignedProfile, policy, cancellationToken).ConfigureAwait(false);
                }

                return new VoiceAssignmentResult(
                    speakerId, existing.Id, assignedProfile.Id,
                    assignedProfile.Provider, assignedProfile.VoiceId,
                    existing.AssignmentReason, existing.PolicyHash, false);
            }
        }

        // Project-stable reuse: copy the prior run's voice when no override diverts.
        var prior = await LoadPriorProjectAssignmentAsync(tenantId, projectId, runId, speakerId, cancellationToken).ConfigureAwait(false);
        if (prior is not null
            && (!overrides.TryGetValue(speaker.SpeakerKey, out var priorWanted) || string.IsNullOrWhiteSpace(priorWanted)))
        {
            var priorProfile = await LoadVoiceProfileAsync(tenantId, prior.VoiceProfileId, cancellationToken).ConfigureAwait(false);
            if (priorProfile is not null)
            {
                if (priorProfile.Type == VoiceType.Cloned)
                {
                    await EnsureClonedConsentLiveAsync(
                        tenantId, projectId, priorProfile, policy, cancellationToken).ConfigureAwait(false);
                }

                var reason = priorProfile.Type == VoiceType.Cloned ? ReasonClonedConsented : prior.AssignmentReason;
                if (!string.Equals(reason, ReasonOverride, StringComparison.Ordinal)
                    && !string.Equals(reason, ReasonDeterministic, StringComparison.Ordinal)
                    && !string.Equals(reason, ReasonClonedConsented, StringComparison.Ordinal))
                {
                    reason = priorProfile.Type == VoiceType.Cloned ? ReasonClonedConsented : ReasonDeterministic;
                }

                var copy = await InsertAssignmentAsync(
                    tenantId, projectId, runId, speakerId,
                    priorProfile.Id, reason, policyHash, cancellationToken).ConfigureAwait(false);
                if (priorProfile.Type == VoiceType.Cloned)
                {
                    await AuditClonedAsync(tenantId, projectId, speakerId, copy, priorProfile, policyHash, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation(
                    "Voice assignment reused project-stable voice {VoiceId} for speaker {SpeakerId} in run {RunId}.",
                    priorProfile.VoiceId, speakerId, runId);
                return new VoiceAssignmentResult(
                    speakerId, copy.Id, priorProfile.Id,
                    priorProfile.Provider, priorProfile.VoiceId,
                    reason, policyHash, true);
            }
        }

        // Fresh resolve (override or deterministic) + consent + persist.
        var (candidate, reasonFresh) = ResolveVoice(speaker.SpeakerKey, overrides, candidates, target, defaultProvider);
        var profile = await EnsureVoiceProfileAsync(tenantId, candidate, target, cancellationToken).ConfigureAwait(false);

        // The persisted row is the Type source of truth; an inferred candidate
        // Type that disagrees with an existing row follows the row.
        var effectiveType = profile.Type;
        if (effectiveType == VoiceType.Cloned)
        {
            await EnsureClonedConsentLiveAsync(tenantId, projectId, profile, policy, cancellationToken).ConfigureAwait(false);
            reasonFresh = string.Equals(reasonFresh, ReasonOverride, StringComparison.Ordinal)
                ? ReasonOverride
                : ReasonClonedConsented;
        }

        var assignment = await InsertAssignmentAsync(
            tenantId, projectId, runId, speakerId,
            profile.Id, reasonFresh, policyHash, cancellationToken).ConfigureAwait(false);
        if (effectiveType == VoiceType.Cloned)
        {
            await AuditClonedAsync(tenantId, projectId, speakerId, assignment, profile, policyHash, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Voice assignment created for speaker {SpeakerId} in run {RunId}: voice {VoiceId} ({Reason}).",
            speakerId, runId, profile.VoiceId, reasonFresh);
        return new VoiceAssignmentResult(
            speakerId, assignment.Id, profile.Id,
            profile.Provider, profile.VoiceId,
            reasonFresh, policyHash, true);
    }

    private async Task<VoiceAssignmentResult> ReplaceForOverrideAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Speaker speaker,
        SpeakerVoiceAssignment existing,
        VoiceProfile assignedProfile,
        string wantedVoiceId,
        IReadOnlyList<VoiceCandidate> candidates,
        string target,
        string defaultProvider,
        ProcessingPolicy? policy,
        string policyHash,
        CancellationToken cancellationToken)
    {
        var matches = candidates
            .Where(c => string.Equals(c.VoiceId, wantedVoiceId, StringComparison.Ordinal))
            .OrderBy(c => string.Equals(c.Provider?.Trim(), defaultProvider.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.Provider, StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"Voice override '{wantedVoiceId}' for speaker is not in the provider inventory for language '{target}'.");
        }

        var profile = await EnsureVoiceProfileAsync(tenantId, matches[0], target, cancellationToken).ConfigureAwait(false);
        if (profile.Type == VoiceType.Cloned)
        {
            await EnsureClonedConsentLiveAsync(tenantId, projectId, profile, policy, cancellationToken).ConfigureAwait(false);
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var tracked = await db.Set<SpeakerVoiceAssignment>()
                    .FirstOrDefaultAsync(a => a.Id == existing.Id, cancellationToken).ConfigureAwait(false);
                if (tracked is not null)
                {
                    db.Set<SpeakerVoiceAssignment>().Remove(tracked);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                var now = DateTimeOffset.UtcNow;
                var replacement = new SpeakerVoiceAssignment(
                    Guid.NewGuid(), tenantId, projectId, runId,
                    speaker.Id, profile.Id, ReasonOverride, policyHash, now);
                db.Set<SpeakerVoiceAssignment>().Add(replacement);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                if (profile.Type == VoiceType.Cloned)
                {
                    await AuditClonedAsync(tenantId, projectId, speaker.Id, replacement, profile, policyHash, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation(
                    "Voice assignment override replaced voice {OldVoice} with {NewVoice} for speaker {SpeakerId} in run {RunId}.",
                    assignedProfile.VoiceId, profile.VoiceId, speaker.Id, runId);
                return new VoiceAssignmentResult(
                    speaker.Id, replacement.Id, profile.Id,
                    profile.Provider, profile.VoiceId,
                    ReasonOverride, policyHash, true);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Rollback best effort; original exception propagates.
                }

                throw;
            }
        }
    }

    private async Task<IReadOnlyList<VoiceCandidate>> BuildCandidatesAsync(
        Guid tenantId,
        string target,
        IReadOnlyList<ProviderCapabilityDescriptor> descriptors,
        ProcessingPolicy? policy,
        CancellationToken cancellationToken)
    {
        var routing = new ProviderRoutingRequest(target, 0, 0, null, false, false, false);
        var allowed = (descriptors ?? [])
            .Where(d => d is not null
                && PolicyChecker.CanUseProvider(policy, d.Provider, d.Region)
                && _descriptors.IsCompatible(d, routing))
            .ToList();

        Dictionary<string, VoiceProfile> profilesByVoice = new(StringComparer.Ordinal);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var profiles = await db.Set<VoiceProfile>()
                .AsNoTracking()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var profile in profiles)
            {
                var key = string.Concat(profile.Provider.Trim().ToLowerInvariant(), "|", profile.VoiceId);
                if (!profilesByVoice.ContainsKey(key))
                {
                    profilesByVoice[key] = profile;
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<VoiceCandidate>();
        foreach (var descriptor in allowed.OrderBy(d => d.Provider.ToString(), StringComparer.Ordinal))
        {
            // Language gate: descriptors advertising languages must include the target.
            if (descriptor.SupportedLanguages.Length > 0
                && !descriptor.SupportedLanguages.Any(l => string.Equals(l?.Trim(), target, StringComparison.OrdinalIgnoreCase) || string.Equals(l?.Trim(), "*", StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (var raw in descriptor.VoiceInventory ?? [])
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var voiceId = raw.Trim();
                var dedupe = string.Concat(descriptor.Provider.ToString().ToLowerInvariant(), "|", voiceId);
                if (!seen.Add(dedupe))
                {
                    continue;
                }

                var lookup = string.Concat(descriptor.Provider.ToString().ToLowerInvariant(), "|", voiceId);
                var type = profilesByVoice.TryGetValue(lookup, out var existing)
                    ? existing.Type
                    : InferVoiceType(voiceId);
                var version = existing?.VoiceVersion ?? "1";
                candidates.Add(new VoiceCandidate(
                    descriptor.Provider.ToString(), voiceId, target, type, version));
            }
        }

        if (candidates.Count > 0)
        {
            return candidates;
        }

        // No inventory contributed: descriptors exist but none match the
        // language (or all inventories are empty) → fail with a clear message.
        // The sole exception is the zero-config mock default (no descriptors
        // at all, or only the synthesized empty mock): synthesize stock mock
        // voices so default deployments flow without config.
        var hasConfiguredInventory = (descriptors ?? []).Any(d =>
            d is not null && d.VoiceInventory is not null && d.VoiceInventory.Length > 0);
        if (hasConfiguredInventory)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"No voice inventory supports language '{target}' for voice assignment.");
        }

        var fallbackProvider = string.IsNullOrWhiteSpace(_voices.DefaultProvider) ? "mock" : _voices.DefaultProvider.Trim();
        return
        [
            new VoiceCandidate(fallbackProvider, "mock-voice-1", target, VoiceType.Stock, "1"),
            new VoiceCandidate(fallbackProvider, "mock-voice-2", target, VoiceType.Stock, "1"),
            new VoiceCandidate(fallbackProvider, "mock-voice-3", target, VoiceType.Stock, "1"),
        ];
    }

    private async Task<VoiceProfile> EnsureVoiceProfileAsync(
        Guid tenantId,
        VoiceCandidate candidate,
        string target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var provider = candidate.Provider.Trim();
        var voiceId = candidate.VoiceId.Trim();
        var language = (string.IsNullOrWhiteSpace(candidate.Language) ? target : candidate.Language).Trim().ToLowerInvariant();
        var profileId = VoiceProfileIdFor(tenantId, provider, voiceId, language);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var current = await db.Set<VoiceProfile>()
                .FirstOrDefaultAsync(v => v.Id == profileId, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                return current;
            }

            var now = DateTimeOffset.UtcNow;
            var created = new VoiceProfile(
                profileId, tenantId, provider, voiceId, candidate.VoiceVersion ?? "1",
                language, candidate.Type, _voices.CloningEnabled, null, now);
            db.Set<VoiceProfile>().Add(created);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                var raced = await db.Set<VoiceProfile>()
                    .FirstOrDefaultAsync(v => v.Id == profileId, cancellationToken).ConfigureAwait(false);
                if (raced is not null)
                {
                    return raced;
                }

                throw new ErrorCodeException(ErrorCodes.Conflict, $"Voice profile '{voiceId}' was created concurrently.");
            }

            return created;
        }
    }

    private async Task EnsureClonedConsentLiveAsync(
        Guid tenantId,
        Guid projectId,
        VoiceProfile profile,
        ProcessingPolicy? policy,
        CancellationToken cancellationToken)
    {
        if (profile.Type != VoiceType.Cloned)
        {
            return;
        }

        if (!_voices.CloningEnabled)
        {
            throw new ErrorCodeException(ErrorCodes.ConsentRequired, "Cloned voices require Voices:CloningEnabled=true plus a granted consent.");
        }

        List<ConsentRecord> consents;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            consents = await db.Set<ConsentRecord>()
                .AsNoTracking()
                .Where(c => c.Status == ConsentStatus.Granted && c.RevokedAt == null)
                .OrderByDescending(c => c.GrantedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var sawPolicyDenied = false;
        foreach (var consent in consents)
        {
            try
            {
                ConsentService.ValidateForVoice(consent, projectId, profile.Id, policy);
                return;
            }
            catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.PolicyDenied, StringComparison.Ordinal))
            {
                sawPolicyDenied = true;
            }
            catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.ConsentRequired, StringComparison.Ordinal))
            {
                // Try the next consent row.
            }
        }

        // Also consider revoked/expired rows for the error signal: any
        // jurisdiction mismatch surfaces as POLICY_DENIED, else CONSENT_REQUIRED.
        if (sawPolicyDenied)
        {
            throw new ErrorCodeException(ErrorCodes.PolicyDenied, "No granted consent satisfies the policy residency for this cloned voice.");
        }

        throw new ErrorCodeException(ErrorCodes.ConsentRequired, "No granted consent covers this cloned voice use (revoked consents block new uses).");
    }

    private async Task<SpeakerVoiceAssignment> InsertAssignmentAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid speakerId,
        Guid voiceProfileId,
        string reason,
        string policyHash,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var assignment = new SpeakerVoiceAssignment(
                Guid.NewGuid(), tenantId, projectId, runId,
                speakerId, voiceProfileId, reason, policyHash, now);
            db.Set<SpeakerVoiceAssignment>().Add(assignment);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                var raced = await db.Set<SpeakerVoiceAssignment>()
                    .FirstOrDefaultAsync(a => a.RunId == runId && a.SpeakerId == speakerId, cancellationToken).ConfigureAwait(false);
                if (raced is not null)
                {
                    return raced;
                }

                throw new ErrorCodeException(ErrorCodes.Conflict, $"Voice assignment for speaker '{speakerId:D}' was created concurrently.");
            }

            return assignment;
        }
    }

    private async Task AuditClonedAsync(
        Guid tenantId,
        Guid projectId,
        Guid speakerId,
        SpeakerVoiceAssignment assignment,
        VoiceProfile profile,
        string policyHash,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            assignmentId = assignment.Id.ToString("N"),
            speakerId = speakerId.ToString("N"),
            voiceProfileId = profile.Id.ToString("N"),
            provider = profile.Provider,
            voiceId = profile.VoiceId,
            reason = assignment.AssignmentReason,
            policyHash,
        }, JsonOptions);
        await _audit.LogAsync(
            tenantId, projectId, "voice-assignment", "voice.cloned.assigned",
            "SpeakerVoiceAssignment", assignment.Id.ToString("N"),
            details, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SpeakerVoiceAssignment?> LoadAssignmentAsync(
        Guid tenantId, Guid runId, Guid speakerId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<SpeakerVoiceAssignment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.RunId == runId && a.SpeakerId == speakerId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SpeakerVoiceAssignment?> LoadPriorProjectAssignmentAsync(
        Guid tenantId, Guid projectId, Guid runId, Guid speakerId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<SpeakerVoiceAssignment>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.SpeakerId == speakerId && a.RunId != runId)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<VoiceProfile?> LoadVoiceProfileAsync(
        Guid tenantId, Guid voiceProfileId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var profile = await db.Set<VoiceProfile>()
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == voiceProfileId, cancellationToken).ConfigureAwait(false);
            if (profile is not null && profile.TenantId != tenantId)
            {
                throw new ForbiddenException($"Voice profile '{voiceProfileId}' does not belong to the current tenant.");
            }

            return profile;
        }
    }

    private async Task<Speaker> LoadSpeakerAsync(
        Guid tenantId, Guid projectId, Guid speakerId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var speaker = await db.Set<Speaker>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == speakerId, cancellationToken).ConfigureAwait(false);
            if (speaker is null)
            {
                throw new NotFoundException($"Speaker '{speakerId:D}' was not found.");
            }

            if (speaker.TenantId != tenantId || speaker.ProjectId != projectId)
            {
                throw new ForbiddenException($"Speaker '{speakerId:D}' does not belong to the current tenant/project.");
            }

            return speaker;
        }
    }

    private async Task<DubbingProject> LoadOwnedProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        DubbingProject? project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                throw new NotFoundException($"Project '{projectId}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }

        return project;
    }

    private async Task EnsureRunActiveAsync(Guid tenantId, Guid runId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                throw new NotFoundException($"Processing run '{runId}' was not found.");
            }

            if (run.TenantId != tenantId || run.ProjectId != projectId)
            {
                throw new ForbiddenException($"Processing run '{runId}' does not belong to the current tenant/project.");
            }

            if (run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                throw new LeaseLostException($"Processing run '{runId}' is '{run.Status}'; aborting before commit.");
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || message.Contains("23505", StringComparison.Ordinal);
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException($"{name} must not be empty.");
        }
    }
}
