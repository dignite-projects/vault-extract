import { DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl } from '@angular/forms';
import {
  Observable,
  Subject,
  catchError,
  filter,
  finalize,
  map,
  of,
  switchMap,
  timeout,
} from 'rxjs';

// Failure fallback for synchronous LLM calls: if no result arrives within this time, treat it as
// failed, clear the spinner, and fall back to a local placeholder. Suggestion requests must not hang
// the user experience; users can always enter the machine key manually.
const SUGGEST_TIMEOUT_MS = 8000;

/**
 * Automatic display-name-to-machine-key suggestion (issue #190). FieldDefinition and DocumentType
 * creation forms share the same wiring logic.
 *
 * Behavior:
 * - When the admin blurs displayName, call the backend LLM translation endpoint to prefill target
 *   (name / typeCode).
 *   Earlier versions debounced after displayName paused for 400ms. Measured feedback changed this to
 *   blur-triggered behavior: fewer LLM calls, and no translation of a half-entered display name.
 * - Applies only when the caller allows it: automatic suggestion takes over only while target is
 *   enabled and not marked as manually edited.
 * - Once the user manually edits target, do not overwrite it: target.valueChanges is observed, and all
 *   programmatic writes use `{ emitEvent: false }`, so only real typing is treated as manual.
 * - Stale-key protection: as soon as displayName changes, immediately clear target, independent of
 *   trigger timing, keeping it empty until a new suggestion settles. Together with required validation,
 *   this disables Save and prevents persisting a slug based on the previous display name as an immutable
 *   key. When a suggestion returns, compare the captured displayName with the current value and discard
 *   mismatches. switchMap already cancels stale in-flight requests; this is the second line of defense.
 * - If the LLM returns empty or is unavailable, fall back to a local placeholder such as `field_3`, so
 *   target has a valid default after settling.
 */
export interface SlugSuggestionHandle {
  /**
   * Re-enables automatic suggestions and resets state by clearing the manual-edit marker and pending
   * hint. Call this in the creation form opener after `form.reset()` and `enable()`.
   */
  reset(): void;
  /** Marks the current target value as retained by the user or caller, so later displayName changes no longer overwrite it automatically. */
  markManual(): void;
  /**
   * Called when the display-name input blurs. If automatic management is active (target enabled and
   * not manual), this starts one LLM slug translation.
   * The caller wires this to the displayName control's `(blur)` event.
   */
  notifyDisplayNameBlur(): void;
}

export interface SlugSuggestionConfig {
  /** Source: human-readable display name. */
  displayName: FormControl<string>;
  /** Target: machine key (FieldDefinition.name / DocumentType.typeCode). */
  target: FormControl<string>;
  /** Calls the backend LLM endpoint and emits the sanitized slug, or '' on failure or empty output. */
  suggest: (displayName: string) => Observable<string>;
  /** Local dependency-free fallback, such as `field_3`, used when the LLM has no result. */
  fallback: (displayName: string) => string;
  destroyRef: DestroyRef;
  /** Optional: toggled while suggestion is pending, for spinner or hint text. */
  onPending?: (pending: boolean) => void;
}

export function wireSlugSuggestion(config: SlugSuggestionConfig): SlugSuggestionHandle {
  let manuallyEdited = false;

  // Automatic target management applies when the caller enables target and has not marked it manual.
  // In edit mode, if an existing machine key must be retained, the caller should disable before reset,
  // then enable and markManual().
  const autoManaged = (): boolean => config.target.enabled && !manuallyEdited;

  // Display-name blur event stream that triggers LLM translation. Blur is naturally low-frequency, so no
  // debounce is needed.
  const blur$ = new Subject<void>();

  // Real typing on target marks it manual and stops automatic overwrites.
  // Programmatic writes below all use { emitEvent: false }, so they do not reach this subscription.
  config.target.valueChanges.pipe(takeUntilDestroyed(config.destroyRef)).subscribe(() => {
    manuallyEdited = true;
  });

  // Stale-key protection, executed on input and decoupled from trigger timing: clear target as soon as
  // displayName changes, keeping it empty until a new suggestion settles.
  // target is required, so empty value means form.invalid and Save is disabled, preventing a stale
  // machine key based on the old display name from being saved.
  config.displayName.valueChanges.pipe(takeUntilDestroyed(config.destroyRef)).subscribe(() => {
    if (autoManaged() && config.target.value !== '') {
      config.target.setValue('', { emitEvent: false });
    }
  });

  // Trigger changed to blur based on measured feedback: when leaving the displayName input, use its
  // current value and start one LLM translation only if automatic management is active.
  // Deliberately avoid distinctUntilChanged: displayName can change away and then back to the same value
  // while target has already been cleared by stale-key protection. Adjacent de-duplication would block
  // the request that refills target and leave it empty. Blur is low-frequency, so an occasional duplicate
  // request is acceptable.
  blur$
    .pipe(
      map(() => (config.displayName.value ?? '').trim()),
      filter(text => text.length > 0 && autoManaged()),
      switchMap(text => {
        config.onPending?.(true);
        return config.suggest(text).pipe(
          timeout({ first: SUGGEST_TIMEOUT_MS }),
          catchError(() => of('')),
          map(slug => ({ text, slug })),
          finalize(() => config.onPending?.(false)),
        );
      }),
      takeUntilDestroyed(config.destroyRef),
    )
    .subscribe(({ text, slug }) => {
      // Second line of defense: when the result returns, discard it if edit mode/manual edit has taken
      // over or displayName changed from the captured value, avoiding writes based on stale displayName.
      if (!autoManaged() || text !== (config.displayName.value ?? '').trim()) {
        return;
      }
      config.target.setValue(slug || config.fallback(text), { emitEvent: false });
    });

  return {
    reset: () => {
      manuallyEdited = false;
      // Reset pending: when reopening a creation form, clear any leftover "generating" hint so callers
      // do not have to clean it manually.
      config.onPending?.(false);
    },
    markManual: () => {
      manuallyEdited = true;
      config.onPending?.(false);
    },
    notifyDisplayNameBlur: () => blur$.next(),
  };
}
