// Task 039C: domain identity unit gap closure.
using System;
using System.Collections.Generic;
using System.Linq;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.Identity;

namespace DubbingPlatform.UnitTests.Domain.Identity;

/// <summary>
/// Public prefixed-ID mapping. Prefixes are a closed set; unknown prefixes and
/// malformed bodies must fail with <see cref="DomainException"/> (never a
/// generic parse failure surfacing as a 500).
/// </summary>
public sealed class PublicIdMapperTests
{
    /// <summary>Shim for the not-yet-existing <c>Job</c> entity; see the test body.</summary>
    private sealed class Job
    {
    }

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void AllPrefixes_Contains_Exactly_22_Distinct_Lowercase_Prefixes()
    {
        Assert.Equal(22, PublicIdMapper.AllPrefixes.Count);
        Assert.Equal(22, PublicIdMapper.AllPrefixes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PublicIdMapper.AllPrefixes, p => Assert.EndsWith("_", p, StringComparison.Ordinal));
        Assert.All(PublicIdMapper.AllPrefixes, p => Assert.Equal(p.ToLowerInvariant(), p));
    }

    [Fact]
    public void ToPublic_Is_Deterministic_N_Hex_Encoding()
    {
        var first = PublicIdMapper.ToPublic(TenantId, PublicIdMapper.TenantPrefix);
        var second = PublicIdMapper.ToPublic(TenantId, PublicIdMapper.TenantPrefix);

        Assert.Equal(first, second);
        Assert.Equal("tenant_" + TenantId.ToString("N"), first);
        Assert.Equal("tenant_11111111111111111111111111111111", first);
        Assert.Equal(PublicIdMapper.TenantPrefix.Length + 32, first.Length);
    }

