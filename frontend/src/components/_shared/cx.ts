/** Join class names, skipping falsy values. Token-first: callers pass token-backed classes only. */
export function cx(...parts: readonly (string | false | null | undefined)[]): string {
  return parts.filter((p): p is string => typeof p === 'string' && p.length > 0).join(' ');
}
