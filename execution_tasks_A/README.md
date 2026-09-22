# Execution Tasks

## Execution Order

1. `001-project-initiation.md` — Solution, projects, build props, tooling, NuGet, compilable stubs
2. `002-docker-compose-and-build-tooling.md` — Dockerfiles, compose fast/full, healthchecks, Makefile
3. `003-domain-enums-and-value-objects.md` — Enums and value objects with loudness/timing defaults
4. `004-domain-core-entities.md` — Tenant/project/run/media/upload/speaker/segment entities + IDs
5. `005-domain-execution-artifact-entities.md` — Stage/artifact/provider/QC/review/export/audit/cost entities
6. `006-persistence-context-and-state-machines.md` — DbContext, configs, indexes, RLS, state machines, hashes
7. `007-initial-migration-and-constraints.md` — Initial migration, RLS apply, constraint verification
8. `008-api-middleware-options-and-errors.md` — Options, correlation, error envelope, validation, Polly
9. `009-observability-health-and-process-runner.md` — Serilog, OTel, metrics, health separation, ProcessRunner, profiles
10. `010-messaging-contracts.md` — 15 message contracts, version policy, queue names
11. `011-masstransit-outbox-and-stage-execution.md` — Bus, outbox/inbox, BaseConsumer, claim + lease fencing
12. `012-saga-dag-barriers-and-recovery.md` — Saga, StageGraph DAG, barriers, dispatcher, timeouts, sweeper, DLQ
13. `013-artifact-storage-and-reconciliation.md` — IArtifactStorage, S3, atomic publication, lineage, reconcilers
14. `014-provider-model-routing-and-recording.md` — Provider interfaces, descriptors, resolver, health, recording
15. `015-mock-providers.md` — Deterministic mocks for all capabilities + failure scenarios
16. `016-real-provider-adapters.md` — Azure/OpenAI/Google/Local adapters + jobs + contract tests
17. `017-api-foundation-auth-and-idempotency.md` — JWT, roles, ownership, idempotency, pagination, OpenAPI, audit
18. `018-project-and-upload-endpoints.md` — 32 routes, project/upload session logic, status codes
19. `019-upload-ingestion-and-media-validation.md` — FFprobe validation, hashing, duplicates, readiness
20. `020-processing-start-and-audio-preparation.md` — Run creation, media analysis, canonical 48kHz audio
21. `021-source-separation-and-background-policy.md` — Optional separation, normalized confidence, fallback
22. `022-vad-and-segment-builder.md` — VAD, segments, overlap relations, timeline validation
23. `023-diarization-and-speaker-identity.md` — Speaker mapping, provenance, single-speaker fallback
24. `024-transcription-worker.md` — Versioned transcription, fallback, review, barriers
25. `025-context-build-worker.md` — Reusable deterministic context windows
26. `026-translation-worker.md` — Bounded versioned translation with glossary scoring
27. `027-voice-assignment-and-consent.md` — Stable voice assignment, cloning consent, audit
28. `028-voice-generation-tts.md` — TTS with estimator, preview/final, budgets
29. `029-timing-optimization.md` — Bounded sync optimization with syncScore classification
30. `030-timeline-assembly.md` — Deterministic timeline placement + integrity checks
31. `031-audio-mixing-and-loudness.md` — FFmpeg mixing, loudness/peak/ducking verification
32. `032-quality-control.md` — Segment/project/signal QC, blocking, reports, reviews
33. `033-final-rendering.md` — Mux/encode, tolerances, OutputAsset, run completion, download
34. `034-export-jobs-and-downloads.md` — On-demand exports, completeness, secure downloads
35. `035-progress-cancellation-retry-review.md` — Progress/SSE, cancel, dependency-aware retry, review APIs
36. `036-cost-quotas-rate-limits.md` — Atomic reservations, quotas, Redis rate limits, fairness
37. `037-security-privacy-retention-deletion.md` — RLS, secrets, mTLS, hardening, audit, retention/deletion, consent
38. `038-observability-slos-dashboards-runbooks.md` — SLOs, alerts, dashboards, diagnostics, runbooks
39. `039-test-fixtures-and-test-tiers.md` — Fixtures, E2E smoke, recovery/load/soak/backup/compat/signal
40. `040-cicd-kubernetes-keda-gpu.md` — CI, K8s manifests, KEDA, GPU, migration job, deploy docs
41. `041-production-hardening-ha-dr.md` — HA/DR, RPO/RTO, chaos/load/bomb/rotation drills, gates
42. `042-optional-video-intelligence-lipsync.md` — [OPTIONAL] Flagged enrichment, isolated failures
43. `043-optional-local-inference-gpu.md` — [OPTIONAL] Local/GPU sidecar boundary, registry, warmup

