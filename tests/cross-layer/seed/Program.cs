// Task 040A: cross-layer harness seeder.
//
// Creates the synthetic environment the seam specs run against. There is no
// public provisioning endpoint by design, so the rig seeds through
// `AppDbContext` - the same path the 006-013 integration suites use.
//
// What it guarantees (040A R3 - deterministic, seeded):
//   - migrations are applied (idempotent),
//   - `reset` drops every tenant-scoped row first, so a prior run cannot leak
//     state into this one (040A edge case: "reset runs before seed, always"),
//   - the tenant, the active user, the project and its membership exist,
//   - the project is promoted to `MediaReady` so `POST /processing` accepts a
//     start, and
//   - the result is printed as one JSON line on stdout so the TypeScript harness
//     never has to re-query for ids it just created.
//
// Synthetic only: fixed GUIDs, `corr-*`-style external subjects and a
// `cross-layer.invalid` email domain (RFC 2606 reserved, so it can never be a
// real mailbox). No PII, no real credentials.
//
// Usage:
//   CrossLayerSeed --connection <conn> --tenant <guid> --user <guid>
//                  --project <guid> [--reset]
//
// Exit codes: 0 seeded, 1 usage error, 2 database error.

using System.Text.Json;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DubbingPlatform.CrossLayer.Seed;

/// <summary>Row counts the harness asserts after a run (040A R2 seam assertions).</summary>
internal static class Program
{
    private const string ExternalSubject = "cross-layer-owner";
    private const string Email = "owner@cross-layer.invalid";
    private const string DisplayName = "Cross Layer Owner";
    private const string ProjectName = "Cross Layer Pilot";

