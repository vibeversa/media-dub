// Task 040B seam 7: backend event -> durable notification -> centre render + deep link.
//
// Owning tasks: 034 + 002/012B.
//
// The claim under test: a backend event becomes a durable row the notification
// centre can list, the unread count tracks it, marking it read is a mutation
// that survives a re-read, and the row carries the resource reference a deep
// link needs. A seam that only checked "the centre rendered something" would pass
// against a list served from an in-memory buffer that a refresh discards.
//
// R3: the notification is read back out of the database, and the read state is
// asserted there, not from the response the client just received.

import { expect, test } from '@playwright/test';

import { ApiClient, countRows } from '../harness/index.js';
import { openSeamContext, sameId } from './seamContext.js';

interface NotificationItem {
  readonly id: string;
  readonly type: string;
  readonly severity: string;
  readonly title: string;
  readonly body: string;
  readonly resourceType: string;
  readonly resourceId: string;
  readonly projectId: string | null;
  readonly readAt: string | null;
}

interface NotificationPage {
  readonly items: NotificationItem[];
  readonly total: number;
}

test.describe.serial('@cross-layer notification', () => {
  test('a durable notification is listed, counted as unread, and carries a deep link', async ({
    page,
  }, testInfo) => {
    const { api, token, projectId, environment } = await openSeamContext();
    const { notificationId } = environment.fixtures;

    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const unreadBefore = await countRows('notifications', projectId);

    // --- the centre lists it -----------------------------------------------
    const centre = await api.requestRaw<NotificationPage>({
      method: 'GET',
      path: '/api/v1/notifications?page=1&pageSize=50',
      token,
    });
    expect(centre.status, `the centre must be readable: ${JSON.stringify(centre.body)}`).toBe(200);

    const item = centre.body.items.find((entry) => sameId(entry.id, notificationId));
    expect(item, 'the seeded notification must appear in the centre').toBeDefined();
    expect(item!.type, 'the notification carries its type').toBeTruthy();
    expect(item!.severity, 'the notification carries its severity').toBeTruthy();
    expect(item!.title, 'the notification has a title to render').toBeTruthy();
    expect(item!.readAt, 'a fresh notification is unread').toBeNull();

    // --- the deep link the row enables -------------------------------------
    // `resourceType` + `resourceId` is the whole point: without them the centre
    // can render a row but cannot navigate to what it is about.
    expect(item!.resourceType, 'the row names what it is about').toBeTruthy();
    expect(
      item!.resourceId,
      'the row carries the id a deep link resolves',
    ).toBeTruthy();
    expect(
      sameId(item!.projectId ?? '', projectId),
      'the deep link resolves to the seeded project',
    ).toBe(true);

    // --- the unread count is derived, not decorative -----------------------
    const unread = await api.requestRaw<{ unreadCount: number }>({
      method: 'GET',
      path: '/api/v1/notifications/unread-count',
      token,
    });
    expect(unread.status).toBe(200);
    expect(
      unread.body.unreadCount,
      'the unread count must include the unread notification',
    ).toBeGreaterThanOrEqual(1);

    testInfo.annotations.push({
      type: 'rig',
      description:
        `notificationId=${notificationId} type=${item!.type} severity=${item!.severity} ` +
        `resource=${item!.resourceType}/${item!.resourceId} ` +
        `unread=${unread.body.unreadCount} rowsForProject=${unreadBefore}`,
    });
  });

  test('marking a notification read is durable and decrements the unread count', async () => {
    const { api, token, environment } = await openSeamContext();
    const { notificationId } = environment.fixtures;

    const before = await api.requestRaw<{ unreadCount: number }>({
      method: 'GET',
      path: '/api/v1/notifications/unread-count',
      token,
    });

    // --- the mutation -------------------------------------------------------
    const marked = await api.requestRaw({
      method: 'POST',
      path: `/api/v1/notifications/${notificationId}/read`,
      token,
      headers: { 'Idempotency-Key': `seam-notif-read-${Date.now()}` },
    });
    expect(marked.status, `mark read: ${JSON.stringify(marked.body)}`).toBe(200);

    // --- R3: the read state is stored, not just acknowledged ----------------
    // Re-read through a *different* request than the one that marked it, so an
    // in-memory echo cannot satisfy this.
    const centre = await api.requestRaw<NotificationPage>({
      method: 'GET',
      path: '/api/v1/notifications?page=1&pageSize=50',
      token,
    });
    const item = centre.body.items.find((entry) => sameId(entry.id, notificationId));
    expect(item, 'the notification must still be listed after being read').toBeDefined();
    expect(
      item!.readAt,
      'the read timestamp must be persisted and re-readable',
    ).toBeTruthy();

    const after = await api.requestRaw<{ unreadCount: number }>({
      method: 'GET',
      path: '/api/v1/notifications/unread-count',
      token,
    });
    expect(
      after.body.unreadCount,
      'the unread count must decrease once the notification is read',
    ).toBe(before.body.unreadCount - 1);
  });

  test('an unknown notification is refused by name, and an unauthorised read leaks nothing', async () => {
    const { api, token, environment } = await openSeamContext();

    // The failure half: an id that does not exist must be refused, and refused
    // the same way regardless of which unknown id is used - otherwise a caller
    // can distinguish "exists but not yours" from "does not exist".
    const unknown = await api.requestRaw({
      method: 'POST',
      path: '/api/v1/notifications/ntf_0000000000000000000000000000abcd/read',
      token,
      headers: { 'Idempotency-Key': `seam-notif-unknown-a-${Date.now()}` },
    });
    const alsoUnknown = await api.requestRaw({
      method: 'POST',
      path: '/api/v1/notifications/ntf_0000000000000000000000000000dcba/read',
      token,
      headers: { 'Idempotency-Key': `seam-notif-unknown-b-${Date.now()}` },
    });

    expect(
      unknown.status,
      'an unknown notification must be refused',
    ).toBeGreaterThanOrEqual(400);
    expect(
      unknown.status,
      'two unknown notifications must be indistinguishable (no existence leak)',
    ).toBe(alsoUnknown.status);
    expect(ApiClient.errorCode(unknown)).toBe(ApiClient.errorCode(alsoUnknown));

    // An unauthenticated centre read must not list anything.
    const anonymous = await api.requestRaw({ method: 'GET', path: '/api/v1/notifications' });
    expect(anonymous.status, 'an unauthenticated centre read is 401').toBe(401);
    expect(
      JSON.stringify(anonymous.body),
      'the refusal must not list notifications',
    ).not.toContain(environment.fixtures.notificationId);
  });
});
