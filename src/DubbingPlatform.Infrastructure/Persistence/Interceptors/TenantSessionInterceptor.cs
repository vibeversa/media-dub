using System.Data.Common;
using System.Globalization;
using DubbingPlatform.Application.MultiTenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DubbingPlatform.Infrastructure.Persistence.Interceptors;

/// <summary>
/// PostgreSQL-only connection interceptor that publishes the current tenant to the
/// session via <c>app.tenant_id</c> so row-level security policies can enforce isolation.
/// Uses a parameterized <c>set_config</c> call (no string-concatenated SQL).
/// Maintenance scopes reset the setting; connections without a tenant reset it as well
/// so pooled connections fail closed (RLS policies deny when the setting is missing).
/// </summary>
public sealed class TenantSessionInterceptor : DbConnectionInterceptor
{
    private const string TenantSetting = "app.tenant_id";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetTenantSession(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetTenantSessionAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private static void SetTenantSession(DbConnection connection)
    {
        EnsureNpgsql(connection);

        var tenantId = TenantContext.IsMaintenance ? null : TenantContext.CurrentTenantId;
        if (tenantId is null || tenantId == Guid.Empty)
        {
            ResetTenantSession(connection);
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.tenant_id', $1, false)";
        var parameter = command.CreateParameter();
        parameter.Value = tenantId.Value.ToString("D", CultureInfo.InvariantCulture);
        command.Parameters.Add(parameter);
        command.ExecuteNonQuery();
    }

    private static async Task SetTenantSessionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        EnsureNpgsql(connection);

        var tenantId = TenantContext.IsMaintenance ? null : TenantContext.CurrentTenantId;
        if (tenantId is null || tenantId == Guid.Empty)
        {
            await ResetTenantSessionAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.tenant_id', $1, false)";
        var parameter = command.CreateParameter();
        parameter.Value = tenantId.Value.ToString("D", CultureInfo.InvariantCulture);
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ResetTenantSession(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "RESET app.tenant_id";
        command.ExecuteNonQuery();
    }

    private static async Task ResetTenantSessionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "RESET app.tenant_id";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureNpgsql(DbConnection connection)
    {
        var name = connection.GetType().FullName;
        if (name?.StartsWith("Npgsql.", StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException(
                $"TenantSessionInterceptor supports PostgreSQL (Npgsql) connections only, got '{name}'.");
        }
    }
}
