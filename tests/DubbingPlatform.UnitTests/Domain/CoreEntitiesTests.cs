using System;
using System.Linq;
using System.Reflection;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.Identity;

namespace DubbingPlatform.UnitTests.Domain;

public sealed class CoreEntitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Project_Rejects_Same_Source_Target_Language()
    {
        Assert.Throws<DomainException>(() => new DubbingProject(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "en",
            "en",
            ProjectStatus.Created,
            "{}",
            "config",
            null,
            null,
            Now,
            Now));

        Assert.Throws<DomainException>(() => new DubbingProject(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "EN",
            "en",
            ProjectStatus.Created,
            "{}",
            "config",
            null,
            null,
            Now,
            Now));
    }

    [Fact]
    public void Segment_Rejects_Negative_Timeline()
    {
        Assert.Throws<DomainException>(() => new SpeechSegment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            -1,
            100,
            "Pending",
            null,
            Now));

        Assert.Throws<DomainException>(() => new SpeechSegment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            100,
            100,
            "Pending",
            null,
            Now));

        Assert.Throws<DomainException>(() => new SpeechSegment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            200,
            100,
            "Pending",
            null,
            Now));
    }

    [Fact]
    public void UploadPart_Rejects_PartNumber_Zero()
    {
        Assert.Throws<DomainException>(() => new UploadPart(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            "etag",
            10,
            Now));

        Assert.Throws<DomainException>(() => new UploadPart(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            10001,
            "etag",
            10,
            Now));
    }

    [Fact]
    public void PublicIdMapper_RoundTrips_All_Prefixes()
    {
        Assert.Equal(22, PublicIdMapper.AllPrefixes.Count);

        foreach (var prefix in PublicIdMapper.AllPrefixes)
        {
            var id = Guid.NewGuid();
            var publicId = PublicIdMapper.ToPublic(id, prefix);

            Assert.Equal(prefix + id.ToString("N"), publicId);

            var (parsedPrefix, parsedId) = PublicIdMapper.FromPublic(publicId);

            Assert.Equal(prefix, parsedPrefix);
            Assert.Equal(id, parsedId);
        }

        Assert.Equal("tenant_", PublicIdMapper.PrefixFor<Tenant>());
        Assert.Equal("prj_", PublicIdMapper.PrefixFor<DubbingProject>());
        Assert.Equal("run_", PublicIdMapper.PrefixFor<ProcessingRun>());
        Assert.Equal("asset_", PublicIdMapper.PrefixFor<MediaAsset>());
        Assert.Equal("upl_", PublicIdMapper.PrefixFor<UploadSession>());
        Assert.Equal("seg_", PublicIdMapper.PrefixFor<SpeechSegment>());
        Assert.Equal("spk_", PublicIdMapper.PrefixFor<Speaker>());
        Assert.Equal("ctx_", PublicIdMapper.PrefixFor<ContextWindow>());
        Assert.Equal("voice_", PublicIdMapper.PrefixFor<VoiceProfile>());
        Assert.Equal("vpv_", PublicIdMapper.PrefixFor<VoicePreviewJob>());
    }

    [Fact]
    public void All_Scoped_Entities_Have_TenantId()
    {
        var entityTypes = typeof(Tenant).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && string.Equals(t.Namespace, "DubbingPlatform.Domain.Entities", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(52, entityTypes.Count);

        foreach (var type in entityTypes)
        {
            var property = type.GetProperty("TenantId", BindingFlags.Public | BindingFlags.Instance);
            if (string.Equals(type.Name, nameof(Tenant), StringComparison.Ordinal))
            {
                Assert.True(property is null, "Tenant must not have TenantId.");
            }
            else
            {
                Assert.True(property is not null, $"{type.Name} must have TenantId.");
                Assert.Equal(typeof(Guid), property!.PropertyType);
            }
        }
    }
}
