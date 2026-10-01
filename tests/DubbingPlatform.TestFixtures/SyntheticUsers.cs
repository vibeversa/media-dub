using System.Globalization;

using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.TestFixtures;

/// <summary>
/// Builds synthetic users for every role the platform authorises
/// (Task 046 instruction 2, "seeded auth for all roles").
/// </summary>
/// <remarks>
/// <para>
/// The role set mirrors <c>DubbingPlatform.Application.Authorization.Roles.All</c>
/// exactly. That is a deliberate coupling with a documented reason: an E2E
/// fixture that seeds "an admin" but not the platform's admin role name proves
/// nothing about authorisation, and a fixture that invents a role name the API
/// does not recognise produces a 403 that reads like a product defect. When
/// <c>Roles.All</c> changes, this list must change with it -
/// <c>TestFixturesTests</c> asserts the two agree.
/// </para>
/// <para>
/// Every user is <see cref="TenantUserStatus.Active"/>. A disabled user is
/// built on demand by <see cref="BuildDisabled"/> rather than being part of the
/// default set, because a disabled user in the standard set would silently
/// exclude itself from every role matrix assertion.
/// </para>
/// </remarks>
public static class SyntheticUsers
{
    /// <summary>External subject of the tenant administrator.</summary>
    public const string AdminSubject = "harness-admin";

    /// <summary>External subject of the project owner.</summary>
    public const string OwnerSubject = "harness-owner";

    /// <summary>External subject of the project editor.</summary>
    public const string EditorSubject = "harness-editor";

    /// <summary>External subject of the reviewer.</summary>
    public const string ReviewerSubject = "harness-reviewer";

    /// <summary>External subject of the project viewer.</summary>
    public const string ViewerSubject = "harness-viewer";

    // The role-name keys `SeededRoles` is built from, named so a seeder can switch
    // on them. Without them a caller would write `"TenantAdmin"` as a bare literal,
    // and a role rename would leave the switch silently falling through to its
    // throw arm - or worse, to a wrong-but-valid arm.

    /// <summary>The `TenantAdmin` entry in <see cref="SeededRoles"/>.</summary>
    public const string AdminSubjectRole = "TenantAdmin";

    /// <summary>The `ProjectOwner` entry in <see cref="SeededRoles"/>.</summary>
    public const string OwnerSubjectRole = "ProjectOwner";

    /// <summary>The `ProjectEditor` entry in <see cref="SeededRoles"/>.</summary>
    public const string EditorSubjectRole = "ProjectEditor";

    /// <summary>The `Reviewer` entry in <see cref="SeededRoles"/>.</summary>
    public const string ReviewerSubjectRole = "Reviewer";

    /// <summary>The `ProjectViewer` entry in <see cref="SeededRoles"/>.</summary>
    public const string ViewerSubjectRole = "ProjectViewer";

    /// <summary>
    /// The <c>Service</c> role has no interactive user. It is issued to a
    /// machine identity, and a fixture that minted a login for it would imply a
    /// human sign-in path the product deliberately does not have.
    /// </summary>
    public const string ServiceRole = "Service";

    /// <summary>
    /// The six roles the harness seeds, in the platform's own declaration order.
    /// </summary>
    public static readonly IReadOnlyList<string> SeededRoles =
    [
        "TenantAdmin",
        "ProjectOwner",
        "ProjectEditor",
        "Reviewer",
        "ProjectViewer",
    ];

    /// <summary>Builds the administrator for <paramref name="tenantId"/>.</summary>
    /// <param name="tenantSeed">Seed for id derivation.</param>
    /// <param name="tenantId">Tenant the user belongs to.</param>
    public static TenantUser BuildAdmin(Guid tenantSeed, Guid tenantId) =>
        Build(tenantSeed, tenantId, AdminSubject, "Harness Administrator");

    /// <summary>Builds the project owner for <paramref name="tenantId"/>.</summary>
    /// <param name="tenantSeed">Seed for id derivation.</param>
    /// <param name="tenantId">Tenant the user belongs to.</param>
    public static TenantUser BuildOwner(Guid tenantSeed, Guid tenantId) =>
        Build(tenantSeed, tenantId, OwnerSubject, "Harness Owner");

