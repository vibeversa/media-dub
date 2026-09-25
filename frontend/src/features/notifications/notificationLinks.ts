/**
 * Deep links for notification rows (Task 034).
 *
 * Maps each notification `type` + `resourceType`/`resourceId`/`projectId` to
 * a frontend target. Every mapping falls back to a live route (never a dead
 * link): unknown types go to the dashboard, linked rows fall back to the
 * owning project, and deleted targets resolve to a `gone` kind so the UI can
 * render a `GoneState` with an explanation instead of navigating.
 *
 * - Processing events (`ProcessingCompleted`/`ProcessingFailed`,
 *   `ProcessingRun` rows, `DubbingProject` rows, quota/policy warnings with
 *   a project) → project workspace (`/projects/:id`).
 * - Review events (`ManualReviewRequired`/`ReviewResolved`, `ReviewItem`
 *   rows) → review studio item (`/review?project=:id&reviewId=:rid`).
 * - Export events (`ExportCompleted`/`ExportFailed`, `ExportJob` rows) →
 *   outputs page (`/projects/:id/exports`).
 * - Media rows (`MediaAsset`) → media tab (`/projects/:id/media`).
 * - Tenant rows and unknown types → dashboard (`/dashboard`).
 */

import { isDeletedTargetId } from './types.js';

export type NotificationLinkKind = 'project' | 'review' | 'export' | 'media' | 'dashboard' | 'gone';

export interface NotificationLinkTarget {
  readonly href: string;
  readonly kind: NotificationLinkKind;
}

export interface NotificationLinkInput {
  readonly type?: string;
  readonly resourceType?: string;
  readonly resourceId?: string;
  readonly projectId?: string;
}

function encodeSegment(value: string): string {
  return encodeURIComponent(value);
}

/**
 * Resolves the deep-link target for one notification. Never returns a dead
 * link: unknown types fall back to `/dashboard`, linked rows fall back to
 * the owning project, and deleted sentinels resolve to `gone`. Pure.
 */
export function notificationLinkFor(input: NotificationLinkInput): NotificationLinkTarget {
  const type = (input.type ?? '').trim();
  const resourceType = (input.resourceType ?? '').trim();
  const resourceId = (input.resourceId ?? '').trim();
  const projectId = (input.projectId ?? '').trim();

  const resourceGone = isDeletedTargetId(resourceId) || isDeletedTargetId(projectId);
  if (resourceGone) {
    if (projectId !== '' && !isDeletedTargetId(projectId)) {
      return { href: `/projects/${encodeSegment(projectId)}`, kind: 'gone' };
    }
    return { href: '/dashboard', kind: 'gone' };
  }

  if (resourceType === 'ReviewItem' || type === 'ManualReviewRequired' || type === 'ReviewResolved') {
    if (projectId !== '' && resourceId !== '') {
      return {
        href: `/review?project=${encodeSegment(projectId)}&reviewId=${encodeSegment(resourceId)}`,
        kind: 'review',
      };
    }
    if (projectId !== '') {
      return { href: `/review?project=${encodeSegment(projectId)}`, kind: 'review' };
    }
    return { href: '/review', kind: 'review' };
  }

  if (resourceType === 'ExportJob' || type === 'ExportCompleted' || type === 'ExportFailed') {
    if (projectId !== '') {
      return { href: `/projects/${encodeSegment(projectId)}/exports`, kind: 'export' };
    }
    return { href: '/dashboard', kind: 'dashboard' };
  }

  if (resourceType === 'MediaAsset' || type === 'UploadRejected') {
    if (projectId !== '') {
      return { href: `/projects/${encodeSegment(projectId)}/media`, kind: 'media' };
    }
    return { href: '/dashboard', kind: 'dashboard' };
  }

  if (resourceType === 'ProcessingRun' || type === 'ProcessingCompleted' || type === 'ProcessingFailed') {
    if (projectId !== '') {
      return { href: `/projects/${encodeSegment(projectId)}`, kind: 'project' };
    }
    return { href: '/dashboard', kind: 'dashboard' };
  }

  if (resourceType === 'DubbingProject') {
    const target = projectId !== '' ? projectId : resourceId;
    if (target !== '') {
      return { href: `/projects/${encodeSegment(target)}`, kind: 'project' };
    }
    return { href: '/dashboard', kind: 'dashboard' };
  }

  if (resourceType === 'Tenant' || type === 'QuotaWarning' || type === 'ProviderPolicyWarning') {
    if (projectId !== '') {
      return { href: `/projects/${encodeSegment(projectId)}`, kind: 'project' };
    }
    return { href: '/dashboard', kind: 'dashboard' };
  }

  if (projectId !== '') {
    return { href: `/projects/${encodeSegment(projectId)}`, kind: 'project' };
  }
  if (resourceId !== '' && resourceType === '') {
    return { href: '/dashboard', kind: 'dashboard' };
  }
  return { href: '/dashboard', kind: 'dashboard' };
}

/** True when the target should render a `GoneState` instead of navigating. Pure. */
export function isGoneLink(target: NotificationLinkTarget): boolean {
  return target.kind === 'gone';
}
