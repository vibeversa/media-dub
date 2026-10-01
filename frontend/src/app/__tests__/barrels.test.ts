// Task 039B: barrel import coverage. Every `src/**/index.ts` re-export barrel
// (except `src/stores/index.ts`, which owns the Zustand root and has
// behavioral specs) is executed here so the gap script stops counting pure
// re-export statements as uncovered lines. Each assertion pins one real
// export per barrel, so barrel drift (a dropped re-export) fails loudly
// instead of silently shrinking the public surface.
import { describe, expect, it } from 'vitest';
import * as guards from '../guards/index.js';
import * as layouts from '../layouts/index.js';
import * as navigation from '../navigation/index.js';
import * as providers from '../providers/index.js';
import * as session from '../session/index.js';
import * as components from '../../components/index.js';
import * as activity from '../../features/activity/index.js';
import * as admin from '../../features/admin/index.js';
import * as auth from '../../features/auth/index.js';
import * as cost from '../../features/cost/index.js';
import * as enrichment from '../../features/enrichment/index.js';
import * as exportsArea from '../../features/exports/index.js';
import * as notifications from '../../features/notifications/index.js';
import * as quality from '../../features/quality/index.js';
import * as review from '../../features/review/index.js';
import * as settings from '../../features/settings/index.js';
import * as timeline from '../../features/timeline/index.js';
import * as transcript from '../../features/transcript/index.js';
import * as translation from '../../features/translation/index.js';
import * as voices from '../../features/voices/index.js';
import * as i18nBarrel from '../../i18n/index.js';
import * as lib from '../../lib/index.js';
import * as telemetryBarrel from '../../telemetry/index.js';
import '../../features/index.js';
import '../../hooks/index.js';
import '../../types/index.js';

describe('barrel surface (039B)', () => {
  it('re-exports the app seams', () => {
    expect(typeof guards.RequireAuth).toBe('function');
    expect(typeof guards.RequireAdmin).toBe('function');
    expect(typeof guards.SessionSkeleton).toBe('function');
    expect(typeof layouts.AppShell).toBe('function');
    expect(typeof layouts.AuthLayout).toBe('function');
    expect(typeof layouts.ProjectLayout).toBe('function');
    expect(typeof navigation.getTopNavItems).toBe('function');
    expect(typeof navigation.getProjectTabs).toBe('function');
    expect(typeof providers.AuthProvider).toBe('function');
    expect(typeof providers.LocaleProvider).toBe('function');
    expect(typeof providers.QueryProvider).toBe('function');
    expect(typeof providers.StoreProvider).toBe('function');
    expect(typeof providers.TelemetryProvider).toBe('function');
    expect(typeof providers.ThemeProvider).toBe('function');
    expect(typeof session.readE2eSessionSeed).toBe('function');
    expect(typeof session.hasAdminPermission).toBe('function');
    expect(typeof session.isAdminPath).toBe('function');
  });

  it('re-exports shared primitives and product components', () => {
    expect(typeof components.Card).toBe('function');
    expect(typeof components.EmptyState).toBe('function');
    expect(typeof components.ErrorState).toBe('function');
    expect(typeof components.StatusBadge).toBe('function');
    expect(typeof components.statusToVariant).toBe('function');
    expect(typeof components.ToastProvider).toBe('function');
    expect(typeof components.useToast).toBe('function');
    expect(typeof components.CorrelationId).toBe('function');
    expect(typeof components.CostDisplay).toBe('function');
    expect(typeof components.EntityId).toBe('function');
    expect(typeof components.ProviderBadge).toBe('function');
    expect(typeof components.QuotaMeter).toBe('function');
    expect(typeof components.RelativeTime).toBe('function');
  });

  it('re-exports every feature surface', () => {
    expect(typeof activity.ActivityFilters).toBe('function');
    expect(typeof activity.AuditTimeline).toBe('function');
    expect(typeof admin.AdminPage).toBe('function');
    expect(typeof admin.OpsDashboard).toBe('function');
    expect(typeof auth.useAuthStore).toBe('function');
    expect(typeof auth.useSession).toBe('function');
    expect(typeof auth.ensureRestoreStarted).toBe('function');
    expect(typeof cost.QuotaBadge).toBe('function');
    expect(typeof cost.CostSummary).toBe('function');
    expect(typeof enrichment.EnrichmentGate).toBe('function');
    expect(typeof enrichment.ProjectEnrichment).toBe('function');
    expect(typeof enrichment.VideoIntelPanel).toBe('function');
    expect(typeof enrichment.LipSyncPanel).toBe('function');
    expect(typeof admin.LocalGpuPanel).toBe('function');
    expect(typeof exportsArea.ExportCard).toBe('function');
    expect(typeof exportsArea.OutputsPage).toBe('function');
    expect(typeof notifications.NotificationCenter).toBe('function');
    expect(typeof quality.QualitySummary).toBe('function');
    expect(typeof quality.QualityWorkspace).toBe('function');
    expect(typeof review.ReviewQueue).toBe('function');
    expect(typeof review.ReviewStudio).toBe('function');
    expect(typeof settings.PreferencesForm).toBe('function');
    expect(typeof settings.SettingsPage).toBe('function');
    expect(typeof timeline.MediaPlayer).toBe('function');
    expect(typeof timeline.TimelineWorkspace).toBe('function');
    expect(typeof transcript.TranscriptEditor).toBe('function');
    expect(typeof translation.TranslationWorkspace).toBe('function');
    expect(typeof voices.SpeakerList).toBe('function');
    expect(typeof voices.VoiceSelector).toBe('function');
  });

  it('re-exports i18n, lib, and telemetry seams', () => {
    expect(i18nBarrel.FALLBACK_LOCALE).toBe('en');
    expect(typeof i18nBarrel.isRtlLocale).toBe('function');
    expect(typeof i18nBarrel.useLocale).toBe('function');
    expect(typeof lib.getEnv).toBe('function');
    expect(typeof lib.tryGetEnv).toBe('function');
    expect(typeof telemetryBarrel.sanitizeRoute).toBe('function');
    expect(typeof telemetryBarrel.emitTelemetryEvent).toBe('function');
    expect(typeof telemetryBarrel.useTelemetry).toBe('function');
    expect(typeof telemetryBarrel.trackAnalytics).toBe('function');
    expect(typeof telemetryBarrel.buildPageEvent).toBe('function');
  });
});