    /// <summary>Builds the project editor for <paramref name="tenantId"/>.</summary>
    /// <param name="tenantSeed">Seed for id derivation.</param>
    /// <param name="tenantId">Tenant the user belongs to.</param>
    public static TenantUser BuildEditor(Guid tenantSeed, Guid tenantId) =>
        Build(tenantSeed, tenantId, EditorSubject, "Harness Editor");

    /// <summary>Builds the reviewer for <paramref name="tenantId"/>.</summary>
    /// <param name="tenantSeed">Seed for id derivation.</param>
    /// <param name="tenantId">Tenant the user belongs to.</param>
    public static TenantUser BuildReviewer(Guid tenantSeed, Guid tenantId) =>
        Build(tenantSeed, tenantId, ReviewerSubject, "Harness Reviewer");

    /// <summary>Builds the project viewer for <paramref name="tenantId"/>.</summary>
    /// <param name="tenantSeed">Seed for id derivation.</param>
    /// <param name="tenantId">Tenant the user belongs to.</param>
    public static TenantUser BuildViewer(Guid tenantSeed, Guid tenantId) =>
        Build(tenantSeed, tenantId, ViewerSubject, "Harness Viewer");

    /// <summary>
    /// Builds a user in <see cref="TenantUserStatus.Disabled"/>.
    /// </summary>
    /// <remarks>
    /// The <c>403 USER_DISABLED</c> path is only reachable with one of these,
    /// and the platform distinguishes it from <c>401 INVALID_CREDENTIALS</c>
    /// deliberately: a disabled account must be told so, or a locked-out user
    /// resets their password forever.
    /// </remarks>
    /// <param name="tenantSeed">Seed for id derivation.</param>
    /// <param name="tenantId">Tenant the user belongs to.</param>
    public static TenantUser BuildDisabled(Guid tenantSeed, Guid tenantId)
    {
        var user = Build(tenantSeed, tenantId, "harness-disabled", "Harness Disabled");
        user.Disable(FixtureClock.Now);
        return user;
    }

    /// <summary>
    /// Builds one synthetic user.
    /// </summary>
    /// <remarks>
    /// The display name is prefixed <c>Harness</c> and the email sits under the
    /// RFC 2606 reserved domain, so a row that escapes into a log, a screenshot
    /// or a support ticket is identifiable as synthetic at a glance. See
    /// <see cref="PiiScrubber"/> for the assertions that make this mechanical
    /// rather than a convention.
    /// </remarks>
    /// <param name="tenantSeed">Seed for id derivation.</param>
    /// <param name="tenantId">Tenant the user belongs to.</param>
    /// <param name="externalSubject">
    /// Stable, non-personal subject. This is the credential the API's
    /// <c>POST /auth/login</c> takes - there is no password anywhere in this
    /// harness.
    /// </param>
    /// <param name="displayName">Synthetic display name.</param>
    public static TenantUser Build(
        Guid tenantSeed,
        Guid tenantId,
        string externalSubject,
        string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return new TenantUser(
            FixtureIds.Derive(tenantSeed, $"user/{externalSubject}"),
            tenantId,
            externalSubject,
            SyntheticEmail(externalSubject),
            displayName,
            TenantUserStatus.Active,
            FixtureClock.Now,
            FixtureClock.Now);
    }

    /// <summary>
    /// The synthetic email for a subject.
    /// </summary>
    /// <remarks>
    /// Built by string composition rather than <c>string.Create</c> because the
    /// subject is attacker-adjacent only in the sense that a caller may pass any
    /// label: an interpolated hole here would be an email-address injection into
    /// a fixture that is supposed to be incapable of producing a real address.
    /// </remarks>
    /// <param name="externalSubject">The subject the address is derived from.</param>
    public static string SyntheticEmail(string externalSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalSubject);

        var normalised = new string(
            [.. externalSubject.Select(
                character => char.IsAsciiLetterOrDigit(character) ? character : '-')]);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{SyntheticTenants.ReservedEmailLocalPart}+{normalised}@{SyntheticTenants.ReservedEmailDomain}");
    }
}