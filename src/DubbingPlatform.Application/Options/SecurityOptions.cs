using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Service-to-service transport security. Binds to the <c>Security</c>
/// section. mTLS is disabled by default for local development
/// (<c>MtlsEnabled=false</c>); production topologies that terminate service
/// traffic inside the cluster mesh enable it and must provide
/// <c>CaPath</c>/<c>CertPath</c> (fail fast at startup otherwise). See
/// <c>docs/security/mtls.md</c> for the rotation and topology runbook.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public bool MtlsEnabled { get; set; }

    [MaxLength(512)]
    public string? CaPath { get; set; }

    [MaxLength(512)]
    public string? CertPath { get; set; }
}

/// <summary>
/// Fail-fast startup validation for <see cref="SecurityOptions"/>.
/// </summary>
public sealed class SecurityOptionsValidator : IValidateOptions<SecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, SecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MtlsEnabled)
        {
            if (string.IsNullOrWhiteSpace(options.CaPath))
            {
                return ValidateOptionsResult.Fail($"{nameof(SecurityOptions)}.{nameof(SecurityOptions.CaPath)} is required when {nameof(SecurityOptions.MtlsEnabled)} is true.");
            }

            if (string.IsNullOrWhiteSpace(options.CertPath))
            {
                return ValidateOptionsResult.Fail($"{nameof(SecurityOptions)}.{nameof(SecurityOptions.CertPath)} is required when {nameof(SecurityOptions.MtlsEnabled)} is true.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
