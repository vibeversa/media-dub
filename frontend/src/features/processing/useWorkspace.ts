import { useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';

/**
 * Workspace aggregate read model (Task 025).
 *
 * Single-call surface over `GET /projects/{id}/workspace` on
 * `queryKeys.workspace.detail(id)`. Child panels receive slices via props;
 * nothing here fans out per-panel reads. Shapes are parsed defensively from
 * the generated barrel type plus the full backend aggregate (extra sections
 * arrive as unknown JSON and are coerced with fallbacks, never thrown).
 *
 * Presentation notes:
 * - Phase projection is UI-only: a fixed ordered phase list is marked
 *   done/active/queued/failed from the aggregate `phase`/`stage`/`run`
 *   values. No pipeline math is simulated and no time predictions are shown.
 * - Progress percents are display-only approximations from the backend.
 * - Config panels show hash + summary only, never secrets or local paths.
 */

export const WORKSPACE_POLL_INTERVAL_MS = 15_000;

export const WORKSPACE_TERMINAL_STATUSES: readonly string[] = ['Completed', 'Failed', 'Cancelled'];

export interface WorkspaceProjectView {
  readonly id: string;
  readonly name: string;
  readonly status: string;
  readonly sourceLanguage: string | undefined;
  readonly targetLanguage: string | undefined;
  readonly isArchived: boolean;
  readonly configHash: string | undefined;
  readonly settingsVersion: number;
}

export interface WorkspaceMediaView {
  readonly id: string | undefined;
  readonly status: string;
  readonly container: string | undefined;
  readonly sizeBytes: number;
  readonly durationMs: number;
}

export interface WorkspaceRunView {
  readonly id: string | undefined;
  readonly status: string | undefined;
  readonly configHash: string | undefined;
  readonly attempt: number;
}

export interface WorkspaceProgressView {
  readonly percentApproximate: number;
  readonly currentStage: string | undefined;
  readonly updatedAt: string | undefined;
}

export interface WorkspaceReviewView {
  readonly pendingCount: number;
  readonly oldestWaitingAt: string | undefined;
}

export interface WorkspaceOutputView {
  readonly state: string;
  readonly completeness: number;
}

export interface WorkspaceCostView {
  readonly runCost: number;
  readonly monthToDate: number;
}

export interface WorkspaceActivityRow {
  readonly id: string;
  readonly summary: string;
  readonly occurredAt: string;
}

export interface WorkspaceView {
  readonly project: WorkspaceProjectView;
  readonly media: WorkspaceMediaView;
  readonly run: WorkspaceRunView;
  readonly phase: string;
  readonly stage: string | null;
  readonly progress: WorkspaceProgressView;
  readonly review: WorkspaceReviewView;
  readonly warnings: readonly string[];
  readonly output: WorkspaceOutputView;
  readonly cost: WorkspaceCostView;
  readonly activity: readonly WorkspaceActivityRow[];
  readonly allowedActions: readonly string[];
}

export interface PipelinePhaseDef {
  readonly id: string;
  readonly parallel: boolean;
}

/**
 * Fixed display order for the pipeline stepper. Translation and voice share
 * a parallel branch in the backend DAG (voice assignment fans out after
 * diarization while translation flows through context build; both join at
 * voice generation), so those two carry the parallel note. All other phases
 * are sequential and carry no note.
 */
export const PIPELINE_PHASES: readonly PipelinePhaseDef[] = [
  { id: 'validation', parallel: false },
  { id: 'speech', parallel: false },
  { id: 'translation', parallel: true },
  { id: 'voice', parallel: true },
  { id: 'timing', parallel: false },
  { id: 'mix', parallel: false },
  { id: 'qc', parallel: false },
  { id: 'render', parallel: false },
];

export type StageState = 'done' | 'active' | 'pending' | 'failed';

export interface StageView {
  readonly id: string;
  readonly state: StageState;
  readonly parallel: boolean;
}

export type WorkspaceActionId = 'open' | 'cancel' | 'retry' | 'export' | 'delete' | 'archive';

function toFiniteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function toNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

function toRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

function pick(record: Record<string, unknown> | undefined, ...keys: readonly string[]): unknown {
  if (record === undefined) {
    return undefined;
  }
  for (const key of keys) {
    const value = record[key];
    if (value !== undefined && value !== null) {
      return value;
    }
  }
  return undefined;
}

function parseAllowedActions(raw: unknown): string[] {
  if (Array.isArray(raw)) {
    return (raw as unknown[]).filter((entry): entry is string => typeof entry === 'string' && entry !== '');
  }
  const record = toRecord(raw);
  if (record !== undefined) {
    const inner = pick(record, 'allowedActions', 'AllowedActions');
    if (Array.isArray(inner)) {
      return (inner as unknown[]).filter((entry): entry is string => typeof entry === 'string' && entry !== '');
    }
  }
  return [];
}

function parseWarnings(raw: unknown): string[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const out: string[] = [];
  for (const entry of raw as unknown[]) {
    if (typeof entry === 'string' && entry !== '') {
      out.push(entry);
      continue;
    }
    const record = toRecord(entry);
    if (record === undefined) {
      continue;
    }
    const code = toNonEmptyString(pick(record, 'code', 'Code'));
    const message = toNonEmptyString(pick(record, 'message', 'Message'));
    if (code !== undefined && message !== undefined) {
      out.push(`${code}: ${message}`);
    } else if (code !== undefined) {
      out.push(code);
    } else if (message !== undefined) {
      out.push(message);
    }
  }
  return out;
}

function parseActivity(raw: unknown): WorkspaceActivityRow[] {
  let rows: unknown = raw;
  const record = toRecord(raw);
  if (record !== undefined) {
    const inner = pick(record, 'recent', 'Recent', 'items', 'Items');
    if (Array.isArray(inner)) {
      rows = inner;
    } else if (raw !== undefined && !Array.isArray(raw)) {
      return [];
    }
  }
  if (!Array.isArray(rows)) {
    return [];
  }
  const out: WorkspaceActivityRow[] = [];
  for (const entry of rows as unknown[]) {
    const item = toRecord(entry);
    if (item === undefined) {
      continue;
    }
    const id = toNonEmptyString(pick(item, 'id', 'Id'));
    const summary = toNonEmptyString(pick(item, 'summary', 'Summary'));
    const occurredAt = toNonEmptyString(pick(item, 'occurredAt', 'OccurredAt'));
    if (id === undefined || summary === undefined) {
      continue;
    }
    out.push({ id, summary, occurredAt: occurredAt ?? '' });
  }
  return out.slice(0, 10);
}

/**
 * Coerces the raw aggregate JSON into a view. Missing optional sections fall
 * back to neutral defaults; a missing project section rejects (the page
 * cannot render without identity). Never carries secrets, paths, or bytes.
 */
export function parseWorkspace(raw: unknown): WorkspaceView {
  const root = toRecord(raw) ?? {};
  const projectRecord = toRecord(pick(root, 'project', 'Project'));
  if (projectRecord === undefined) {
    throw new Error('Workspace aggregate is missing the project section.');
  }
  const mediaRecord = toRecord(pick(root, 'media', 'Media')) ?? {};
  const runRecord = toRecord(pick(root, 'run', 'Run')) ?? {};
  const progressRecord = toRecord(pick(root, 'progress', 'Progress')) ?? {};
  const reviewRecord = toRecord(pick(root, 'review', 'Review')) ?? {};
  const outputRecord = toRecord(pick(root, 'output', 'Output')) ?? {};
  const costRecord = toRecord(pick(root, 'cost', 'Cost')) ?? {};

  const projectId = toNonEmptyString(pick(projectRecord, 'id', 'Id')) ?? '';
  const projectName = toNonEmptyString(pick(projectRecord, 'name', 'Name')) ?? 'Untitled project';
  const projectStatus = toNonEmptyString(pick(projectRecord, 'status', 'Status')) ?? 'Created';

  const mediaStatus = toNonEmptyString(pick(mediaRecord, 'status', 'Status')) ?? 'none';
  const runStatus = toNonEmptyString(pick(runRecord, 'status', 'Status'));
  const phase = toNonEmptyString(pick(root, 'phase', 'Phase')) ?? 'created';
  const stageRaw = pick(root, 'stage', 'Stage');
  const stage = typeof stageRaw === 'string' && stageRaw !== '' ? stageRaw : null;

  const percentRaw = toFiniteNumber(pick(progressRecord, 'percentApproximate', 'PercentApproximate'));
  const percent =
    percentRaw === undefined ? 0 : Math.min(100, Math.max(0, Math.round(percentRaw)));

  const completenessRaw = toFiniteNumber(pick(outputRecord, 'completeness', 'Completeness'));
  const completeness =
    completenessRaw === undefined ? 0 : Math.min(100, Math.max(0, Math.round(completenessRaw)));

  return {
    project: {
      id: projectId,
      name: projectName,
      status: projectStatus,
      sourceLanguage: toNonEmptyString(pick(projectRecord, 'sourceLanguage', 'SourceLanguage')),
      targetLanguage: toNonEmptyString(pick(projectRecord, 'targetLanguage', 'TargetLanguage')),
      isArchived: pick(projectRecord, 'isArchived', 'IsArchived') === true,
      configHash: toNonEmptyString(
        pick(projectRecord, 'configHash', 'ConfigHash', 'configurationHash', 'ConfigurationHash'),
      ),
      settingsVersion: toFiniteNumber(pick(projectRecord, 'settingsVersion', 'SettingsVersion')) ?? 0,
    },
    media: {
      id: toNonEmptyString(pick(mediaRecord, 'id', 'Id')),
      status: mediaStatus,
      container: toNonEmptyString(pick(mediaRecord, 'container', 'Container')),
      sizeBytes: toFiniteNumber(pick(mediaRecord, 'sizeBytes', 'SizeBytes')) ?? 0,
      durationMs: toFiniteNumber(pick(mediaRecord, 'durationMs', 'DurationMs')) ?? 0,
    },
    run: {
      id: toNonEmptyString(pick(runRecord, 'id', 'Id')),
      status: runStatus,
      configHash: toNonEmptyString(pick(runRecord, 'configHash', 'ConfigHash', 'configurationHash', 'ConfigurationHash')),
      attempt: toFiniteNumber(pick(runRecord, 'attempt', 'Attempt')) ?? 0,
    },
    phase,
    stage,
    progress: {
      percentApproximate: percent,
      currentStage: toNonEmptyString(pick(progressRecord, 'currentStage', 'CurrentStage')),
      updatedAt: toNonEmptyString(pick(progressRecord, 'updatedAt', 'UpdatedAt')),
    },
    review: {
      pendingCount: toFiniteNumber(pick(reviewRecord, 'pendingCount', 'PendingCount')) ?? 0,
      oldestWaitingAt: toNonEmptyString(pick(reviewRecord, 'oldestWaitingAt', 'OldestWaitingAt')),
    },
    warnings: parseWarnings(pick(root, 'warnings', 'Warnings')),
    output: {
      state: toNonEmptyString(pick(outputRecord, 'state', 'State')) ?? 'pending',
      completeness,
    },
    cost: {
      runCost: toFiniteNumber(pick(costRecord, 'runCost', 'RunCost')) ?? 0,
      monthToDate: toFiniteNumber(pick(costRecord, 'monthToDate', 'MonthToDate')) ?? 0,
    },
    activity: parseActivity(pick(root, 'activity', 'Activity')),
    allowedActions: parseAllowedActions(pick(root, 'permissions', 'Permissions')),
  };
}

/** True for terminal run statuses (polling stops, recovery may show). */
export function isTerminalRunStatus(status: string | undefined | null): boolean {
  if (status === undefined || status === null || status === '') {
    return false;
  }
  return WORKSPACE_TERMINAL_STATUSES.some((terminal) => terminal.toLowerCase() === status.toLowerCase());
}

/** True when the workspace run reached a terminal status. */
export function isTerminalWorkspace(workspace: WorkspaceView | undefined): boolean {
  if (workspace === undefined) {
    return false;
  }
  return isTerminalRunStatus(workspace.run.status);
}

/**
 * Poll cadence for the workspace aggregate. Terminal runs stop polling
 * (Task 026 owns live updates for active runs); every other state polls on
 * a fixed display cadence. Pure for unit tests.
 */
export function getWorkspacePollInterval(workspace: WorkspaceView | undefined): number | false {
  if (workspace !== undefined && isTerminalWorkspace(workspace)) {
    return false;
  }
  return WORKSPACE_POLL_INTERVAL_MS;
}

/** True once a run exists (progress + history show; otherwise prerun shows). */
export function hasRun(workspace: WorkspaceView): boolean {
  return workspace.run.id !== undefined && workspace.run.id !== '';
}

/** True when the run hash drifted from the project hash (stale banner). */
export function isVersionSkewed(workspace: WorkspaceView): boolean {
  const runHash = workspace.run.configHash;
  const projectHash = workspace.project.configHash;
  if (runHash === undefined || runHash === '' || projectHash === undefined || projectHash === '') {
    return false;
  }
  return runHash !== projectHash;
}

/** True when the latest run stopped without completing. */
export function isFailedWorkspace(workspace: WorkspaceView): boolean {
  const status = (workspace.run.status ?? '').toLowerCase();
  if (status === 'failed' || status === 'cancelled') {
    return true;
  }
  return workspace.phase.toLowerCase() === 'failed';
}

/** True while progress UI applies (a run exists). */
export function shouldShowProgress(workspace: WorkspaceView): boolean {
  return hasRun(workspace);
}

/** True while the recovery panel applies (failed run). */
export function shouldShowRecovery(workspace: WorkspaceView): boolean {
  return hasRun(workspace) && isFailedWorkspace(workspace);
}

/**
 * Maps a backend stage name to its display phase. Mirrors the server phase
 * mapping (fail-open to speech for display only; math is unaffected).
 */
export function mapStageToPhaseId(stage: string): string {
  const name = stage.trim();
  if (name === 'MediaValidation' || name === 'MediaAnalysis') {
    return 'validation';
  }
  if (
    name === 'AudioPreparation' ||
    name === 'SourceSeparation' ||
    name === 'Vad' ||
    name === 'SegmentBuild' ||
    name === 'Diarization' ||
    name === 'Transcription'
  ) {
    return 'speech';
  }
  if (name === 'ContextBuild' || name === 'Translation') {
    return 'translation';
  }
  if (name === 'VoiceAssignment' || name === 'VoiceGeneration') {
    return 'voice';
  }
  if (name === 'TimingOptimization' || name === 'TimelineAssembly') {
    return 'timing';
  }
  if (name === 'AudioMixing') {
    return 'mix';
  }
  if (name === 'QualityControl') {
    return 'qc';
  }
  if (name === 'Render') {
    return 'render';
  }
  return 'speech';
}

function phaseIndex(phase: string): number {
  const normalized = phase.trim().toLowerCase();
  for (let index = 0; index < PIPELINE_PHASES.length; index += 1) {
    if ((PIPELINE_PHASES[index]?.id ?? '') === normalized) {
      return index;
    }
  }
  return -1;
}

/**
 * Projects the aggregate phase/stage/run into per-stage states for the
 * stepper. UI-only: earlier phases read done, the current phase reads
 * active, later phases read queued. Terminal completion marks all done;
 * a failed run marks its frontier failed. Translation + voice share one
 * parallel branch, so both read active while either is current.
 */
export function projectPhaseStates(phase: string, stage: string | null, runStatus?: string): StageView[] {
  const normalizedPhase = phase.trim().toLowerCase();
  const normalizedStatus = (runStatus ?? '').trim().toLowerCase();

  if (normalizedStatus === 'completed' || normalizedPhase === 'completed') {
    return PIPELINE_PHASES.map((def) => ({ id: def.id, state: 'done' as StageState, parallel: def.parallel }));
  }

  const failed = normalizedStatus === 'failed' || normalizedStatus === 'cancelled' || normalizedPhase === 'failed';
  if (failed) {
    let failedId = 'render';
    if (typeof stage === 'string' && stage !== '') {
      failedId = mapStageToPhaseId(stage);
    } else if (phaseIndex(normalizedPhase) >= 0) {
      failedId = normalizedPhase;
    }
    const failedAt = phaseIndex(failedId);
    return PIPELINE_PHASES.map((def, index) => {
      if (index < failedAt) {
        return { id: def.id, state: 'done' as StageState, parallel: def.parallel };
      }
      if (index === failedAt) {
        return { id: def.id, state: 'failed' as StageState, parallel: def.parallel };
      }
      return { id: def.id, state: 'pending' as StageState, parallel: def.parallel };
    });
  }

  if (
    normalizedPhase === '' ||
    normalizedPhase === 'created' ||
    normalizedPhase === 'upload' ||
    normalizedPhase === 'none'
  ) {
    return PIPELINE_PHASES.map((def) => ({ id: def.id, state: 'pending' as StageState, parallel: def.parallel }));
  }

  let current = phaseIndex(normalizedPhase);
  if (current < 0 && typeof stage === 'string' && stage !== '') {
    current = phaseIndex(mapStageToPhaseId(stage));
  }
  if (current < 0) {
    return PIPELINE_PHASES.map((def) => ({ id: def.id, state: 'pending' as StageState, parallel: def.parallel }));
  }

  const activeIds = new Set<string>([PIPELINE_PHASES[current]?.id ?? '']);
  const currentId = PIPELINE_PHASES[current]?.id ?? '';
  if (currentId === 'translation' || currentId === 'voice') {
    activeIds.add('translation');
    activeIds.add('voice');
  }

  return PIPELINE_PHASES.map((def, index) => {
    if (activeIds.has(def.id)) {
      return { id: def.id, state: 'active' as StageState, parallel: def.parallel };
    }
    if (index < current) {
      return { id: def.id, state: 'done' as StageState, parallel: def.parallel };
    }
    return { id: def.id, state: 'pending' as StageState, parallel: def.parallel };
  });
}

/**
 * Narrows aggregate allowed actions to the six workspace header actions.
 * Order is stable (open/cancel/retry/export/delete/archive). Cancel needs an
 * active run, retry needs a failed run, export needs ready output; open,
 * delete, and archive are permission-only.
 */
export function getWorkspaceActions(
  allowedActions: readonly string[],
  workspace: WorkspaceView | undefined,
): WorkspaceActionId[] {
  const allowed = new Set(allowedActions);
  const actions: WorkspaceActionId[] = [];
  if (allowed.has('project.view')) {
    actions.push('open');
  }
  if (workspace !== undefined && allowed.has('processing.cancel') && hasRun(workspace) && !isTerminalWorkspace(workspace)) {
    actions.push('cancel');
  }
  if (workspace !== undefined && allowed.has('processing.retry') && shouldShowRecovery(workspace)) {
    actions.push('retry');
  }
  if (
    workspace !== undefined &&
    (allowed.has('export.create') || allowed.has('export.download')) &&
    workspace.output.state.toLowerCase() === 'ready'
  ) {
    actions.push('export');
  }
  if (allowed.has('project.delete')) {
    actions.push('delete');
  }
  if (allowed.has('project.edit')) {
    actions.push('archive');
  }
  return actions;
}

/** Single aggregate fetch. Never carries secrets, bytes, or text payloads. */
export async function fetchWorkspace(projectId: string): Promise<WorkspaceView> {
  const raw = await apiClient.getWorkspace({ path: { projectId } });
  return parseWorkspace(raw as unknown);
}

/**
 * Workspace aggregate query on `queryKeys.workspace.detail(id)` (Task 017
 * factory; inline literals are banned). Gated on the Task 019 session and a
 * non-empty id. Terminal aggregates stop polling via `refetchInterval`.
 */
export function useWorkspace(projectId: string): UseQueryResult<WorkspaceView, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<WorkspaceView, AppError>({
    queryKey: queryKeys.workspace.detail(projectId),
    queryFn: async (): Promise<WorkspaceView> => {
      try {
        return await fetchWorkspace(projectId);
      } catch (error) {
        throw normalizeError(error, { method: 'GET' });
      }
    },
    enabled,
    refetchInterval: (query) => getWorkspacePollInterval(query.state.data),
  });
}
