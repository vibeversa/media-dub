using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DubbingPlatform.Infrastructure.Health;

/// <summary>
/// The applied-versus-defined migration state, in the one shape the currency
/// rule needs. Pure data: every judgement about it is a static member, so the
/// rule can be exercised with no database and no Testcontainers.
/// </summary>
/// <param name="Applied">Migration ids in <c>__EFMigrationsHistory</c>, oldest first.</param>
/// <param name="Defined">Migration ids in the compiled migrations assembly, oldest first.</param>
public sealed record MigrationState(IReadOnlyList<string> Applied, IReadOnlyList<string> Defined)
{
    /// <summary>The newest applied migration id, or <c>null</c> on an empty history table.</summary>
    public string? AppliedHead => Applied.Count == 0 ? null : Applied[^1];

    /// <summary>The newest migration the running build knows about, or <c>null</c> when it carries none.</summary>
    public string? DefinedHead => Defined.Count == 0 ? null : Defined[^1];

    /// <summary>
    /// Defined migrations the database has not applied, in order. This is the
    /// set that decides whether a pod may take traffic: an API running against a
    /// schema older than its own build is the state a botched migration-first
    /// rollout leaves behind, and it is a 500 on the first request that touches
    /// a new column rather than a startup failure.
    /// </summary>
    public IReadOnlyList<string> Pending
    {
        get
        {
            var applied = new HashSet<string>(Applied, StringComparer.Ordinal);
            return Defined.Where(id => !applied.Contains(id)).ToArray();
        }
    }

    /// <summary>
    /// A build whose schema is ahead of the running code (a rolled-back pod
    /// against a contracted database). Never "current" — the process cannot
    /// know which of its queries relied on what was removed.
    /// </summary>
    public bool IsAhead => Applied.Except(Defined, StringComparer.Ordinal).Any();

    /// <summary>Currency: nothing pending, and the build is not ahead of the schema.</summary>
    public bool IsCurrent => Pending.Count == 0 && !IsAhead;

    /// <summary>One-line description for a health-check payload. Never contains a connection string.</summary>
    public string Describe() => IsCurrent
        ? $"schema is current at {AppliedHead ?? "no-migrations"}"
        : IsAhead
            ? $"schema is AHEAD of this build: applied head {AppliedHead ?? "none"}, this build defines {DefinedHead ?? "none"}"
            : $"{Pending.Count} migration(s) pending; applied head {AppliedHead ?? "none"}, this build defines {DefinedHead ?? "none"}";
}

/// <summary>
/// Reads the migration state from PostgreSQL. The only I/O in this pair of
/// files, so that <see cref="MigrationCurrencyCheck"/> stays a pure rule over
/// two string lists and can be tested hermetically.
/// </summary>
public interface IMigrationStateReader
{
    Task<MigrationState> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// EF Core implementation over the registered <see cref="IDbContextFactory{TContext}"/>.
/// Uses <c>GetAppliedMigrations</c>/<c>GetMigrations</c> rather than querying
/// <c>__EFMigrationsHistory</c> by hand so snake_case naming and the
/// <c>MigrationsHistoryTable</c> configuration both keep applying.
/// </summary>
public sealed class EfCoreMigrationStateReader : IMigrationStateReader
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public EfCoreMigrationStateReader(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<MigrationState> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        var defined = context.Database.GetMigrations().ToArray();
        return new MigrationState(applied, defined);
    }
}

/// <summary>
/// Readiness probe for <b>migration currency</b> (Task 043, R3). Tagged
/// <c>ready</c> only, never <c>live</c>.
///
/// Why it exists separately from <see cref="NpgSqlHealthCheck"/>: the database
/// being reachable says nothing about the schema matching the binary. A pod can
/// connect to a database whose schema is three migrations behind and report
/// itself healthy, which is exactly the state migration-job-first rollout exists
/// to prevent — and the only symptom is a 500 on the first request that touches
/// a new column. Failing readiness removes the pod from the Service instead, so
/// it never serves traffic.
///
/// Fail-closed in both directions. Pending migrations mean the build is ahead of
/// the schema; applied migrations the build does not define mean the schema is
/// ahead of the build, which is the contract-phase state and is also not current.
/// An unreadable history table is unhealthy rather than healthy: a probe that
/// cannot tell is not a probe that passed.
/// </summary>
public sealed class MigrationCurrencyCheck : IHealthCheck
{
    public const string Name = "migration-currency";

    private readonly IMigrationStateReader _reader;

    public MigrationCurrencyCheck(IMigrationStateReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        MigrationState state;
        try
        {
            state = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                $"Migration currency could not be read: {exception.GetType().Name}.");
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["appliedHead"] = state.AppliedHead ?? string.Empty,
            ["definedHead"] = state.DefinedHead ?? string.Empty,
            ["pending"] = state.Pending.Count,
            ["ahead"] = state.IsAhead,
        };

        // `Unhealthy(description, exception, data)` and not the two-argument
        // overload: there is no exception to attach here, the schema simply does
        // not match, and passing `null` is the honest value. The overload is
        // chosen explicitly so a future exception-carrying path does not change
        // the payload shape.
        return state.IsCurrent
            ? HealthCheckResult.Healthy(state.Describe(), data)
            : HealthCheckResult.Unhealthy(state.Describe(), exception: null, data);
    }
}