    /// <summary>
    /// Valid per <c>ProjectProcessingSettingsValidator</c>: object root,
    /// numeric <c>schemaVersion</c> equal to <c>SupportedSchemaVersion</c>,
    /// four optional non-blank strings of at most 64 chars, a
    /// <c>reviewThreshold</c> in [0, 1] and an optional glossary array. A seeded
    /// project with a null/blank settings blob is rejected at the API boundary,
    /// so a rig that omits it fails every processing-start seam with a 400 that
    /// names a JSON parse error rather than the missing seed data.
    /// </summary>
    private const string ProcessingSettings =
        """
        {
          "schemaVersion": 1,
          "sourceSeparationPolicy": "auto",
          "outputProfile": "standard",
          "timingStrictness": "balanced",
          "voicePolicy": "preserve",
          "reviewThreshold": 0.8,
          "glossary": [],
          "styleInstructions": "Cross-layer harness defaults."
        }
        """;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = SeedOptions.Parse(args);
            await SeedAsync(options).ConfigureAwait(false);
            return 0;
        }
        catch (SeedUsageException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            // Full chain: the top-level Npgsql message is routinely opaque
            // ("Exception while reading from stream"), so the harness log must
            // carry the inner exception or a rig failure is undiagnosable.
            await Console.Error.WriteLineAsync("CROSS_LAYER_SEED_FAILED: " + Describe(ex)).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(false);
            return 2;
        }
    }

    /// <summary>Renders an exception and every inner cause on one line.</summary>
    private static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var suffix = current is PostgresException pg ? $" [SQLSTATE {pg.SqlState}]" : string.Empty;
            parts.Add($"{current.GetType().Name}: {current.Message}{suffix}");
        }

        return string.Join(" <- ", parts);
    }

    private static DbContextOptions<AppDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;

    private static async Task SeedAsync(SeedOptions options)
    {
        // Frozen clock: 040A R3 requires deterministic seeded data, so every
        // timestamp comes from one captured instant rather than the wall clock.
        var now = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        using (TenantContext.BeginMaintenanceScope())
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        if (options.Reset)
        {
            await ResetAsync(options).ConfigureAwait(false);
        }

        // Tenant, user, project and membership go in one tenant-scoped unit of
        // work. Two details are load-bearing and were both found the hard way:
        //   * exactly one SaveChangesAsync - adding entities to a context that is
        //     then disposed persists nothing, which produced a rig that reported
        //     success while `tenant_users` stayed empty and every later login
        //     returned 401 INVALID_CREDENTIALS.
        //   * the membership probe keys on the (project, user) pair, never on
        //     `m.Id == Guid.NewGuid()` - that predicate is trivially false and
        //     would duplicate the row on every re-seed.
        using (TenantContext.BeginScope(options.TenantId))
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));

            if (!await context.Tenants.AnyAsync(t => t.Id == options.TenantId).ConfigureAwait(false))
            {
                context.Tenants.Add(new Tenant(
                    options.TenantId,
                    $"Cross-layer tenant {options.TenantId:N}",
                    $"cross-layer-{options.TenantId:N}",
                    now));
            }

            if (!await context.Set<TenantUser>().AnyAsync(u => u.Id == options.UserId).ConfigureAwait(false))
            {
                context.Set<TenantUser>().Add(new TenantUser(
                    options.UserId,
                    options.TenantId,
                    ExternalSubject,
                    Email,
                    DisplayName,
                    TenantUserStatus.Active,
                    now,
                    now));
            }

            if (!await context.Set<DubbingProject>().AnyAsync(p => p.Id == options.ProjectId).ConfigureAwait(false))
            {
                context.Set<DubbingProject>().Add(new DubbingProject(
                    options.ProjectId,
                    options.TenantId,
                    "en",
                    "es",
                    ProjectStatus.Created,
                    "{}",
                    new string('a', 64),
                    null,
                    null,
                    now,
                    now,
                    ProjectName,
                    "Cross-layer harness project.",
                    options.UserId,
                    options.UserId,
                    options.UserId,
                    isArchived: false,
                    archivedAt: null,
                    settingsVersion: 1,
                    processingSettingsJson: ProcessingSettings));
            }

            var memberExists = await context.Set<ProjectMembership>()
                .AnyAsync(m => m.ProjectId == options.ProjectId && m.UserId == options.UserId)
                .ConfigureAwait(false);
            if (!memberExists)
            {
                context.Set<ProjectMembership>().Add(new ProjectMembership(
                    Guid.NewGuid(),
                    options.TenantId,
                    options.ProjectId,
                    options.UserId,
                    ProjectRole.ProjectOwner,
                    options.UserId,
                    now));
            }

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        await PromoteToMediaReadyAsync(options, now).ConfigureAwait(false);
        await VerifyAsync(options).ConfigureAwait(false);

        var result = new
        {
            tenantId = options.TenantId.ToString("D"),
            userId = options.UserId.ToString("D"),
            projectId = options.ProjectId.ToString("D"),
            externalSubject = ExternalSubject,
            // The seeder never sees a token; the harness obtains one via
            // POST /auth/login, which is part of the seam it is proving.
            projectStatus = ProjectStatus.MediaReady.ToString(),
            seededAt = now.ToString("O"),
        };
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
    }

    /// <summary>
    /// Tables that must survive a reset.
    ///
    /// The transport tables are owned by the message bus rather than by the
    /// domain: the API is already running and holds live transport state, and
    /// truncating them would disturb consumers that are not part of a reset.
    ///
    /// <c>__EFMigrationsHistory</c> is EF's record of applied migrations.
    /// Truncating it makes the next <c>MigrateAsync</c> re-apply every migration
    /// onto a schema that already exists, which fails immediately with
    /// 42P07 "relation already exists" - the schema must be preserved and only
    /// its rows discarded.
    /// </summary>
    private static readonly string[] PreservedTables =
    [
        "inbox_state",
        "outbox_state",
        "outbox_message",
        "__EFMigrationsHistory",
    ];

    /// <summary>
    /// Wipes every domain table so a run cannot inherit state from the previous
    /// one (040A edge case: "Leftover state from prior run -> reset runs before
    /// seed, always").
    ///
    /// <para>
    /// This truncates rather than deleting by <c>tenant_id</c> for three
    /// reasons. The schema has 53 tenant-scoped tables and a hand-maintained
    /// delete list silently rots - an omitted table leaks state, and the seam
    /// then fails much later for the wrong reason (a stale
    /// <c>processing_runs</c> row made a fresh start return
    /// 409 RUN_ALREADY_ACTIVE). The foreign-key graph contains cycles, so no
    /// delete order satisfies it. And a truncate is atomic and fast.
    /// </para>
    ///
    /// <para>
    /// The table list is discovered from <c>information_schema</c> rather than
    /// hard-coded, and each identifier is re-validated before it reaches SQL
    /// because it is interpolated into a statement.
    /// </para>
    /// </summary>
    private static async Task ResetAsync(SeedOptions options)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));

            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var tables = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT table_name
                    FROM information_schema.tables
                    WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
                    ORDER BY table_name
                    """;

                await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    tables.Add(reader.GetString(0));
                }
            }

            var targets = tables
                .Where(name => !PreservedTables.Contains(name, StringComparer.Ordinal))
                .ToArray();

            if (targets.Length == 0)
            {
                throw new SeedVerificationException(
                    "Reset found no domain tables. The schema is missing, so seeding " +
                    "would recreate a rig that cannot prove anything.");
            }

            foreach (var table in targets)
            {
                if (!IsSafeIdentifier(table))
                {
                    throw new SeedVerificationException($"Refusing to truncate unexpected table name '{table}'.");
                }
            }

            var list = string.Join(", ", targets.Select(name => $"\"{name}\""));
            await context.Database
                .ExecuteSqlRawAsync($"TRUNCATE TABLE {list} RESTART IDENTITY CASCADE;")
                .ConfigureAwait(false);
        }
    }

    private static bool IsSafeIdentifier(string value) =>
        value.Length > 0
        && value.Length <= 63
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
        && !char.IsAsciiDigit(value[0]);

    /// <summary>
    /// Read-after-write proof that every row the harness depends on is really
    /// committed. Without this the seeder can print a success payload while the
    /// database is empty, and the failure surfaces three steps later as an
    /// unrelated 401. Failing here keeps the blast radius at the rig.
    /// </summary>
    private static async Task VerifyAsync(SeedOptions options)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));

            var checks = new (string Label, bool Ok)[]
            {
                ("tenant", await context.Tenants.AnyAsync(t => t.Id == options.TenantId).ConfigureAwait(false)),
                ("tenant_user", await context.Set<TenantUser>()
                    .AnyAsync(u => u.TenantId == options.TenantId && u.ExternalSubject == ExternalSubject)
                    .ConfigureAwait(false)),
                ("project", await context.Set<DubbingProject>().AnyAsync(p => p.Id == options.ProjectId).ConfigureAwait(false)),
                ("project_membership", await context.Set<ProjectMembership>()
                    .AnyAsync(m => m.ProjectId == options.ProjectId && m.UserId == options.UserId)
                    .ConfigureAwait(false)),
                ("source_media", await context.Set<MediaAsset>()
                    .AnyAsync(a => a.ProjectId == options.ProjectId)
                    .ConfigureAwait(false)),
            };

            var missing = checks.Where(c => !c.Ok).Select(c => c.Label).ToArray();
            if (missing.Length > 0)
            {
                throw new SeedVerificationException(
                    "Seed completed without persisting: missing " + string.Join(", ", missing) +
                    ". The rig is unusable; do not run seam specs against it.");
            }

            // Prove the reset actually happened. A surviving processing run makes
            // the next start return 409 RUN_ALREADY_ACTIVE, which reads like an
            // API bug and is really a rig bug, so it is worth catching here.
            var staleRuns = await context.Set<ProcessingRun>()
                .IgnoreQueryFilters()
                .CountAsync(r => r.TenantId == options.TenantId)
                .ConfigureAwait(false);
            if (staleRuns > 0)
            {
                throw new SeedVerificationException(
                    $"Reset left {staleRuns} processing run(s) behind for tenant " +
                    $"{options.TenantId}. A subsequent start will fail with " +
                    "409 RUN_ALREADY_ACTIVE.");
            }
        }
    }

    /// <summary>
    /// Attaches a committed source media row and flips the project to
    /// `MediaReady` so processing can start. Mirrors the promotion the 006-013
    /// integration suite uses: a real ingestion pipeline (FFprobe + an AI
    /// provider) is out of scope for a rig smoke, but the seam under test is
    /// start -> SSE, not ingestion.
    /// </summary>
    private static async Task PromoteToMediaReadyAsync(SeedOptions options, DateTimeOffset now)
    {
        var hash = new string('a', 64);

        using (TenantContext.BeginScope(options.TenantId))
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));
            var hasMedia = await context.Set<MediaAsset>()
                .AnyAsync(m => m.ProjectId == options.ProjectId)
                .ConfigureAwait(false);
            if (!hasMedia)
            {
                var contentId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                context.Set<ContentObject>().Add(new ContentObject(
                    contentId,
                    options.TenantId,
                    hash,
                    hash,
                    1024,
                    "video/mp4",
                    string.Concat(options.TenantId.ToString("N"), "/seed/source.mp4"),
                    ContentObjectStatus.Committed,
                    now,
                    now));
                context.Set<MediaAsset>().Add(new MediaAsset(
                    assetId,
                    options.TenantId,
                    options.ProjectId,
                    contentId,
                    "source.mp4",
                    "mp4",
                    "aac",
                    "h264",
                    1024,
                    2000,
                    48000,
                    2,
                    "stereo",
                    MediaAssetStatus.Valid,
                    null,
                    hash,
                    now));
                await context.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        using (TenantContext.BeginMaintenanceScope())
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE dubbing_projects SET status = {0}, updated_at = {1} WHERE id = {2}",
                ProjectStatus.MediaReady.ToString(),
                now,
                options.ProjectId).ConfigureAwait(false);
        }
    }
}

internal sealed class SeedUsageException : Exception
{
    public SeedUsageException(string message)
        : base(message)
    {
    }
}

internal sealed class SeedVerificationException : Exception
{
    public SeedVerificationException(string message)
        : base(message)
    {
    }
}

internal sealed record SeedOptions(
    string ConnectionString,
    Guid TenantId,
    Guid UserId,
    Guid ProjectId,
    bool Reset)
{
    public const string Usage =
        "Usage: CrossLayerSeed --connection <conn> --tenant <guid> --user <guid> --project <guid> [--reset]";

    public static SeedOptions Parse(string[] args)
    {
        string? connection = null;
        var tenant = Guid.Empty;
        var user = Guid.Empty;
        var project = Guid.Empty;
        var reset = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection" when i + 1 < args.Length:
                    connection = args[++i];
                    break;
                case "--tenant" when i + 1 < args.Length:
                    tenant = Guid.Parse(args[++i]);
                    break;
                case "--user" when i + 1 < args.Length:
                    user = Guid.Parse(args[++i]);
                    break;
                case "--project" when i + 1 < args.Length:
                    project = Guid.Parse(args[++i]);
                    break;
                case "--reset":
                    reset = true;
                    break;
                default:
                    throw new SeedUsageException($"Unrecognised argument '{args[i]}'. {Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new SeedUsageException($"--connection is required. {Usage}");
        }

        if (tenant == Guid.Empty || user == Guid.Empty || project == Guid.Empty)
        {
            throw new SeedUsageException($"--tenant, --user and --project must be non-empty GUIDs. {Usage}");
        }

        return new SeedOptions(connection, tenant, user, project, reset);
    }
}