## Dependency Overview

Foundation first: 001–002 scaffolding and containers. Domain 003–005 defines types with no dependencies. Persistence 006–007 builds on domain. Cross-cutting 008–009 needs persistence for health/options. Messaging 010 standalone contracts; 011 needs 006+010; 012 needs 011. Storage 013 needs 006+008. Providers 014 needs domain; 015 needs 014; 016 needs 014–015. API 017 needs 006+008; 018 needs 017+013. Ingestion 019 needs 013+014+018. Linear pipeline 020→033 each needs prior stage output plus shared infra (013 storage, 011 leases, 014 providers, 036 cost gates as interfaces). Exports 034 needs 033. Operations 035 needs saga/QC/render; 036 needs messaging/providers/API; 037 needs storage/providers/API/exports; 038 needs 008+011. Testing 039 needs all pipeline for E2E. Deploy 040 needs 038–039; hardening 041 needs 040. Optionals 042–043 last, gated disabled, depend on 033/016/040 but never block core.

## Task Coverage

| Task | Plan Area Covered |
|---|---|
| 001-project-initiation | Sec 1 scaffolding, solution boundaries |
| 002-docker-compose-and-build-tooling | Sec 1 containers, compose fast/full, Makefile |
| 003-domain-enums-and-value-objects | Sec 2 enums/value objects, loudness/timing |
| 004-domain-core-entities | Sec 2 core entities, identity mapping |
| 005-domain-execution-artifact-entities | Sec 2 execution/artifact/provider/QC/review/export/audit/cost entities |
| 006-persistence-context-and-state-machines | Sec 2 DbContext, configs, state machines, hashes, RLS |
| 007-initial-migration-and-constraints | Sec 2 migration, constraints, snake_case |
| 008-api-middleware-options-and-errors | Sec 3 config, correlation, errors, validation, Polly |
| 009-observability-health-and-process-runner | Sec 3 logging/tracing/metrics/health/ProcessRunner/profiles |
| 010-messaging-contracts | Sec 4 contracts, versioning, queues |
| 011-masstransit-outbox-and-stage-execution | Sec 4 bus, outbox/inbox, claim, leases |
| 012-saga-dag-barriers-and-recovery | Sec 4 saga, DAG, barriers, dispatcher, timeouts, sweeper, DLQ |
| 013-artifact-storage-and-reconciliation | Sec 5 storage, lineage, publication, reconcilers |
| 014-provider-model-routing-and-recording | Sec 6 capabilities, routing, health, recording |
| 015-mock-providers | Sec 6 mocks deterministic |
| 016-real-provider-adapters | Sec 6 Azure/OpenAI/Google/Local + jobs + fixtures |
| 017-api-foundation-auth-and-idempotency | Sec 7 auth, roles, idempotency, pagination, OpenAPI |
| 018-project-and-upload-endpoints | Sec 7 routes + Sec 8 session creation |
| 019-upload-ingestion-and-media-validation | Sec 8 ingestion, FFprobe, duplicates, quotas |
| 020-processing-start-and-audio-preparation | Sec 9 start, analysis, canonical audio |
| 021-source-separation-and-background-policy | Sec 10 separation policy |
| 022-vad-and-segment-builder | Sec 11 VAD, segments, overlaps |
| 023-diarization-and-speaker-identity | Sec 12 diarization |
| 024-transcription-worker | Sec 13 transcription |
| 025-context-build-worker | Sec 14 context windows |
| 026-translation-worker | Sec 15 translation |
| 027-voice-assignment-and-consent | Sec 16 voices + consent |
| 028-voice-generation-tts | Sec 17 TTS |
| 029-timing-optimization | Sec 18 timing optimization |
| 030-timeline-assembly | Sec 19 timeline |
| 031-audio-mixing-and-loudness | Sec 20 mixing + loudness |
| 032-quality-control | Sec 21 QC |
| 033-final-rendering | Sec 22 render |
| 034-export-jobs-and-downloads | Sec 23 exports |
| 035-progress-cancellation-retry-review | Sec 24 progress/cancel/retry/review |
| 036-cost-quotas-rate-limits | Sec 25 cost/quotas/rate/fairness |
| 037-security-privacy-retention-deletion | Sec 26 security/privacy/audit/retention/deletion |
| 038-observability-slos-dashboards-runbooks | Sec 27 SLOs/dashboards/runbooks |
| 039-test-fixtures-and-test-tiers | Sec 28 fixtures + all test tiers |
| 040-cicd-kubernetes-keda-gpu | Sec 29 CI/CD/K8s/KEDA/GPU |
| 041-production-hardening-ha-dr | Sec 30 HA/DR/chaos/load |
| 042-optional-video-intelligence-lipsync | Sec 31 optional enrichment |
| 043-optional-local-inference-gpu | Sec 32 local/GPU inference |

