using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Validation;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.TestFixtures;
using FluentAssertions;

namespace DubbingPlatform.TestFixtures.Tests;

/// <summary>
/// The fixture library's own proofs (Task 046 instruction 4: "factories build
/// valid graphs, scrubber passes"; R3, R4).
/// </summary>
/// <remarks>
/// <para>
/// These are the tests that make the harness itself trustworthy. Every other
/// suite in the repository assumes that a fixture is tenant-isolated,
/// deterministic, referentially sound and free of real PII; if that assumption
/// is wrong, those suites fail for reasons that have nothing to do with the code
/// they are testing. So this file does not test the product - it tests the thing
/// the product's tests stand on.
/// </para>
/// <para>
/// Every test is pure. There is no database and no container, which is
/// deliberate: the fixture proofs must run in the 042 basic-ci job that has no
/// Docker at all, and a proof that needs PostgreSQL is a proof that does not run
/// where it is cheapest to run.
/// </para>
/// </remarks>
public sealed class TestFixturesTests
{
    private static readonly Guid WorkerSeed = new("7f3a1c2b-4d5e-4f60-8a71-9b2c3d4e5f60");

    private static SyntheticEnvironment Environment() => SyntheticEnvironments.BuildForWorker(WorkerSeed);

    // ---------------------------------------------------------------- R3 -----

    [Fact]
    public void Factory_builds_a_tenant_with_one_user_per_seeded_role()
    {
        var environment = Environment();

        environment.Tenant.Tenant.Name.Should().NotBeNullOrWhiteSpace();
        environment.Tenant.Tenant.Slug.Should().NotBeNullOrWhiteSpace();
        environment.Tenant.Tenant.Id.Should().NotBe(Guid.Empty);

        environment.Tenant.Users.Should().HaveCount(SyntheticUsers.SeededRoles.Count);
        environment.Tenant.Users.Select(user => user.ExternalSubject)
            .Should().OnlyHaveUniqueItems()
            // Compared against the declared subjects rather than against strings
            // rebuilt here: a hardcoded "harness-owner" keeps passing after
            // `SyntheticUsers.OwnerSubject` changes, which is exactly the drift
            // this assertion exists to catch.
            .And.BeEquivalentTo(
            [
                SyntheticUsers.AdminSubject,
                SyntheticUsers.OwnerSubject,
                SyntheticUsers.EditorSubject,
                SyntheticUsers.ReviewerSubject,
                SyntheticUsers.ViewerSubject,
            ]);

        environment.Tenant.Admin.ExternalSubject.Should().Be(SyntheticUsers.AdminSubject);
        environment.Tenant.Admin.Email.Should().Be(
            SyntheticUsers.SyntheticEmail(SyntheticUsers.AdminSubject));
    }

    [Fact]
    public void Factory_builds_a_tenant_that_isolates_by_seed()
    {
        var first = Environment();
        var second = SyntheticEnvironments.BuildForWorker(WorkerSeed);

        // Two workers must produce the same graph for the same seed (that is what
        // makes a visual baseline and a re-run of a failed test meaningful).
        second.Tenant.Tenant.Id.Should().Be(first.Tenant.Tenant.Id);
        second.AnchorProject.ProjectId.Should().Be(first.AnchorProject.ProjectId);
        second.AnchorProject.Segment.Id.Should().Be(first.AnchorProject.Segment.Id);

        // ...and two DIFFERENT seeds must collide on nothing at all. This is
        // 046's "parallel workers share tenant" edge case, asserted rather than
        // assumed: if the derivation ever lost its tenant prefix, this is the test
        // that would say so.
        var other = SyntheticEnvironments.BuildForWorker(Guid.NewGuid());

        other.Tenant.Tenant.Id.Should().NotBe(first.Tenant.Tenant.Id);
        other.Tenant.Tenant.Slug.Should().NotBe(first.Tenant.Tenant.Slug);
        other.AnchorProject.ProjectId.Should().NotBe(first.AnchorProject.ProjectId);
        other.AnchorProject.SourceContent.StorageKey.Should().NotBe(first.AnchorProject.SourceContent.StorageKey);
        other.AnchorProject.SourceContent.ContentHash.Should().NotBe(first.AnchorProject.SourceContent.ContentHash);

        CollectIds(other).Should().NotIntersectWith(CollectIds(first));
    }

