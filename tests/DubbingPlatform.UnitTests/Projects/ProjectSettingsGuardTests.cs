// Task 039C: project settings-guard and config-hash unit gap closure.

using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Projects;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DubbingPlatform.UnitTests.Projects;

/// <summary>
/// What actually blocks a project settings change. <see cref="ProjectSettingsGuard"/>
/// is defined purely by "does the project have a run in an active status":
/// active run blocks, terminal run does not, archived project without an active
/// run is not this guard's concern, and a no-op settings change is not either.
/// All lookups run over an InMemory <c>AppDbContext</c> (no Docker, no network)
/// with the tenant query filter active, so the cross-tenant negative case is
/// genuinely exercised. The pure <c>IsActiveStatus</c> matrix and the error-code
/// catalogue live in <see cref="ProjectGuardHashTests"/>; this file covers the
/// guard's own reads plus <see cref="ProjectConfigHash"/> determinism and
/// <c>ProjectExceptions</c> payloads.
/// </summary>
public sealed class ProjectSettingsGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // ProjectSettingsGuard — active-run matrix
    // ------------------------------------------------------------------

    [Fact]
    public void Active_Run_Status_Set_Is_Exactly_The_Four_Quota_Active_States()
    {
        Assert.Equal(
            new[]
            {
                ProcessingRunStatus.Pending,
                ProcessingRunStatus.Running,
                ProcessingRunStatus.Cancelling,
                ProcessingRunStatus.ManualReviewRequired,
            },
            ProjectSettingsGuard.ActiveRunStatuses);

        Assert.Equal(4, ProjectSettingsGuard.ActiveRunStatuses.Length);
    }

    [Theory]
    [InlineData(ProcessingRunStatus.Pending, true)]
    [InlineData(ProcessingRunStatus.Running, true)]
    [InlineData(ProcessingRunStatus.Cancelling, true)]
    [InlineData(ProcessingRunStatus.ManualReviewRequired, true)]
    [InlineData(ProcessingRunStatus.Completed, false)]
    [InlineData(ProcessingRunStatus.Failed, false)]
    [InlineData(ProcessingRunStatus.Cancelled, false)]
    public async Task HasActiveRun_Maps_Every_Run_Status(ProcessingRunStatus status, bool expected)
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedRun(factory, tenantId, projectId, status);

        var guard = new ProjectSettingsGuard(factory);

        Assert.Equal(expected, await guard.HasActiveRunAsync(tenantId, projectId));
    }

    [Fact]
    public async Task No_Runs_At_All_Means_No_Lock()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var factory = CreateFactory();
        var guard = new ProjectSettingsGuard(factory);

        Assert.False(await guard.HasActiveRunAsync(tenantId, projectId));
    }

    [Fact]
    public async Task An_Active_Run_On_Another_Project_Does_Not_Lock()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedRun(factory, tenantId, Guid.NewGuid(), ProcessingRunStatus.Running);
        var guard = new ProjectSettingsGuard(factory);

        Assert.False(await guard.HasActiveRunAsync(tenantId, projectId));
    }

    [Fact]
    public async Task A_Completed_Run_Does_Not_Block_A_Settings_Change_Or_A_Delete()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedRun(factory, tenantId, projectId, ProcessingRunStatus.Completed);
        var guard = new ProjectSettingsGuard(factory);

        Assert.False(await guard.HasActiveRunAsync(tenantId, projectId));
        await guard.ThrowIfSettingsLockedAsync(tenantId, projectId);
        await guard.ThrowIfDeleteBlockedAsync(tenantId, projectId);
    }

    [Fact]
    public async Task An_Active_Run_Blocks_Settings_And_Delete_With_The_Catalogued_Codes()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedRun(factory, tenantId, projectId, ProcessingRunStatus.Running);
        var guard = new ProjectSettingsGuard(factory);

        var settings = await Assert.ThrowsAsync<SettingsLockedActiveRunException>(
            () => guard.ThrowIfSettingsLockedAsync(tenantId, projectId));
        Assert.Equal(ErrorCodes.SettingsLockedActiveRun, settings.ErrorCode);
        Assert.Equal(409, settings.StatusCode);
        Assert.Contains(projectId.ToString(), settings.Message, StringComparison.Ordinal);

        var delete = await Assert.ThrowsAsync<ProjectHasActiveRunException>(
            () => guard.ThrowIfDeleteBlockedAsync(tenantId, projectId));
        Assert.Equal(ErrorCodes.ProjectHasActiveRun, delete.ErrorCode);
        Assert.Equal(409, delete.StatusCode);
    }

    [Fact]
    public async Task Cross_Tenant_Active_Runs_Never_Lock_A_Project()
    {
        // Same project id, different tenant: the tenant query filter keeps the
        // other tenant's run out, so the guard reports "no active run".
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedRun(factory, tenantB, projectId, ProcessingRunStatus.Running);
        SeedRun(factory, tenantB, projectId, ProcessingRunStatus.Pending);
        var guard = new ProjectSettingsGuard(factory);

        Assert.False(await guard.HasActiveRunAsync(tenantA, projectId));
        await guard.ThrowIfSettingsLockedAsync(tenantA, projectId);
        await guard.ThrowIfDeleteBlockedAsync(tenantA, projectId);
    }

    [Fact]
    public async Task Archived_Project_Without_An_Active_Run_Is_Not_Blocked_By_This_Guard()
    {
        // Archival is enforced elsewhere (PROJECT_ARCHIVED); the guard's only
        // input is the run set. Pinning it here keeps the contract explicit.
        var tenantId = Guid.NewGuid();
        using var factory = CreateFactory();
        var projectId = Guid.NewGuid();
        SeedArchivedProject(factory, tenantId, projectId);
        var guard = new ProjectSettingsGuard(factory);

        Assert.False(await guard.HasActiveRunAsync(tenantId, projectId));
        await guard.ThrowIfSettingsLockedAsync(tenantId, projectId);
        await guard.ThrowIfDeleteBlockedAsync(tenantId, projectId);
    }

    [Fact]
    public async Task A_NoOp_Settings_Change_Is_Not_Blocked_When_No_Run_Is_Active()
    {
        var tenantId = Guid.NewGuid();
        using var factory = CreateFactory();
        var projectId = Guid.NewGuid();
        SeedProject(factory, tenantId, projectId, archived: false);
        var guard = new ProjectSettingsGuard(factory);

        // Re-applying the identical settings is a no-op; the guard still passes.
        await guard.ThrowIfSettingsLockedAsync(tenantId, projectId);
        await guard.ThrowIfSettingsLockedAsync(tenantId, projectId);
    }

    [Fact]
    public void Guard_Requires_A_Context_Factory()
    {
        Assert.Throws<ArgumentNullException>(() => new ProjectSettingsGuard(null!));
    }

    // ------------------------------------------------------------------
    // ProjectConfigHash — determinism
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigHash_Is_64_Lowercase_Hex_And_Key_Order_Independent()
    {
        var first = ProjectConfigHash.Compute("en", "es", """{"a":1,"b":{"y":true,"x":[1,2]}}""", null);
        var second = ProjectConfigHash.Compute("en", "es", """{"b":{"x":[1,2],"y":true},"a":1}""", null);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void ConfigHash_Empty_Input_Is_Stable_And_Distinct_From_Any_Content()
    {
        var empty = ProjectConfigHash.Compute("en", "es", "{}", null);

        Assert.Equal(empty, ProjectConfigHash.Compute("en", "es", "{}", ""));
        Assert.Equal(empty, ProjectConfigHash.Compute("en", "es", "{}", "   "));
        Assert.NotEqual(empty, ProjectConfigHash.Compute("en", "es", """{"a":1}""", null));
    }

    [Fact]
    public void ConfigHash_Trims_Languages_But_Keeps_Them_Significant()
    {
        Assert.Equal(
            ProjectConfigHash.Compute("en", "es", "{}", null),
            ProjectConfigHash.Compute("  en  ", "\tes\n", "{}", null));

        Assert.NotEqual(
            ProjectConfigHash.Compute("en", "es", "{}", null),
            ProjectConfigHash.Compute("EN", "es", "{}", null));
        Assert.NotEqual(
            ProjectConfigHash.Compute("en", "es", "{}", null),
            ProjectConfigHash.Compute("en", "ES", "{}", null));
    }

    [Fact]
    public void ConfigHash_Changes_For_Any_Single_Settings_Edit()
    {
        var baseline = ProjectConfigHash.Compute("en", "es", """{"profile":"a","level":1}""", """{"schemaVersion":1}""");

        Assert.NotEqual(baseline, ProjectConfigHash.Compute("en", "es", """{"profile":"b","level":1}""", """{"schemaVersion":1}"""));
        Assert.NotEqual(baseline, ProjectConfigHash.Compute("en", "es", """{"profile":"a","level":2}""", """{"schemaVersion":1}"""));
        Assert.NotEqual(baseline, ProjectConfigHash.Compute("en", "es", """{"profile":"a"}""", """{"schemaVersion":1}"""));
        Assert.NotEqual(baseline, ProjectConfigHash.Compute("en", "es", """{"profile":"a","level":1}""", """{"schemaVersion":2}"""));
        Assert.NotEqual(baseline, ProjectConfigHash.Compute("en", "es", """{"profile":"a","level":1}""", null));
        Assert.NotEqual(baseline, ProjectConfigHash.Compute("en", "fr", """{"profile":"a","level":1}""", """{"schemaVersion":1}"""));
    }

    [Fact]
    public void ConfigHash_Malformed_Settings_Fall_Back_To_An_Empty_Object()
    {
        var fromEmpty = ProjectConfigHash.Compute("en", "es", "{}", null);

        // A settings blob that does not parse contributes nothing rather than
        // failing the call, and a JSON `null` is normalised to the empty object.
        Assert.Equal(fromEmpty, ProjectConfigHash.Compute("en", "es", "{oops", null));
        Assert.Equal(fromEmpty, ProjectConfigHash.Compute("en", "es", "null", null));
    }

    [Fact]
    public void ConfigHash_Non_Object_Or_Primitive_Settings_Still_Contribute()
    {
        var fromEmpty = ProjectConfigHash.Compute("en", "es", "{}", null);

        // Anything that parses contributes to the hash; only unparsable text is
        // dropped. Parsed-but-unexpected shapes must not silently collapse.
        Assert.NotEqual(fromEmpty, ProjectConfigHash.Compute("en", "es", "[]", null));
        Assert.NotEqual(fromEmpty, ProjectConfigHash.Compute("en", "es", "42", null));
        Assert.Equal(
            ProjectConfigHash.Compute("en", "es", "42", null),
            ProjectConfigHash.Compute("en", "es", "42", null));
    }

    [Fact]
    public void ConfigHash_Malformed_ProcessingSettings_Fall_Back_But_Valid_Values_Still_Contribute()
    {
        var withoutProcessing = ProjectConfigHash.Compute("en", "es", "{}", null);
        var emptyObject = ProjectConfigHash.Compute("en", "es", "{}", "{}");

        // Unparsable processing settings degrade to the empty object, which is
        // itself a real, hashable value distinct from "absent".
        Assert.Equal(emptyObject, ProjectConfigHash.Compute("en", "es", "{}", "{oops"));
        Assert.Equal(emptyObject, ProjectConfigHash.Compute("en", "es", "{}", "null"));
        Assert.NotEqual(withoutProcessing, emptyObject);
        Assert.NotEqual(emptyObject, ProjectConfigHash.Compute("en", "es", "{}", """{"schemaVersion":1}"""));
    }

    [Fact]
    public void ConfigHash_Strips_Secrets_So_Keys_Never_Influence_The_Hash()
    {
        Assert.Equal(
            ProjectConfigHash.Compute("en", "es", """{"name":"x"}""", null),
            ProjectConfigHash.Compute("en", "es", """{"name":"x","apiKey":"value-a"}""", null));

        Assert.Equal(
            ProjectConfigHash.Compute("en", "es", "{}", """{"mode":"fast"}"""),
            ProjectConfigHash.Compute("en", "es", "{}", """{"mode":"fast","clientSecret":"value-b"}"""));
    }

    [Fact]
    public void ConfigHash_Rejects_Blank_Language_And_Settings_Arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectConfigHash.Compute(null!, "es", "{}", null));
        Assert.Throws<ArgumentNullException>(() => ProjectConfigHash.Compute("en", null!, "{}", null));
        Assert.Throws<ArgumentNullException>(() => ProjectConfigHash.Compute("en", "es", null!, null));

        Assert.Throws<ArgumentException>(() => ProjectConfigHash.Compute("", "es", "{}", null));
        Assert.Throws<ArgumentException>(() => ProjectConfigHash.Compute("  ", "es", "{}", null));
        Assert.Throws<ArgumentException>(() => ProjectConfigHash.Compute("en", "  ", "{}", null));
        Assert.Throws<ArgumentException>(() => ProjectConfigHash.Compute("en", "es", "  ", null));
    }

    // ------------------------------------------------------------------
    // Shared configuration hashing (ProjectConfigHash delegates to these)
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigHash_Strings_That_Parse_To_Non_Objects_Still_Contribute()
    {
        // A settings blob that is valid JSON but not an object still changes the
        // hash, so a caller cannot smuggle a config change past it.
        var fromEmpty = ProjectConfigHash.Compute("en", "es", "{}", null);

        Assert.NotEqual(fromEmpty, ProjectConfigHash.Compute("en", "es", "[]", null));
        Assert.NotEqual(fromEmpty, ProjectConfigHash.Compute("en", "es", "42", null));
        Assert.Equal(
            ProjectConfigHash.Compute("en", "es", "42", null),
            ProjectConfigHash.Compute("en", "es", "42", null));
    }

    [Fact]
    public void Execution_Snapshot_Treats_Absent_And_Empty_Inputs_Identically()
    {
        Assert.Equal(
            ExecutionSnapshotCalculator.Compute(null, null, null, null, null),
            ExecutionSnapshotCalculator.Compute(null, null, [], [], ""));
    }

    [Fact]
    public void Execution_Snapshot_Is_Order_Independent_And_Sensitive_To_Every_Input()
    {
        var runConfig = new Dictionary<string, object?> { ["pipeline"] = "v1" };
        var baseline = ExecutionSnapshotCalculator.Compute(runConfig, "route", ["a", "b"], ["p"], "policy");

        Assert.Equal(
            baseline,
            ExecutionSnapshotCalculator.Compute(runConfig, "route", ["b", "a"], ["p"], "policy"));

        Assert.NotEqual(baseline, ExecutionSnapshotCalculator.Compute(runConfig, "other", ["a", "b"], ["p"], "policy"));
        Assert.NotEqual(baseline, ExecutionSnapshotCalculator.Compute(runConfig, "route", ["a", "b"], ["q"], "policy"));
        Assert.NotEqual(baseline, ExecutionSnapshotCalculator.Compute(runConfig, "route", ["a", "b"], ["p"], "other"));
        Assert.NotEqual(
            baseline,
            ExecutionSnapshotCalculator.Compute(
                new Dictionary<string, object?> { ["pipeline"] = "v2" }, "route", ["a", "b"], ["p"], "policy"));
    }

    [Fact]
    public void Execution_Snapshot_Is_64_Lowercase_Hex_And_Strips_Run_Config_Secrets()
    {
        var hash = ExecutionSnapshotCalculator.Compute(
            new Dictionary<string, object?> { ["pipeline"] = "v1", ["apiToken"] = "value" },
            "route", ["a"], ["p"], "policy");

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(
            hash,
            ExecutionSnapshotCalculator.Compute(
                new Dictionary<string, object?> { ["pipeline"] = "v1" }, "route", ["a"], ["p"], "policy"));
    }

    [Fact]
    public void Configuration_Hash_Treats_Null_And_Blank_As_An_Empty_Object()
    {
        var fromEmpty = ConfigurationHashCalculator.Compute(new Dictionary<string, object?>());

        Assert.Equal(fromEmpty, ConfigurationHashCalculator.Compute(null));
        Assert.Equal(fromEmpty, ConfigurationHashCalculator.Compute("{}"));
        Assert.Equal(fromEmpty, ConfigurationHashCalculator.Compute("   "));
    }

    [Fact]
    public void Configuration_Hash_Treats_Non_Json_Text_As_A_Json_Scalar_Not_As_Empty()
    {
        var fromEmpty = ConfigurationHashCalculator.Compute(new Dictionary<string, object?>());

        // Unparsable text is still a distinct, hashable value: it must not
        // silently collapse onto the empty object.
        Assert.NotEqual(fromEmpty, ConfigurationHashCalculator.Compute("{not json"));
        Assert.Equal(
            ConfigurationHashCalculator.Compute("{not json"),
            ConfigurationHashCalculator.Compute("{not json"));
    }

    [Fact]
    public void Configuration_Hash_Parses_Json_Text_And_Ignores_Key_Order()
    {
        Assert.Equal(
            ConfigurationHashCalculator.Compute("""{"a":1,"b":[1,2]}"""),
            ConfigurationHashCalculator.Compute("""{"b":[1,2],"a":1}"""));
    }

    // ------------------------------------------------------------------
    // SpeakerVoiceAssignment — the policy binding a clone decision carries
    // ------------------------------------------------------------------

    [Fact]
    public void SpeakerVoiceAssignment_Keeps_The_Reason_And_Policy_Hash_Verbatim()
    {
        // Unlike preview text, the assignment reason and policy hash are stored
        // as given: they are the audit record of a decision, not free text.
        var reason = "  deterministic  ";
        var policyHash = new string('p', 64);
        var assignment = new SpeakerVoiceAssignment(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            reason, policyHash, Now);

        Assert.Equal(reason, assignment.AssignmentReason);
        Assert.Equal(policyHash, assignment.PolicyHash);
        Assert.Equal(Now, assignment.CreatedAt);
    }

    [Fact]
    public void SpeakerVoiceAssignment_Is_Immutable_After_Write()
    {
        var assignment = new SpeakerVoiceAssignment(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "deterministic", new string('p', 64), Now);

        assignment.Validate();
        assignment.Validate();

        // Only Validate() is callable: the policy hash behind a clone decision
        // cannot drift after the row is written.
        var mutators = typeof(SpeakerVoiceAssignment)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.Name is not ("Validate" or "ToString" or "GetHashCode" or "GetType" or "Equals"))
            .ToList();
        Assert.Empty(mutators);

        foreach (var property in typeof(SpeakerVoiceAssignment).GetProperties())
        {
            Assert.True(property.SetMethod is null || !property.SetMethod.IsPublic, property.Name);
        }
    }

    // ------------------------------------------------------------------
    // ProjectExceptions — codes, statuses, messages
    // ------------------------------------------------------------------

    [Fact]
    public void Project_Exceptions_Carry_The_Catalogued_Code_Status_And_Message()
    {
        var cases = new (AppException Exception, string Code, int Status)[]
        {
            (new LanguageImmutableException("Source/target languages are immutable."), ErrorCodes.LanguageImmutable, 400),
            (new SettingsLockedActiveRunException("Settings are locked."), ErrorCodes.SettingsLockedActiveRun, 409),
            (new ProjectNotFoundException("prj_missing was not found."), ErrorCodes.ProjectNotFound, 404),
            (new ProjectHasActiveRunException("Project has an active run."), ErrorCodes.ProjectHasActiveRun, 409),
            (new SettingsVersionConflictException("Settings version mismatch."), ErrorCodes.SettingsVersionConflict, 409),
        };

        foreach (var (exception, code, status) in cases)
        {
            Assert.Equal(code, exception.ErrorCode);
            Assert.Equal(status, exception.StatusCode);
            Assert.Equal(ErrorCodes.StatusFor(code), exception.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        }

        Assert.Equal("Settings are locked.", cases[1].Exception.Message);
        Assert.Equal("Settings version mismatch.", cases[4].Exception.Message);
    }

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    private static TestContextFactory CreateFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("ProjectSettingsGuardTests-" + Guid.NewGuid().ToString("N"))
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;
        return new TestContextFactory(options);
    }

    private static void SeedRun(
        TestContextFactory factory,
        Guid tenantId,
        Guid projectId,
        ProcessingRunStatus status)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<ProcessingRun>().Add(new ProcessingRun(
                Guid.NewGuid(), tenantId, projectId, 1, status,
                "pipeline-v1", new string('a', 64), new string('b', 64), new string('c', 64),
                Now, Now, Now, null));
            db.SaveChanges();
        }
    }

    private static void SeedProject(TestContextFactory factory, Guid tenantId, Guid projectId, bool archived)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<DubbingProject>().Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.Created, "{}", new string('d', 64),
                null, null, Now, Now,
                name: "Synthetic project",
                isArchived: archived,
                archivedAt: archived ? Now : null));
            db.SaveChanges();
        }
    }

    private static void SeedArchivedProject(TestContextFactory factory, Guid tenantId, Guid projectId)
        => SeedProject(factory, tenantId, projectId, archived: true);

    private sealed class TestAppDbContext : AppDbContext
    {
        public TestAppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }
    }

    private sealed class TestContextFactory : IStageExecutionContextFactory, IDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<TestAppDbContext> _all = [];

        public TestContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public TestAppDbContext CreateSetup() => new(_options);

        DbContext IStageExecutionContextFactory.CreateDbContext()
        {
            var context = CreateSetup();
            _all.Add(context);
            return context;
        }

        public void Dispose()
        {
            foreach (var context in _all)
            {
                context.Dispose();
            }
        }
    }
}
