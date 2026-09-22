using System.Text.Json;
using DubbingPlatform.Api.Filters;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Projects;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Project CRUD with archive semantics and settings-change guards.
/// <c>POST /api/v1/projects</c> creates (race-safe idempotency, 7-day
/// retention) and audits; <c>GET /api/v1/projects</c> lists with filters
/// (<c>status, ownerId, search, archived</c>), pagination (<c>page, pageSize</c>
/// default 20/max 100, over-max clamped with <c>clamped:true</c>) and sort
/// (<c>createdAt|updatedAt|name</c> + <c>sortDir asc|desc</c>, default
/// <c>createdAt desc</c>) in the envelope
/// <c>{ items, page, pageSize, total, sort, sortDir, hasMore, clamped }</c>
/// (archived excluded unless <c>archived=true|all</c>);
/// <c>GET /api/v1/projects/{id}</c> returns one row plus <c>ETag</c> with the
/// <c>SettingsVersion</c>; <c>PATCH /api/v1/projects/{id}</c> edits
/// name/description/settings only (unknown fields → 400, no partial apply;
/// <c>targetLanguage</c>/<c>sourceLanguage</c> present → 400
/// <c>LANGUAGE_IMMUTABLE</c>; <c>processingSettings</c> with an active run →
/// 409 <c>SETTINGS_LOCKED_ACTIVE_RUN</c>; <c>If-Match</c>/<c>settingsVersion</c>
/// mismatch → 409 <c>SETTINGS_VERSION_CONFLICT</c>; success bumps the version,
/// recomputes the config hash and audits old/new hash);
/// <c>POST .../archive|unarchive</c> are idempotent (already-archived → 200);
/// <c>DELETE</c> is a logical delete (<c>IsDeleted</c> shadow + archived state,
/// physical removal belongs to Task 037) returning 202 plus audit, blocked
/// with 409 <c>PROJECT_HAS_ACTIVE_RUN</c> while a run is active (hard delete
/// only via admin path, not here). Missing/deleted rows return 404
/// <c>PROJECT_NOT_FOUND</c>; cross-tenant ids return 403 <c>FORBIDDEN</c>
/// (established handler contract; see <c>ProjectService.GetAsync</c>).
/// Ids accept raw GUIDs and <c>prj_</c> prefixed ids; responses expose
/// <c>prj_</c>. Error examples: 400
/// <c>{ error: { code: "LANGUAGE_IMMUTABLE", ... } }</c>; 409
/// <c>{ error: { code: "SETTINGS_LOCKED_ACTIVE_RUN", ... } }</c>.
/// </summary>
[ApiController]
[Route("api/v1/projects")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class ProjectsController : ControllerBase
{
    private static readonly HashSet<string> PatchAllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "description",
        "settings",
        "processingSettings",
        "settingsVersion",
    };

    private readonly ProjectService _projects;

    public ProjectsController(ProjectService projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        _projects = projects;
    }

    /// <summary>
    /// Creates a project. Idempotent on <c>Idempotency-Key</c> (same key+body
    /// replays the stored 201; same key+different body → 409 CONFLICT).
    /// Duplicate names are allowed; empty/overlong names → 400.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateProjectRequest request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var settingsJson = request.Settings.HasValue
            ? request.Settings.Value.GetRawText()
            : "{}";
        var processingJson = request.ProcessingSettings.HasValue
            ? request.ProcessingSettings.Value.GetRawText()
            : null;

        var project = await _projects.CreateAsync(
            tenantId, request.SourceLanguage, request.TargetLanguage,
            settingsJson, User.GetSubject(), cancellationToken,
            request.Name, request.Description, processingJson, correlationId).ConfigureAwait(false);
        PlatformMetrics.ProjectStarted(tenantId);

        var response = ProjectResponse.From(project);
        return CreatedAtAction(nameof(GetById), new { projectId = response.Id }, response);
    }

    /// <summary>
    /// Lists projects with filters, pagination, and sort. Archived rows are
    /// excluded by default; pass <c>archived=true</c> for only-archived or
    /// <c>archived=all</c> for both.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(ProjectListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? ownerId,
        [FromQuery] string? search,
        [FromQuery] string? archived,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? sort,
        [FromQuery] string? sortDir,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var requestedSize = pageSize ?? PaginationParams.DefaultPageSize;
        var clamped = requestedSize > PaginationParams.MaxPageSize;

        ProjectStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ProjectStatus>(status.Trim(), true, out var parsed) || !Enum.IsDefined(typeof(ProjectStatus), parsed))
            {
                throw new Domain.Exceptions.DomainException($"Status must be a valid ProjectStatus (got '{status}').");
            }

            parsedStatus = parsed;
        }

        Guid? parsedOwner = null;
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            parsedOwner = TryParseUserId(ownerId.Trim());
            if (parsedOwner is null)
            {
                throw new Domain.Exceptions.DomainException($"OwnerId '{ownerId}' is not a valid identifier.");
            }
        }

        var query = new ProjectListQuery(
            parsedStatus, parsedOwner, search, archived, sort, sortDir,
            page ?? 1, requestedSize);
        var (items, total, sortOut, dirOut) = await _projects.ListFilteredAsync(tenantId, query, cancellationToken).ConfigureAwait(false);

        var normalized = new PaginationParams { Page = page ?? 1, PageSize = requestedSize };
        normalized.Normalize();
        var (safePage, safeSize) = normalized.Normalized();

        var responses = items.Select(ProjectResponse.From).ToList();
        return Ok(ProjectListResponse.Create(responses, safePage, safeSize, total, sortOut, dirOut, clamped));
    }

    /// <summary>
    /// Gets a project by id (raw GUID or <c>prj_</c>). Cross-tenant ids
    /// return 403 FORBIDDEN (not 404); deleted/missing return 404
    /// PROJECT_NOT_FOUND. Sets <c>ETag</c> to the settings version.
    /// </summary>
    [HttpGet("{projectId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById([FromRoute] string projectId, CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var id = PublicIdParser.ParseProjectId(projectId);
        var project = await _projects.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        Response.Headers.ETag = QuoteVersion(project.SettingsVersion);
        return Ok(ProjectResponse.From(project));
    }

    /// <summary>
    /// Patches name/description/settings only. See class docs for the guard,
    /// immutability, unknown-field, and concurrency rules.
    /// </summary>
    [HttpPatch("{projectId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Patch(
        [FromRoute] string projectId,
        [FromBody] JsonElement body,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var id = PublicIdParser.ParseProjectId(projectId);

        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new Domain.Exceptions.DomainException("Patch body must be a JSON object.");
        }

        foreach (var property in body.EnumerateObject())
        {
            if (string.Equals(property.Name, "targetLanguage", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "sourceLanguage", StringComparison.OrdinalIgnoreCase))
            {
                throw new LanguageImmutableException($"Language field '{property.Name}' is immutable after creation.");
            }

            if (!PatchAllowedKeys.Contains(property.Name))
            {
                throw new Domain.Exceptions.DomainException($"Unknown patch field '{property.Name}'. Allowed: name, description, settings, processingSettings, settingsVersion.");
            }
        }

        string? name = null;
        var hasName = false;
        if (body.TryGetProperty("name", out var nameProp) || TryGetPropertyCaseInsensitive(body, "name", out nameProp))
        {
            hasName = true;
            if (nameProp.ValueKind != JsonValueKind.String)
            {
                throw new Domain.Exceptions.DomainException("Patch field 'name' must be a string.");
            }

            name = nameProp.GetString();
        }

        string? description = null;
        var hasDescription = false;
        if (body.TryGetProperty("description", out var descProp) || TryGetPropertyCaseInsensitive(body, "description", out descProp))
        {
            if (descProp.ValueKind == JsonValueKind.Null || descProp.ValueKind == JsonValueKind.Undefined)
            {
                // JSON null is equivalent to absent in this API version
                // (clearing is unsupported); treat as no description change.
                hasDescription = false;
            }
            else
            {
                if (descProp.ValueKind != JsonValueKind.String)
                {
                    throw new Domain.Exceptions.DomainException("Patch field 'description' must be a string or null.");
                }

                hasDescription = true;
                description = descProp.GetString();
            }
        }

        string? settingsJson = null;
        if (body.TryGetProperty("settings", out var settingsProp) || TryGetPropertyCaseInsensitive(body, "settings", out settingsProp))
        {
            if (settingsProp.ValueKind == JsonValueKind.Null || settingsProp.ValueKind == JsonValueKind.Undefined)
            {
                throw new Domain.Exceptions.DomainException("Patch field 'settings' must be a JSON object.");
            }

            settingsJson = settingsProp.GetRawText();
        }

        string? processingJson = null;
        if (body.TryGetProperty("processingSettings", out var processingProp) || TryGetPropertyCaseInsensitive(body, "processingSettings", out processingProp))
        {
            if (processingProp.ValueKind == JsonValueKind.Null || processingProp.ValueKind == JsonValueKind.Undefined)
            {
                throw new Domain.Exceptions.DomainException("Patch field 'processingSettings' must be a JSON object.");
            }

            processingJson = processingProp.GetRawText();
        }

        int? bodyVersion = null;
        if (body.TryGetProperty("settingsVersion", out var versionProp) || TryGetPropertyCaseInsensitive(body, "settingsVersion", out versionProp))
        {
            if (versionProp.ValueKind != JsonValueKind.Number || !versionProp.TryGetInt32(out var parsed) || parsed < 1)
            {
                throw new Domain.Exceptions.DomainException("Patch field 'settingsVersion' must be an integer >= 1.");
            }

            bodyVersion = parsed;
        }

        var expected = ParseIfMatch(GetIfMatchHeader(), bodyVersion);

        var patched = await _projects.PatchAsync(
            tenantId, id, User.GetSubject(),
            hasName ? name : null,
            hasDescription ? description : null,
            settingsJson, processingJson, expected, correlationId, cancellationToken).ConfigureAwait(false);

        Response.Headers.ETag = QuoteVersion(patched.SettingsVersion);
        return Ok(ProjectResponse.From(patched));
    }

    /// <summary>
    /// Archives a project. Idempotent: already-archived returns 200.
    /// </summary>
    [HttpPost("{projectId}/archive")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Archive(
        [FromRoute] string projectId,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var id = PublicIdParser.ParseProjectId(projectId);
        var project = await _projects.ArchiveAsync(tenantId, id, User.GetSubject(), correlationId, cancellationToken).ConfigureAwait(false);
        return Ok(ProjectResponse.From(project));
    }

    /// <summary>
    /// Unarchives a project. Idempotent: already-active returns 200.
    /// </summary>
    [HttpPost("{projectId}/unarchive")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unarchive(
        [FromRoute] string projectId,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var id = PublicIdParser.ParseProjectId(projectId);
        var project = await _projects.UnarchiveAsync(tenantId, id, User.GetSubject(), correlationId, cancellationToken).ConfigureAwait(false);
        return Ok(ProjectResponse.From(project));
    }

    /// <summary>
    /// Logically deletes a project (202 + audit). Blocked with 409
    /// PROJECT_HAS_ACTIVE_RUN while a run is active. Subsequent reads return 404.
    /// </summary>
    [HttpDelete("{projectId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectOwner)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(DeleteProjectResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(
        [FromRoute] string projectId,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var id = PublicIdParser.ParseProjectId(projectId);
        await _projects.DeleteAsync(tenantId, id, User.GetSubject(), cancellationToken, correlationId).ConfigureAwait(false);
        PlatformMetrics.ProjectCancelled(tenantId);
        return Accepted(new DeleteProjectResponse(PublicIdParser.ToProjectId(id), true));
    }

    private static Guid? TryParseUserId(string raw)
    {
        if (Guid.TryParse(raw, out var guid) && guid != Guid.Empty)
        {
            return guid;
        }

        try
        {
            var (prefix, id) = Domain.Identity.PublicIdMapper.FromPublic(raw);
            if (string.Equals(prefix, Domain.Identity.PublicIdMapper.TenantUserPrefix, StringComparison.Ordinal))
            {
                return id;
            }
        }
        catch (Domain.Exceptions.DomainException)
        {
        }

        return null;
    }

    private string? GetIfMatchHeader()
    {
        if (Request.Headers.TryGetValue("If-Match", out var values))
        {
            return values.ToString();
        }

        return null;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static int? ParseIfMatch(string? header, int? bodyVersion)
    {
        int? fromHeader = null;
        if (!string.IsNullOrWhiteSpace(header) && !string.Equals(header.Trim(), "*", StringComparison.Ordinal))
        {
            var trimmed = header.Trim().TrimStart('W').TrimStart('w');
            trimmed = trimmed.Trim().Trim('"').Trim('\'').Trim();
            var first = trimmed.Split(',')[0].Trim().Trim('"').Trim('\'').Trim();
            if (!int.TryParse(first, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                throw new Domain.Exceptions.DomainException("If-Match header must contain a settings version integer >= 1.");
            }

            fromHeader = parsed;
        }

        if (fromHeader.HasValue && bodyVersion.HasValue && fromHeader.Value != bodyVersion.Value)
        {
            throw new SettingsVersionConflictException($"If-Match version {fromHeader.Value} does not match body settingsVersion {bodyVersion.Value}.");
        }

        return fromHeader ?? bodyVersion;
    }

    private static string QuoteVersion(int version)
    {
        return string.Concat("\"", version.ToString(System.Globalization.CultureInfo.InvariantCulture), "\"");
    }
}