    [Fact]
    public void ToPublic_Rejects_Empty_Guid()
    {
        var ex = Assert.Throws<DomainException>(() => PublicIdMapper.ToPublic(Guid.Empty, PublicIdMapper.TenantPrefix));
        Assert.Equal("Id must not be empty.", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope_")]
    [InlineData("TENANT_")]
    [InlineData("tenant")]
    [InlineData(" x")]
    public void ToPublic_Rejects_Unknown_Prefixes(string? prefix)
    {
        var ex = Assert.Throws<DomainException>(() => PublicIdMapper.ToPublic(TenantId, prefix!));
        Assert.Contains("Unknown public ID prefix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToPublic_Checks_Empty_Guid_Before_Prefix()
    {
        var ex = Assert.Throws<DomainException>(() => PublicIdMapper.ToPublic(Guid.Empty, "bogus_"));
        Assert.Equal("Id must not be empty.", ex.Message);
    }

    [Fact]
    public void ToPublic_And_FromPublic_Round_Trip_Every_Prefix()
    {
        foreach (var prefix in PublicIdMapper.AllPrefixes)
        {
            var publicId = PublicIdMapper.ToPublic(Other, prefix);
            var (parsedPrefix, parsedId) = PublicIdMapper.FromPublic(publicId);

            Assert.Equal(prefix, parsedPrefix);
            Assert.Equal(Other, parsedId);
        }
    }

    [Fact]
    public void Every_Prefix_Produces_A_Unique_Public_Id_For_The_Same_Guid()
    {
        var publicIds = PublicIdMapper.AllPrefixes
            .Select(p => PublicIdMapper.ToPublic(TenantId, p))
            .ToList();

        Assert.Equal(22, publicIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void FromPublic_Rejects_Null_And_Empty()
    {
        Assert.Equal("Public ID must not be empty.", Assert.Throws<DomainException>(() => PublicIdMapper.FromPublic(null!)).Message);
        Assert.Equal("Public ID must not be empty.", Assert.Throws<DomainException>(() => PublicIdMapper.FromPublic(string.Empty)).Message);
    }

    [Theory]
    [InlineData("tenant_")]
    [InlineData("prj_1111111111111111111111111111111")]
    [InlineData("prj_111111111111111111111111111111111")]
    [InlineData("prj_zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("prj_1111111111111111111111111111111G")]
    [InlineData("prj_00000000000000000000000000000000")]
    public void FromPublic_Rejects_Malformed_Bodies(string publicId)
    {
        var ex = Assert.Throws<DomainException>(() => PublicIdMapper.FromPublic(publicId));
        Assert.Contains("has an invalid identifier body", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown_11111111111111111111111111111111")]
    [InlineData("prj")]
    [InlineData("tenant-11111111111111111111111111111111")]
    [InlineData("TENANT_11111111111111111111111111111111")]
    public void FromPublic_Rejects_Unknown_Prefixes(string publicId)
    {
        var ex = Assert.Throws<DomainException>(() => PublicIdMapper.FromPublic(publicId));
        Assert.Contains("has an unknown prefix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromPublic_Resolves_Every_Prefix_Deterministically()
    {
        // Shortest-prefix-first ordering must still resolve each prefix to itself.
        foreach (var prefix in PublicIdMapper.AllPrefixes.OrderBy(p => p.Length))
        {
            var publicId = PublicIdMapper.ToPublic(Other, prefix);
            Assert.Equal(prefix, PublicIdMapper.FromPublic(publicId).Prefix);
        }
    }

    [Fact]
    public void PrefixFor_Maps_Every_Known_Entity_Type()
    {
        var expectations = new Dictionary<Type, string>
        {
            [typeof(Tenant)] = PublicIdMapper.TenantPrefix,
            [typeof(DubbingProject)] = PublicIdMapper.DubbingProjectPrefix,
            [typeof(ProcessingRun)] = PublicIdMapper.ProcessingRunPrefix,
            [typeof(MediaAsset)] = PublicIdMapper.MediaAssetPrefix,
            [typeof(UploadSession)] = PublicIdMapper.UploadSessionPrefix,
            [typeof(SpeechSegment)] = PublicIdMapper.SpeechSegmentPrefix,
            [typeof(Speaker)] = PublicIdMapper.SpeakerPrefix,
            [typeof(ContextWindow)] = PublicIdMapper.ContextWindowPrefix,
            [typeof(VoiceProfile)] = PublicIdMapper.VoiceProfilePrefix,
            [typeof(Artifact)] = PublicIdMapper.ArtifactPrefix,
            [typeof(ContentObject)] = PublicIdMapper.ContentObjectPrefix,
            [typeof(StageExecution)] = PublicIdMapper.StageExecutionPrefix,
            [typeof(ProviderExecution)] = PublicIdMapper.ProviderExecutionPrefix,
            [typeof(QualityResult)] = PublicIdMapper.QualityResultPrefix,
            [typeof(ReviewItem)] = PublicIdMapper.ReviewItemPrefix,
            [typeof(ExportJob)] = PublicIdMapper.ExportJobPrefix,

            // Plan A: the `job_` prefix maps by type NAME, but no `Job` entity
            // exists in the Domain project yet. A local shim keeps the mapping
            // covered; replace with typeof(Job) once the entity lands.
            [typeof(Job)] = PublicIdMapper.JobPrefix,
            [typeof(TenantUser)] = PublicIdMapper.TenantUserPrefix,
            [typeof(ProjectMembership)] = PublicIdMapper.ProjectMembershipPrefix,
            [typeof(Notification)] = PublicIdMapper.NotificationPrefix,
            [typeof(ActivityEvent)] = PublicIdMapper.ActivityEventPrefix,
            [typeof(VoicePreviewJob)] = PublicIdMapper.VoicePreviewPrefix,
        };

        Assert.Equal(22, expectations.Count);
        Assert.Equal(
            expectations.Count,
            expectations.Values.Distinct(StringComparer.Ordinal).Count());

        foreach (var (type, expected) in expectations)
        {
            Assert.Equal(expected, PublicIdMapper.PrefixFor(type));
        }
    }

    [Fact]
    public void PrefixFor_Generic_Matches_PrefixFor_Type()
    {
        Assert.Equal(PublicIdMapper.PrefixFor(typeof(DubbingProject)), PublicIdMapper.PrefixFor<DubbingProject>());
        Assert.Equal(PublicIdMapper.VoicePreviewPrefix, PublicIdMapper.PrefixFor<VoicePreviewJob>());
    }

    [Fact]
    public void PrefixFor_Rejects_Unknown_Types()
    {
        var ex = Assert.Throws<DomainException>(() => PublicIdMapper.PrefixFor(typeof(string)));
        Assert.Contains("No public ID prefix mapping for type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrefixFor_Rejects_Null_Type()
    {
        Assert.Throws<ArgumentNullException>(() => PublicIdMapper.PrefixFor((Type)null!));
    }
}
