import { describe, expect, it } from 'vitest';
import { buildLoginPath, getNextPath, isSafeNext } from '../destination.js';

describe('destination preservation (R2)', () => {
  it('accepts safe same-origin destinations', () => {
    expect(isSafeNext('/dashboard')).toBe(true);
    expect(isSafeNext('/projects/prj_1?tab=media')).toBe(true);
  });

  it('rejects open-redirect shapes', () => {
    expect(isSafeNext('')).toBe(false);
    expect(isSafeNext('https://evil.example/phish')).toBe(false);
    expect(isSafeNext('//evil.example/phish')).toBe(false);
    expect(isSafeNext('/login')).toBe(false);
    expect(isSafeNext('/login?next=%2Fdashboard')).toBe(false);
    expect(isSafeNext('dashboard')).toBe(false);
  });

  it('resolves ?next= and falls back to /dashboard', () => {
    expect(getNextPath('?next=%2Fprojects%2Fprj_1')).toBe('/projects/prj_1');
    expect(getNextPath('')).toBe('/dashboard');
    expect(getNextPath('?next=https%3A%2F%2Fevil.example')).toBe('/dashboard');
    expect(getNextPath('?next=%2F%2Fevil.example')).toBe('/dashboard');
    expect(getNextPath('?next=%2Flogin')).toBe('/dashboard');
    expect(getNextPath('?next=%ZZ')).toBe('/dashboard');
  });

  it('builds encoded login redirects', () => {
    expect(buildLoginPath('/projects/prj_1')).toBe('/login?next=%2Fprojects%2Fprj_1');
    expect(buildLoginPath('https://evil.example')).toBe('/login?next=%2Fdashboard');
  });
});
