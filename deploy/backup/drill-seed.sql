-- Drill seed for the Task 043C new-entity restore drill.
--
-- SYNTHETIC DATA ONLY. Every id here is a literal UUID chosen to be obviously
-- not a real one: they all begin de71, which is valid hex and spells "drill",
-- and every email is @drill.invalid, which is a reserved TLD that cannot resolve.
-- Nothing here is copied from an environment and nothing here may be replaced
-- with real values; a drill that seeds real rows is a drill that writes to
-- production data.
--
-- WHAT IT SEEDS
--   tenants, tenant_users, dubbing_projects, speakers, voice_profiles,
--   user_preferences, notifications, activity_events,
--   project_memberships, voice_preview_jobs
-- i.e. the seven Plan B durable-entity groups (deploy/backup/scope.json) plus
-- the parents they reference.
--
-- WHY PARENTS ARE SEEDED
--   The schema has no foreign keys between any of these tables - the only FKs in
--   the database are the MassTransit outbox/inbox pair. So a partial restore is
--   silent: a restored notification whose project was not restored is accepted
--   by PostgreSQL and produces an empty workspace with no error anywhere. The
--   drill therefore asserts referential coherence explicitly, and the seed has
--   to make a coherent state in the first place for that assertion to mean
--   anything.
--
-- ROW COUNTS (asserted by the drill, so a change here is a change to the drill)
--   tenants 1, dubbing_projects 3, speakers 2, voice_profiles 2, tenant_users 2,
--   user_preferences 4, notifications 5, activity_events 4,
--   project_memberships 3, voice_preview_jobs 2
--
-- Deliberately asymmetric on purpose: one tenant is deliberately left with NO
-- notifications and NO activity, so "counts are non-zero everywhere" is a check
-- that can fail rather than a check that always passes.

\set ON_ERROR_STOP on

BEGIN;

-- Idempotent. The drill is expected to be re-run - against the same disposable
-- instance, after a failure, or a week later against a new one - and a seed that
-- only works on an empty database is a seed that gets skipped after the first
-- attempt. The delete is scoped to the drill's own tenant id, so it cannot touch
-- anything that is not a drill row.
DELETE FROM voice_preview_jobs WHERE tenant_id = 'de710000-0000-4000-8000-000000000001';
DELETE FROM notifications    WHERE tenant_id = 'de710000-0000-4000-8000-000000000001';
DELETE FROM activity_events  WHERE tenant_id = 'de710000-0000-4000-8000-000000000001';
DELETE FROM project_memberships WHERE tenant_id = 'de710000-0000-4000-8000-000000000001';
DELETE FROM user_preferences WHERE tenant_id = 'de710000-0000-4000-8000-000000000001';
DELETE FROM voice_profiles   WHERE tenant_id = 'de710000-0000-4000-8000-000000000001';
DELETE FROM speakers         WHERE tenant_id = 'de710000-0000-4000-8000-000000000001';
DELETE FROM dubbing_projects WHERE tenant_id = 'de710000-0000-4000-8000-000000000001';
DELETE FROM tenant_users     WHERE tenant_id = 'de710000-0000-4000-8000-000000000001';
DELETE FROM tenants          WHERE id         = 'de710000-0000-4000-8000-000000000001';

-- Parents ---------------------------------------------------------------------
INSERT INTO tenants (id, name, slug, created_at) VALUES
  ('de710000-0000-4000-8000-000000000001', 'Drill Tenant A', 'drill-tenant-a', '2026-10-01T00:00:00Z');

INSERT INTO tenant_users (id, tenant_id, external_subject, email, display_name, status, created_at, updated_at) VALUES
  ('de710000-0000-4000-8000-000000000011', 'de710000-0000-4000-8000-000000000001',
   'sub|drill-alice', 'alice@drill.invalid', 'Drill Alice', 'Active', '2026-10-01T00:01:00Z', '2026-10-01T00:01:00Z'),
  ('de710000-0000-4000-8000-000000000012', 'de710000-0000-4000-8000-000000000001',
   'sub|drill-bob', 'bob@drill.invalid', 'Drill Bob', 'Active', '2026-10-01T00:02:00Z', '2026-10-01T00:02:00Z');