## Assumption Coverage

| Assumption | Tasks |
|---|---|
| .NET 10, ASP.NET Core, worker services | 001, 002, 008, 009 |
| PostgreSQL 16 system of record | 006, 007, 009, 011 |
| RabbitMQ 3.13 durable transport | 002, 010, 011, 012 |
| Redis 7 ephemeral only | 002, 009, 012, 036, 037 |
| MassTransit messaging | 010, 011, 012 |
| EF Core + outbox/inbox | 006, 007, 011 |
| snake_case, uuid, prefixed IDs | 003, 004, 006, 007 |
| Timeline ms, source immutable, one lang/project | 003, 004, 019 |
| Canonical 48kHz/24-bit, float temp, provider downmix | 003, 020, 031 |
| Artifacts immutable, SHA-256 tenant, no cross-tenant dedup | 005, 013, 019 |
| S3-compatible, MinIO local, managed prod | 002, 013 |
| FFmpeg only media images, ArgumentList, no shell | 002, 009, 019, 020, 031, 033 |
| At-least-once, idempotent, reconciliation | 010, 011, 012, 014 |
| Routing config-driven + privacy + compatibility | 014, 016, 037 |
| Failure vs quality distinct, mocks deterministic/default | 014, 015 |
| Azure/OpenAI/Google/Local families, Python/gRPC, GPU pools | 014, 016, 043 |
| Stage execution, constraints, fencing, conditional commit | 005, 011, 012 |
| Retry layers + budgets, manual retry, new run, cancel durable | 011, 012, 035 |
| Run authoritative, project projected, review first-class | 006, 012, 032, 035 |
| Export on-demand, partial completeness | 034 |
| Idempotency replayable, endpoint retention | 017, 018 |
| Cost estimate/usage/actuals, atomic reservations, rate dims, fairness | 036 |
| Loudness -16/-1, broadcast -23, timing ±50/±100 ±15% 1.15x, estimator | 003, 028, 029, 031 |
| Observability, security, audit append-only, retention logical/physical | 009, 037, 038 |
| Managed deps, K8s, KEDA queue+metrics, fast/full profiles, phased delivery | 002, 009, 040, 041 |

## Completeness Checklist

- [ ] All implementation-plan sections covered
- [ ] All assumptions assigned
- [ ] All functional checklist items covered
- [ ] All error-handling checklist items covered
- [ ] All test checklist items covered
- [ ] All integration checklist items covered
- [ ] All completion criteria covered
- [ ] Optional post-MVP tasks are last and feature-flagged disabled by default