    [Fact]
    public void Every_row_carries_the_tenant_it_belongs_to()
    {
        var environment = Environment();
        var rows = environment.AllProjectRows();

        rows.Should().NotBeEmpty();

        foreach (var row in rows)
        {
            // Read by reflection rather than through an interface: the domain
            // entities have no common `ITenantScoped` contract, and adding one to
            // production code to make a test convenient is the wrong direction.
            // It fails closed - a fixture row type without a readable `TenantId`
            // throws here instead of quietly passing the assertion.
            TenantIdOf(row).Should().Be(
                environment.Tenant.TenantId,
                $"{row.GetType().Name} is tenant-scoped");
        }
    }

    [Fact]
    public void Project_graph_resolves_every_foreign_key()
    {
        var environment = Environment();
        var graph = environment.AnchorProject;

        graph.Project.OwnerUserId.Should().NotBeNull();
        graph.Project.TenantId.Should().Be(graph.TenantId);

        graph.Run.ProjectId.Should().Be(graph.ProjectId);

        graph.SourceMedia.ProjectId.Should().Be(graph.ProjectId);
        graph.SourceMedia.ContentObjectId.Should().Be(graph.SourceContent.Id);

        graph.Segment.ProjectId.Should().Be(graph.ProjectId);
        graph.Segment.RunId.Should().Be(graph.Run.Id);
        graph.Segment.DurationMs.Should().Be(graph.Segment.EndMs - graph.Segment.StartMs);

        graph.TranscriptVersion.SegmentId.Should().Be(graph.Segment.Id);
        graph.TranslationVersion.SegmentId.Should().Be(graph.Segment.Id);

        graph.Selection.SegmentId.Should().Be(graph.Segment.Id);
        graph.Selection.SelectedTranscriptVersionId.Should().Be(graph.TranscriptVersion.Id);
        graph.Selection.SelectedTranslationVersionId.Should().Be(graph.TranslationVersion.Id);

        graph.Speaker.ProjectId.Should().Be(graph.ProjectId);
        graph.Voices.Should().HaveCount(2, "a reassignment needs somewhere to go");
        graph.VoiceAssignment.SpeakerId.Should().Be(graph.Speaker.Id);
        graph.Voices.Select(voice => voice.Id).Should().Contain(graph.VoiceAssignment.VoiceProfileId);

        graph.ReviewItem.SegmentId.Should().Be(graph.Segment.Id);
        graph.ReviewItem.ScopeId.Should().Be(graph.Segment.Id.ToString("D"));

        graph.ExportJob.ProcessingRunId.Should().Be(graph.Run.Id);
        graph.ExportJob.CompletenessJson.Should().Contain("\"complete\":true");
        graph.ExportArtifactLink.ExportJobId.Should().Be(graph.ExportJob.Id);
        graph.ExportArtifactLink.ArtifactId.Should().Be(graph.ExportArtifact.Id);
        graph.ExportJob.ArtifactIdRef.Should().Be(graph.ExportArtifact.Id.ToString("D"));
        graph.ExportArtifact.ContentObjectId.Should().Be(graph.ExportContent.Id);
    }

    [Fact]
    public void Both_projects_carry_a_completed_anchor_run_and_differ_by_project_id()
    {
        var environment = Environment();

        // A start is refused with 409 RUN_ALREADY_ACTIVE only when the project
        // already has a run in an ACTIVE status, so a Completed anchor is
        // invisible to POST /processing. An in-flight anchor would fail every
        // processing test at the API boundary instead of at its assertion.
        environment.AnchorProject.Run.Status.Should().Be(ProcessingRunStatus.Completed);
        environment.StartableProject.Run.Status.Should().Be(ProcessingRunStatus.Completed);

        // A run-less project is not an option: SpeechSegment.Validate requires a
        // non-empty RunId, so the "startable" variant has to differ by project id
        // rather than by having no run. Asserted so a future refactor cannot
        // quietly reintroduce the run-less variant.
        environment.StartableProject.Segment.RunId.Should().Be(environment.StartableProject.Run.Id);
        environment.StartableProject.ProjectId.Should().NotBe(environment.AnchorProject.ProjectId);
        environment.StartableProject.Segment.Id.Should().NotBe(environment.AnchorProject.Segment.Id);
        environment.StartableProject.Run.Id.Should().NotBe(environment.AnchorProject.Run.Id);
    }

