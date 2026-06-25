/**
 * Formats a byte count into a human-readable string (e.g. 1536 -> "1.5 KB"), used by the overview
 * statistics tiles (#333). The unit suffix is universal so it is not localized; the surrounding label
 * is localized by the component. Returns "0 B" for null / non-finite / non-positive input.
 */
export function formatBytes(bytes: number | null | undefined, fractionDigits = 1): string {
  if (bytes == null || !isFinite(bytes) || bytes <= 0) {
    return '0 B';
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  const exponent = Math.min(
    Math.floor(Math.log(bytes) / Math.log(1024)),
    units.length - 1,
  );
  const value = bytes / Math.pow(1024, exponent);
  // Whole bytes never need a fraction; larger units show up to `fractionDigits`.
  const formatted = exponent === 0 ? String(value) : value.toFixed(fractionDigits);
  return `${formatted} ${units[exponent]}`;
}
