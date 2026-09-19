-- Row-level security policies for tenant isolation.
-- Applied by the migration task (Task 7); defined here per Task 6.
--
-- Design notes:
--   * Every table carrying a tenant_id column gets ENABLE ROW LEVEL SECURITY plus a
--     tenant_isolation policy comparing tenant_id to current_setting('app.tenant_id', true).
--   * The policy fails closed: when app.tenant_id is unset, current_setting(..., true)
--     returns NULL and the comparison matches no rows.
--   * The tenants table itself has no tenant_id column and is excluded; it is only
--     readable by the maintenance role, never by tenant application roles.
--     (No ALTER for tenants: RLS is never enabled on it.)
--   * MassTransit outbox/inbox tables (outbox_state, outbox_message, inbox_state) carry
--     no tenant_id and are excluded; they are internal transport state.
--   * TenantSessionInterceptor sets app.tenant_id per connection open using a
--     parameterized set_config call and resets it when no tenant scope is active.
--   * Maintenance work bypasses RLS via a dedicated BYPASSRLS role, never via the
--     application role. Application query filters remain as defense in depth.
--   * Only whitelisted table names appear below; no dynamic SQL is generated from input.

ALTER TABLE dubbing_projects ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON dubbing_projects USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE processing_runs ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON processing_runs USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE media_assets ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON media_assets USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE upload_sessions ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON upload_sessions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE upload_parts ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON upload_parts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE voice_profiles ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON voice_profiles USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE speakers ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON speakers USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE speech_segments ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON speech_segments USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE context_windows ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON context_windows USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE segment_context_assignments ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON segment_context_assignments USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE overlap_groups ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON overlap_groups USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE segment_overlaps ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON segment_overlaps USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE speaker_voice_assignments ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON speaker_voice_assignments USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE consent_records ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON consent_records USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE transcript_versions ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON transcript_versions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE translation_versions ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON translation_versions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE generated_audio_artifacts ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON generated_audio_artifacts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE sync_results ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sync_results USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE stage_executions ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON stage_executions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE run_stage_summaries ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON run_stage_summaries USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE stage_unit_completions ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON stage_unit_completions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE content_objects ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON content_objects USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE artifacts ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON artifacts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE artifact_parents ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON artifact_parents USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE stage_input_artifacts ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON stage_input_artifacts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE stage_output_artifacts ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON stage_output_artifacts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE provider_executions ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON provider_executions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE provider_capability_descriptors ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON provider_capability_descriptors USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE provider_route_snapshots ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON provider_route_snapshots USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE prompt_templates ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON prompt_templates USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE prompt_template_versions ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON prompt_template_versions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE quality_results ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON quality_results USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE review_items ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON review_items USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE review_decisions ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON review_decisions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE output_assets ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON output_assets USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE export_jobs ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON export_jobs USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE export_artifacts ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON export_artifacts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON audit_events USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE idempotency_records ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON idempotency_records USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE cost_reservations ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON cost_reservations USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE quota_usages ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON quota_usages USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE processing_policies ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON processing_policies USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE retention_holds ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON retention_holds USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

ALTER TABLE deletion_jobs ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON deletion_jobs USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
