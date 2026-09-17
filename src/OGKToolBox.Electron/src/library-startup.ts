/** Cache restoration is an optimization; a failed cache read must allow rebuilding. */
export async function loadInitialLibrary<T>(
  readCache: () => Promise<T | null>,
  rebuild: () => Promise<T>,
  isActive: () => boolean
): Promise<{ value: T; cached: boolean } | null> {
  let cached: T | null = null;
  try { cached = await readCache(); }
  catch { /* Retry the service below and attempt to rebuild the index. */ }
  if (!isActive()) return null;
  if (cached !== null) return { value: cached, cached: true };
  const value = await rebuild();
  return isActive() ? { value, cached: false } : null;
}
