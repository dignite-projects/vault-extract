export const EXTRACT_FEATURES = {
  // Whether the tenant may use Vault Extract's application services (VaultExtractFeatures.Enable on the
  // server). The server is the authority: every application service refuses with the feature off, and this
  // value is only read to take the Documents menu out of sight. It is visible to clients, so it arrives in
  // application-configuration under `features.values`.
  Enable: 'VaultExtract.Enable',
} as const;

/**
 * Whether an `application-configuration` feature value says the feature is switched off.
 *
 * Only an explicit `false` counts. A missing value reads as enabled, matching the server's default of `true`:
 * a host on an older server that does not define the feature yet, or one whose configuration carries no
 * `features` at all, must not lose its menu. Nothing is lost by being lenient here, because the server refuses
 * the calls itself.
 */
export function isExtractFeatureDisabled(value: string | undefined): boolean {
  return value?.trim().toLowerCase() === 'false';
}
