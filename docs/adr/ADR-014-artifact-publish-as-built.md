# ADR-014: Artifact publish as-built (upload-first + single-txn commit) and lineage direction

Status: Accepted (GAP-014, 2026-10-02)

Plan A §5 Actions 3 and 14 asked for (a) `ArtifactParent` **and**
`ArtifactChild` tables and (b) a reserve-first publish workflow:

```
reserve ContentObject(Pending) -> reserve Artifact(Pending) -> upload blob
-> verify checksum -> mark committed -> complete stage in the same txn
-> publish outbox in the same txn
```

## Decision

1. **`ArtifactChild` is a reverse query, not a second table.** Lineage is one
   directed edge table (`artifact_parents`, unique `(child_artifact_id,
   parent_artifact_id)`). Both directions are already served relationally:
   - parents: `ArtifactService.GetParentsAsync` → `Where(Child == id)`
   - children: `RetentionService` reverse scan → `Where(Parent == id)`
     plus the `Parent` index. A second table would double writes and add a
   divergence risk with zero new information.
2. **Publish is upload-first, then one transaction that commits.**
   `ArtifactService.PublishAsync` uploads (with streaming SHA-256 and
   authoritative storage-checksum verification) and only then opens a single
   transaction that inserts `ContentObject(Committed)`,
   `Artifact(Committed)`, `ArtifactParent` rows, and `StageOutputArtifact`
   rows. `Pending` rows are never created.
3. **Stage completion stays in its own fenced transaction.** The stage
   completion is a lease-fenced conditional UPDATE on `stage_executions`
   (`StageExecutionSql.CompleteSql`); the artifact commit is its own
   transaction, and the `StageCompleted` send is an EF-bus-outbox publish.

## Rationale (Rule 5)

- The plan's `Pending` reserve would require a stale-`Pending` sweeper, would
  make the storage-quota decision depend on unverified byte counts, and would
  expose uncommitted bytes through artifact reads. Upload-first is
  orphan-safe and already covered by tests
  (`Failed_Commit_Leaves_No_Committed_Artifact`,
  `Orphan_Reconciler_Detects_Blob`).
- Joining the stage completion into the artifact transaction would break the
  lease-fencing contract: the completion is a conditional update keyed on
  `lease_owner`/`lease_token`, and the artifact transaction must commit even
  when the lease was lost (otherwise a lost lease would also discard a valid,
  checksum-verified artifact).

## Crash windows (as-built, with redrive proof)

| Window | State after crash | Redrive / repair |
|---|---|---|
| W1 upload → commit | blob with no rows | `OrphanObjectReconciler` pass 1 quarantines blobs older than `Storage:BlobOrphanAge`; `GetDownloadUrlAsync` only serves `Committed`, so nothing is reachable |
| W2 artifact commit → stage completion | rows `Committed`, execution still `Running` | redriving the stage re-publishes: dedup hits the same `ContentObject` and creates one more logical `Artifact` (no duplicate bytes); the fenced completion overwrites `output_artifact_ids_json` with the latest set |
| W3 stage completion → `StageCompleted` publish | execution `Completed`, saga not notified | the `StageCompleted` send is an EF outbox row committed with the transaction that completed the stage, so it is retried by the outbox dispatcher (at-least-once, deduped downstream by `BarrierService` + `ProviderExecutionRecorder` idempotency keys) |
| W4 publish → dispatch | nothing pending | EF outbox guarantees no lost dispatch |

## Consequences

- `Pending` in `ArtifactStatus`/`ContentObjectStatus` remains valid for the
  state machines and is exercised by unit tests, but the publish path never
  creates it.
- The two-txn split (artifact commit, then fenced stage completion) is
  intentional and is the reason `ArtifactService` never completes a stage.
- Regression coverage: `UnitTests/ArtifactPublishAsBuiltTests` (single
  transaction, no `Pending` rows, reverse lineage query available).