INSERT INTO dubbing_projects (
  id, tenant_id, source_language, target_language, status, settings_json, configuration_hash,
  created_at, updated_at, is_deleted, is_archived, name, description,
  owner_user_id, created_by_user_id, updated_by_user_id, processing_settings_json, settings_version) VALUES
  ('de710000-0000-4000-8000-000000000101', 'de710000-0000-4000-8000-000000000001',
   'en', 'es', 'Draft', '{"seed":"drill"}', 'drillhash0001',
   '2026-10-01T00:03:00Z', '2026-10-01T00:03:00Z', false, false,
   'Drill Project One', 'First drill project; carries the extended metadata columns.',
   'de710000-0000-4000-8000-000000000011', 'de710000-0000-4000-8000-000000000011', 'de710000-0000-4000-8000-000000000011',
   '{"concurrency":2}', 3),
  ('de710000-0000-4000-8000-000000000102', 'de710000-0000-4000-8000-000000000001',
   'en', 'fr', 'Completed', '{"seed":"drill"}', 'drillhash0002',
   '2026-10-01T00:04:00Z', '2026-10-01T00:05:00Z', false, false,
   'Drill Project Two', 'Archived project; proves is_archived and archived_at survive.',
   'de710000-0000-4000-8000-000000000011', 'de710000-0000-4000-8000-000000000011', 'de710000-0000-4000-8000-000000000012',
   '{"concurrency":1}', 1),
  ('de710000-0000-4000-8000-000000000103', 'de710000-0000-4000-8000-000000000001',
   'de', 'en', 'Draft', '{"seed":"drill"}', 'drillhash0003',
   '2026-10-01T00:06:00Z', '2026-10-01T00:06:00Z', false, true,
   'Drill Project Three', 'Soft-deleted project; proves is_deleted and is_archived are independent.',
   'de710000-0000-4000-8000-000000000012', 'de710000-0000-4000-8000-000000000012', 'de710000-0000-4000-8000-000000000012',
   NULL, 1);
UPDATE dubbing_projects SET archived_at = '2026-10-01T00:07:00Z' WHERE id = 'de710000-0000-4000-8000-000000000103';

INSERT INTO speakers (
  id, tenant_id, project_id, speaker_key, display_name, first_appearance_ms, last_appearance_ms,
  mapping_method, mapping_version, confidence, provider_label, created_at) VALUES
  ('de710000-0000-4000-8000-000000000201', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000101', 'spk-1', 'Drill Speaker One', 0, 42000,
   'clustering', 'v1', 0.94, 'drill-voice-a', '2026-10-01T00:08:00Z'),
  ('de710000-0000-4000-8000-000000000202', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000101', 'spk-2', 'Drill Speaker Two', 43000, 90000,
   'clustering', 'v1', 0.81, 'drill-voice-b', '2026-10-01T00:09:00Z');

-- Not a Plan B table, but a parent the preview jobs name by `voice_id`, so it is
-- in the dump: a restore that brings back the job and not the profile leaves a
-- preview pointing at a voice the product cannot describe.
INSERT INTO voice_profiles (
  id, tenant_id, provider, voice_id, voice_version, language, type,
  cloning_enabled, model_ref_json, created_at) VALUES
  ('de710000-0000-4000-8000-000000000211', 'de710000-0000-4000-8000-000000000001',
   'drill-provider', 'drill-voice-a', 'v1', 'es', 'Neural', true, NULL, '2026-10-01T00:09:30Z'),
  ('de710000-0000-4000-8000-000000000212', 'de710000-0000-4000-8000-000000000001',
   'drill-provider', 'drill-voice-b', 'v1', 'es', 'Neural', false, NULL, '2026-10-01T00:09:40Z');

