import { describe, expect, it } from 'vitest';
import { isTenantScopedKey, queryKeys, scopedTenantKey } from '../queryKeys/index.js';

/**
 * Task 037, R2: tenant-scoped query keys prove caches never cross tenants.
 * Additive `scopedTenantKey` wrapper (existing project nesting untouched).
 */
describe('tenant-scoped query keys', () => {
  it('produces stable scoped keys for the same tenant', () => {
    const base = queryKeys.project.detail('prj_1');
    expect(scopedTenantKey('tenant_a', base)).toEqual(scopedTenantKey('tenant_a', base));
  });

  it('partitions caches across tenants', () => {
    const base = queryKeys.project.detail('prj_1');
    expect(scopedTenantKey('tenant_a', base)).not.toEqual(scopedTenantKey('tenant_b', base));
  });

  it('keeps the project nesting intact under the tenant scope', () => {
    const base = queryKeys.project.detail('prj_1');
    const scoped = scopedTenantKey('tenant_a', base);
    expect(scoped.slice(2)).toEqual(base);
    expect(isTenantScopedKey(scoped, 'tenant_a')).toBe(true);
    expect(isTenantScopedKey(scoped, 'tenant_b')).toBe(false);
  });

  it('rejects empty tenant ids fail-closed', () => {
    expect(() => scopedTenantKey('', queryKeys.projects.all)).toThrow();
    expect(() => scopedTenantKey('   ', queryKeys.projects.all)).toThrow();
  });

  it('never reports unscoped keys as tenant-scoped', () => {
    expect(isTenantScopedKey(queryKeys.project.detail('prj_1'), 'tenant_a')).toBe(false);
    expect(isTenantScopedKey(queryKeys.notifications.unreadCount(), 'tenant_a')).toBe(false);
  });
});
