# ADR-010: Prefixed public IDs stay API-only; no EF value converters on ID columns

Status: Accepted (GAP-010, 2026-10-02)

## Context

Plan A §2 Action 5 asks for prefixed public IDs (`prj_…`, `run_…`, …)
via EF value converters plus API serialization.

Implemented:

- API boundary mapping: `Domain/Identity/PublicIdMapper.cs` (22 prefixes),
  `Api/Models/PublicIdParser.cs` (accepts raw/compact GUID + prefixed,
  validates expected prefix).
- Persistence: native PG `uuid` PKs/FKs with `gen_random_uuid()`
  (`Configurations/*Configuration.cs`, `AppDbContextModelSnapshot.cs`
  `.HasColumnType("uuid")`).
- A `PublicIdConverter : ValueConverter<Guid,string>` helper exists at
  `Infrastructure/Persistence/Converters/PublicIdConverter.cs` for
  API-facing string columns and query-translation tests. Its XML doc states
  it must not be applied to primary UUID columns. It is referenced nowhere
  outside its definition.

## Decision

Reject applying `PublicIdConverter` to entity `Id`/FK columns. Public IDs
remain an API-serialization concern (`ToPublic`/`FromPublic` at the
controller/service boundary); the database keeps `uuid`.

## Rationale (Rule 5)

Applying prefixed-text converters to PKs/FKs would:

1. Break `gen_random_uuid()` defaults (text columns cannot use it).
2. Invalidate existing `uuid` indexes, FK joins, and RLS policies over
   50+ tables.
3. Require a non-expand-only `uuid → text` PK/FK rewrite across all
   tables, incompatible with the previous app version (violates the
   expand-only migration rule).
4. Add per-row conversion cost on every join with zero new information
   (prefix is derivable from the table).

The `job_` prefix has no backing entity (`PrefixFor("Job")` is reachable
only via a test shim); wiring it to storage would enshrine a dead mapping.

## Consequences

- `artifact_parents`, lineage queries, and all ID lookups keep `Guid`
  parameters; controllers map `prj_…` → `Guid` before EF LINQ.
- `PublicIdConverter` stays available for compat/test use only.
- Regression pinned by
  `tests/DubbingPlatform.UnitTests/Persistence/PublicIdStorageTests.cs`
  (`Id_Properties_Remain_Uuid_Without_PublicId_Converter`).
- Round-trip coverage stays in
  `Domain/Identity/PublicIdMapperTests.cs` + `Models/PublicIdParserTests.cs`.