-- Group 3/7: user_preferences -------------------------------------------------
INSERT INTO user_preferences (tenant_id, user_id, key, value_json, updated_at) VALUES
  ('de710000-0000-4000-8000-000000000001', 'de710000-0000-4000-8000-000000000011', 'ui.theme', '"dark"',          '2026-10-01T00:10:00Z'),
  ('de710000-0000-4000-8000-000000000001', 'de710000-0000-4000-8000-000000000011', 'ui.locale', '"en-GB"',        '2026-10-01T00:10:00Z'),
  ('de710000-0000-4000-8000-000000000001', 'de710000-0000-4000-8000-000000000011', 'processing.qualityProfile', '{"threshold":0.85}', '2026-10-01T00:11:00Z'),
  ('de710000-0000-4000-8000-000000000001', 'de710000-0000-4000-8000-000000000012', 'ui.theme', '"light"',         '2026-10-01T00:12:00Z');

-- Group 3/7: project_memberships ---------------------------------------------
INSERT INTO project_memberships (id, tenant_id, project_id, user_id, role, granted_by_user_id, created_at) VALUES
  ('de710000-0000-4000-8000-000000000301', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000101', 'de710000-0000-4000-8000-000000000011', 'Owner',    'de710000-0000-4000-8000-000000000011', '2026-10-01T00:13:00Z'),
  ('de710000-0000-4000-8000-000000000302', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000101', 'de710000-0000-4000-8000-000000000012', 'Editor',   'de710000-0000-4000-8000-000000000011', '2026-10-01T00:14:00Z'),
  ('de710000-0000-4000-8000-000000000303', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000102', 'de710000-0000-4000-8000-000000000012', 'Viewer',   'de710000-0000-4000-8000-000000000011', '2026-10-01T00:15:00Z');

-- Group 3/7: notifications ---------------------------------------------------
-- Two rows for Alice, two for Bob, one expired. `read_at` is deliberately mixed
-- so the unread-count path is exercised, and `source_event_id` is set on one
-- row so the dedupe index is exercised.
INSERT INTO notifications (
  id, tenant_id, recipient_user_id, project_id, type, severity, title, body,
  resource_type, resource_id, source_event_id, read_at, created_at, expires_at) VALUES
  ('de710000-0000-4000-8000-000000000401', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000011', 'de710000-0000-4000-8000-000000000101',
   'RunCompleted', 'Info', 'Run completed', 'Project One finished stage Translation.',
   'processing_run', 'de710000-0000-4000-8000-000000000901', 'de710000-0000-4000-8000-0000000000a1',
   NULL, '2026-10-01T00:16:00Z', '2026-11-01T00:16:00Z'),
  ('de710000-0000-4000-8000-000000000402', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000011', 'de710000-0000-4000-8000-000000000101',
   'ReviewRequested', 'Warning', 'Review requested', 'Segment 12 needs a human.',
   'review', 'de710000-0000-4000-8000-000000000b01', NULL,
   NULL, '2026-10-01T00:17:00Z', '2026-11-01T00:17:00Z'),
  ('de710000-0000-4000-8000-000000000403', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000012', 'de710000-0000-4000-8000-000000000102',
   'ExportCompleted', 'Info', 'Export ready', 'Project Two export is available.',
   'export', 'de710000-0000-4000-8000-000000000c01', 'de710000-0000-4000-8000-0000000000a2',
   '2026-10-01T00:20:00Z', '2026-10-01T00:18:00Z', '2026-11-01T00:18:00Z'),
  ('de710000-0000-4000-8000-000000000404', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000012', 'de710000-0000-4000-8000-000000000102',
   'QcBlocked', 'Critical', 'Quality gate blocked', 'Low ASR confidence on segment 5.',
   'quality_result', 'de710000-0000-4000-8000-000000000d01', NULL,
   NULL, '2026-10-01T00:19:00Z', '2026-11-01T00:19:00Z'),
  -- Expired: must be restored as a row, and must not be returned by the list
  -- endpoint. Its presence is how the drill proves retention is a filter and
  -- not a delete.
  ('de710000-0000-4000-8000-000000000405', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000011', NULL,
   'SystemNotice', 'Info', 'Scheduled maintenance', 'This notification is past expires_at.',
   'system', 'de710000-0000-4000-8000-000000000e01', NULL,
   '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z', '2026-09-02T00:00:00Z');

-- Group 4/7: activity_events -------------------------------------------------
INSERT INTO activity_events (
  id, tenant_id, project_id, processing_run_id, type, actor_type, actor_user_id,
  summary, severity, correlation_id, occurred_at, schema_version, metadata_json) VALUES
  ('de710000-0000-4000-8000-000000000501', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000101', 'de710000-0000-4000-8000-000000000901',
   'StageCompleted', 'System', NULL, 'Stage Transcription completed.', 'Info',
   'drill-corr-0001', '2026-10-01T00:21:00Z', 1, '{"stage":"Transcription"}'),
  ('de710000-0000-4000-8000-000000000502', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000101', 'de710000-0000-4000-8000-000000000901',
   'SettingsChanged', 'User', 'de710000-0000-4000-8000-000000000011',
   'Bob was granted the Editor role.', 'Info',
   'drill-corr-0002', '2026-10-01T00:14:00Z', 1, '{"role":"Editor"}'),
  ('de710000-0000-4000-8000-000000000503', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000102', 'de710000-0000-4000-8000-000000000902',
   'RunCompleted', 'System', NULL, 'Project Two completed.', 'Info',
   'drill-corr-0003', '2026-10-01T00:22:00Z', 1, NULL),
  ('de710000-0000-4000-8000-000000000504', 'de710000-0000-4000-8000-000000000001',
   NULL, NULL,
   'UserSignedIn', 'User', 'de710000-0000-4000-8000-000000000012',
   'Bob signed in.', 'Info',
   'drill-corr-0004', '2026-10-01T00:23:00Z', 1, NULL);

-- Group 6/7: voice_preview_jobs ---------------------------------------------
-- One terminal, one deliberately left Running: a restored Running row is a
-- claim on a worker that no longer exists, and the drill records that as a
-- forward-fix rather than pretending the restore produced a clean state.
INSERT INTO voice_preview_jobs (
  id, tenant_id, project_id, speaker_id, voice_id, text, status, requested_by_user_id,
  idempotency_key, quota_check, quota_check_reason, consent_state, provider_execution_id,
  artifact_id, error_code, error_message, created_at, started_at, completed_at) VALUES
  ('de710000-0000-4000-8000-000000000601', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000101', 'de710000-0000-4000-8000-000000000201',
   'drill-voice-a', 'Preview one.', 'Completed', 'de710000-0000-4000-8000-000000000011',
   'drill-idem-0001', 'Passed', NULL, 'Granted', 'de710000-0000-4000-8000-0000000000f1',
   'de710000-0000-4000-8000-0000000000f2', NULL, NULL,
   '2026-10-01T00:24:00Z', '2026-10-01T00:24:05Z', '2026-10-01T00:24:30Z'),
  ('de710000-0000-4000-8000-000000000602', 'de710000-0000-4000-8000-000000000001',
   'de710000-0000-4000-8000-000000000101', 'de710000-0000-4000-8000-000000000202',
   'drill-voice-b', 'Preview two.', 'Running', 'de710000-0000-4000-8000-000000000012',
   'drill-idem-0002', 'Passed', NULL, 'Granted', NULL,
   NULL, NULL, NULL,
   '2026-10-01T00:25:00Z', '2026-10-01T00:25:02Z', NULL);

COMMIT;