    [Fact]
    public void Selection_starts_at_one_so_a_stale_writer_has_something_to_hold()
    {
        // 0 would leave a stale-edit test with no version to make stale: the first
        // edit bumps it to 1 and there is nothing older to conflict with.
        SyntheticProjects.InitialSelectionVersion.Should().Be(1);
        Environment().AnchorProject.Selection.SelectionVersion.Should().Be(1);
    }

    /// <summary>
    /// Parses the fixture's blob with the API's own validator, rather than
    /// asserting a shape this file re-declared.
    /// </summary>
    /// <remarks>
    /// An earlier draft asserted <c>schemaVersion == 1</c> and
    /// <c>reviewThreshold in [0,1]</c> by hand. That is a restatement of
    /// <c>ProjectProcessingSettingsValidator</c> written by someone who had not
    /// read it, and it would have kept passing after the validator changed - the
    /// same defect class as the hardcoded role list. Calling the validator makes
    /// the fixture's settings a property of the API, so a settings change that
    /// the fixture does not satisfy fails here instead of as a 400 at the API
    /// boundary that reads like a product defect.
    /// </remarks>
    [Fact]
    public void Processing_settings_satisfy_the_api_validator()
    {
        var graph = Environment().AnchorProject;

        graph.Project.ProcessingSettingsJson.Should().Be(SyntheticProjects.ProcessingSettingsJson);

        // The live validator, with its real message codes, so a regression names
        // the rule that broke instead of "expected true to be true".
        var result = new ProjectProcessingSettingsValidator().Validate(graph.Project.ProcessingSettingsJson);

        result.IsValid.Should().BeTrue(
            "the fixture's processingSettingsJson must satisfy ProjectProcessingSettingsValidator; " +
            "errors: " + string.Join("; ", result.Errors.Select(error => $"{error.ErrorCode}: {error.ErrorMessage}")));

        // And a negative control on the same validator. `BeValidJson` alone is NOT
        // a sufficient control - it only answers "is this parseable JSON", so a
        // blob with an unsupported schema version passes it. The whole validator
        // is what rejects that, and without this assertion a validator that
        // returned true unconditionally would pass the check above.
        var validator = new ProjectProcessingSettingsValidator();

        ProjectProcessingSettingsValidator.BeValidJson("not json").Should().BeFalse();
        ProjectProcessingSettingsValidator.BeValidJson("   ").Should().BeFalse();
        ProjectProcessingSettingsValidator
            .BeValidJson(SyntheticProjects.ProcessingSettingsJson)
            .Should().BeTrue("the fixture blob is what the validator is being fed");

        // The schema-version rule is a separate rule from JSON validity, so it has
        // to be exercised through `Validate`, not through `BeValidJson`.
        validator.Validate("{\"schemaVersion\": 99}").IsValid.Should().BeFalse(
            "an unsupported schema version is the documented rejection path");
        validator.Validate("{\"schemaVersion\": 99}").Errors
            .Should().Contain(error => error.ErrorCode == ProjectProcessingSettingsValidator.UnsupportedVersionCode);

        validator.Validate("{\"schemaVersion\": 1, \"reviewThreshold\": 5}").IsValid.Should().BeFalse(
            "reviewThreshold outside [0, 1] is outside the documented shape");

        validator.Validate(string.Empty).IsValid.Should().BeFalse(
            "an empty blob is the rejection a fixture with no settings hits");
    }

    [Fact]
    public void Fixture_ids_are_deterministic_and_unique_within_a_graph()
    {
        var graph = Environment().AnchorProject;

        CollectIds(graph).Should().OnlyHaveUniqueItems();

        // Same seed, same label, same id - three times over.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            FixtureIds.Derive(WorkerSeed, "segment", graph.ProjectId)
                .Should().Be(graph.Segment.Id);
        }
    }

    [Fact]
    public void Seeded_roles_match_the_platform_role_declarations()
    {
        // The fixture seeds a role per name the API authorises. A fixture that
        // drifted from `Roles.All` would produce a session whose claims the
        // endpoints reject, and the resulting 403 reads like an authorisation bug.
        SyntheticUsers.SeededRoles.Should().OnlyContain(role => Roles.IsKnown(role));

        // `Service` is a machine role with no interactive login path, so it is
        // deliberately absent from the seeded users. Asserted rather than assumed:
        // someone adding it later should have to think about it.
        SyntheticUsers.SeededRoles.Should().NotContain(SyntheticUsers.ServiceRole);
        SyntheticUsers.SeededRoles.Should().HaveCount(Roles.All.Length - 1);

        foreach (var role in SyntheticUsers.SeededRoles)
        {
            // The mapping must exist, and the membership it produces must be
            // reproducible: a caller who picks a fresh `Guid.NewGuid()` per call
            // creates a duplicate membership on re-seed, which shows up as a user
            // holding two roles and a permission check that passes for the wrong
            // reason.
            var projectRole = SyntheticProjectGraphBuilder.ToProjectRole(role);

            var graph = Environment().AnchorProject;
            var first = SyntheticEnvironments.Membership(graph, graph.OwnerUserId, projectRole, graph.OwnerUserId);
            var second = SyntheticEnvironments.Membership(graph, graph.OwnerUserId, projectRole, graph.OwnerUserId);

            first.Role.Should().Be(projectRole);
            first.TenantId.Should().Be(graph.TenantId);
            first.ProjectId.Should().Be(graph.ProjectId);
            first.Id.Should().Be(second.Id);
        }
    }

    [Fact]
    public void Clock_is_frozen_and_distinct_instants_are_available_for_sorting()
    {
        FixtureClock.Now.Should().Be(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        FixtureClock.OneHourEarlier.Should().BeBefore(FixtureClock.Now);
        FixtureClock.OneHourLater.Should().BeAfter(FixtureClock.Now);

        // The project list orders by CreatedAt with no tiebreaker, so two projects
        // sharing an instant come back in plan-dependent order. Fixed as data.
        Environment().AnchorProject.Project.CreatedAt
            .Should().NotBe(Environment().StartableProject.Project.CreatedAt);
    }

    // ---------------------------------------------------------------- R4 -----

    [Fact]
    public void Scrubber_passes_on_every_synthetic_fixture_field()
    {
        var environment = Environment();
        var scanned = 0;

        foreach (var (label, value) in EnumerateStrings(environment))
        {
            PiiScrubber.AssertClean(value, label);
            scanned++;
        }

        // A rule that never matched anything would report a "clean" result for a
        // codebase with no fixtures in it. This is the control: if the enumeration
        // silently stopped visiting fields, the count collapses and so does the
        // proof.
        scanned.Should().BeGreaterThan(60, "the scrubber must actually visit the fixtures, not a handful");
    }

    [Fact]
    public void Scrubber_flags_a_real_address_but_not_a_reserved_one()
    {
        PiiScrubber.IsReservedAddress("synthetic+harness-owner@fixtures.invalid").Should().BeTrue();
        PiiScrubber.IsReservedAddress("someone@tenant.example").Should().BeTrue();
        PiiScrubber.IsReservedAddress("person@gmail.com").Should().BeFalse();
        PiiScrubber.IsReservedAddress("person@company.co.uk").Should().BeFalse();

        PiiScrubber.Scan("owner@gmail.com", "fixture").Should()
            .ContainSingle(finding => finding.Rule == "email-non-reserved");

        PiiScrubber.Scan("owner@fixtures.invalid", "fixture").Should().BeEmpty();
    }

    [Fact]
    public void Scrubber_flags_secret_shapes_by_structure_not_by_value()
    {
        // Anchored on shape, so a rule listing today's leaked tokens - which would
        // pass the day someone rotated them, the wrong direction for a gate - is
        // not what this is.
        PiiScrubber.Scan("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", "log")
            .Should().Contain(finding => finding.Rule == "secret-jwt");

        PiiScrubber.Scan("Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345", "log")
            .Should().Contain(finding => finding.Rule == "secret-bearer-header");

        PiiScrubber.Scan("Password=CHANGE_ME;SSL Mode=Disable", "compose")
            .Should().NotContain(finding => finding.Rule == "secret-connection-string-password");

        PiiScrubber.Scan("Password=hunter2-do-not-ship;SSL Mode=Disable", "compose")
            .Should().Contain(finding => finding.Rule == "secret-connection-string-password");

        PiiScrubber.Scan("-----BEGIN RSA PRIVATE KEY-----", "fixture")
            .Should().Contain(finding => finding.Rule == "secret-private-key");

        PiiScrubber.Scan("/progress/stream?access_token=abcdefghijklmnop", "url")
            .Should().Contain(finding => finding.Rule == "secret-bearer-query");
    }

    [Fact]
    public void Scrubber_masks_its_evidence_so_a_report_is_not_a_second_copy()
    {
        var findings = PiiScrubber.Scan("person@gmail.com and AKIAIOSFODNN7EXAMPLE", "fixture");

        findings.Should().HaveCountGreaterThan(1);
        findings.Should().OnlyContain(finding => finding.Evidence.Length <= 15);
        findings.Should().NotContain(finding => finding.Evidence.Contains("AKIAIOSFODNN7EXAMPLE"));
        findings.Should().NotContain(finding => finding.Evidence.Contains("gmail.com"));
    }

    [Fact]
    public void Scrubber_throws_with_rule_and_location_but_not_the_secret()
    {
        var act = () => PiiScrubber.AssertClean("contact person@gmail.com", "fixtures/README.md");

        var thrown = act.Should().Throw<PiiScrubber.PiiScrubberException>().Which;
        thrown.Message.Should().Contain("fixtures/README.md");
        thrown.Message.Should().Contain("email-non-reserved");
        thrown.Message.Should().NotContain("person@gmail.com");
        thrown.Findings.Should().ContainSingle();
    }

    [Fact]
    public void Scrubber_accepts_the_reserved_email_domain_the_fixtures_actually_use()
    {
        // If this fails, `SyntheticTenants.ReservedEmailDomain` and
        // `PiiScrubber.ReservedDomains` have drifted, and every fixture email
        // would be a finding that the tests above would have to whitelist - which
        // is how a scrubber gets disabled.
        //
        // The assertions read both sides rather than restating them: a hardcoded
        // "invalid" here would keep passing after the constant changed, which is
        // the whole failure this test exists to prevent.
        var tld = SyntheticTenants.ReservedEmailDomain.Split('.')[^1];
        PiiScrubber.ReservedDomains.Should().Contain(tld);

        var address = SyntheticUsers.SyntheticEmail(SyntheticUsers.OwnerSubject);
        PiiScrubber.IsReservedAddress(address).Should().BeTrue();
        PiiScrubber.Scan(address, "SyntheticUsers.SyntheticEmail").Should().BeEmpty();
    }

    // ------------------------------------------------------------ helpers ----

    private static IReadOnlyList<object> CollectIds(SyntheticEnvironment environment) =>
        [.. environment.Tenant.Users.Select(user => (object)user.Id), .. CollectIds(environment.AnchorProject)];

    private static IReadOnlyList<object> CollectIds(SyntheticProjectGraph graph) =>
    [
        graph.Project.Id,
        graph.SourceMedia.Id,
        graph.SourceContent.Id,
        .. graph.Memberships.Select(membership => membership.Id),
        graph.Run.Id,
        graph.Segment.Id,
        graph.TranscriptVersion.Id,
        graph.TranslationVersion.Id,
        graph.Selection.Id,
        graph.Speaker.Id,
        .. graph.Voices.Select(voice => voice.Id),
        graph.VoiceAssignment.Id,
        graph.ReviewItem.Id,
        graph.Notification.Id,
        graph.ExportJob.Id,
        graph.ExportArtifactLink.Id,
        graph.ExportArtifact.Id,
        graph.ExportContent.Id,
    ];

    /// <summary>
    /// Every string reachable from an environment, with a label naming where it
    /// came from, so a scrubber failure points at a field.
    /// </summary>
    /// <remarks>
    /// Written as an explicit walk rather than reflection over every property.
    /// Reflection would also visit ids and enums (harmless) but would silently
    /// miss a nested collection added later, and a scrubber that quietly stops
    /// covering new fields is worse than no scrubber.
    /// </remarks>
    private static IEnumerable<(string Label, string? Value)> EnumerateStrings(SyntheticEnvironment environment)
    {
        var tenant = environment.Tenant;
        yield return ("tenant.Name", tenant.Tenant.Name);
        yield return ("tenant.Slug", tenant.Tenant.Slug);

        foreach (var user in tenant.Users)
        {
            yield return ($"user[{user.ExternalSubject}].ExternalSubject", user.ExternalSubject);
            yield return ($"user[{user.ExternalSubject}].Email", user.Email);
            yield return ($"user[{user.ExternalSubject}].DisplayName", user.DisplayName);
        }

        foreach (var (label, graph) in new[] { ("anchor", environment.AnchorProject), ("startable", environment.StartableProject) })
        {
            yield return ($"{label}.project.Name", graph.Project.Name);
            yield return ($"{label}.project.Description", graph.Project.Description);
            yield return ($"{label}.project.SettingsJson", graph.Project.SettingsJson);
            yield return ($"{label}.project.ProcessingSettingsJson", graph.Project.ProcessingSettingsJson);
            yield return ($"{label}.sourceMedia.FileName", graph.SourceMedia.FileName);
            yield return ($"{label}.sourceMedia.ContentHash", graph.SourceMedia.ContentHash);
            yield return ($"{label}.sourceContent.StorageKey", graph.SourceContent.StorageKey);
            yield return ($"{label}.sourceContent.ContentHash", graph.SourceContent.ContentHash);
            yield return ($"{label}.transcript.Text", graph.TranscriptVersion.Text);
            yield return ($"{label}.transcript.Provider", graph.TranscriptVersion.Provider);
            yield return ($"{label}.transcript.Model", graph.TranscriptVersion.Model);
            yield return ($"{label}.translation.PrimaryText", graph.TranslationVersion.PrimaryText);
            yield return ($"{label}.translation.Provider", graph.TranslationVersion.Provider);
            yield return ($"{label}.translation.Model", graph.TranslationVersion.Model);
            yield return ($"{label}.speaker.DisplayName", graph.Speaker.DisplayName);
            yield return ($"{label}.speaker.SpeakerKey", graph.Speaker.SpeakerKey);
            yield return ($"{label}.review.Reason", graph.ReviewItem.Reason);
            yield return ($"{label}.review.PayloadJson", graph.ReviewItem.PayloadJson);
            yield return ($"{label}.review.ScopeId", graph.ReviewItem.ScopeId);
            yield return ($"{label}.notification.Title", graph.Notification.Title);
            yield return ($"{label}.notification.Body", graph.Notification.Body);
            yield return ($"{label}.notification.ResourceId", graph.Notification.ResourceId);
            yield return ($"{label}.export.CompletenessJson", graph.ExportJob.CompletenessJson);
            yield return ($"{label}.export.ArtifactIdRef", graph.ExportJob.ArtifactIdRef);
            yield return ($"{label}.exportContent.StorageKey", graph.ExportContent.StorageKey);

            foreach (var voice in graph.Voices)
            {
                yield return ($"{label}.voice.VoiceId", voice.VoiceId);
                yield return ($"{label}.voice.Provider", voice.Provider);
            }

            foreach (var membership in graph.Memberships)
            {
                yield return ($"{label}.membership.Role", membership.Role.ToString());
            }
        }
    }

    /// <summary>
    /// Reads the <c>TenantId</c> of a fixture row, failing closed when the type
    /// does not expose one.
    /// </summary>
    /// <param name="row">The row to read.</param>
    private static Guid TenantIdOf(object row)
    {
        var property = row.GetType().GetProperty("TenantId")
            ?? throw new InvalidOperationException(
                $"{row.GetType().Name} has no readable TenantId. A fixture row that is not " +
                "tenant-scoped would leak across tenants, which is exactly the cross-test " +
                "leakage Task 046 forbids.");

        return (Guid)property.GetValue(row)!;
    }
}
