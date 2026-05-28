import { DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl } from '@angular/forms';
import {
  Observable,
  catchError,
  debounceTime,
  distinctUntilChanged,
  filter,
  finalize,
  map,
  of,
  switchMap,
  timeout,
} from 'rxjs';

// 同步 LLM 调用的失败降级：超过此时长未返回即视为失败，清掉 spinner 并回退本地占位，
// 不让建议请求悬挂阻塞用户体验（用户始终可手填机器键）。
const SUGGEST_TIMEOUT_MS = 8000;

/**
 * 显示名 → 机器键自动建议（issue #190）。FieldDefinition 与 DocumentType 创建表单共用同一套接线逻辑。
 *
 * 行为：
 * - admin 在 displayName 停顿 → 调后端 LLM 英译端点预填 target（name / typeCode）。
 * - **仅调用方允许时生效**：target enabled 且未标记为手动编辑时，自动建议才会接管。
 * - **用户手动改过 target 就不再覆盖**：监听 target.valueChanges；程序化写入一律用
 *   `{ emitEvent: false }`，因此只有真实键入会被识别为"手动"。
 * - **过期键防护**：displayName 一变就**立即清空** target（防抖前），让 target 在新建议落定前为空——
 *   配合 required 校验自动禁用 Save，杜绝把基于「上一个显示名」的 slug 当成不可变键保存；
 *   建议返回时再比对当时捕获的 displayName 与当前值，不一致则丢弃（switchMap 已取消过期 in-flight，这是二道防线）。
 * - LLM 返回空 / 不可用 → 回退到本地占位（如 `field_3`），保证 target 落定后有合法默认值。
 */
export interface SlugSuggestionHandle {
  /**
   * 重新启用自动建议并复位状态（清手动编辑标记 + 关掉 pending 提示）。
   * 在创建表单的 opener 里、`form.reset()` 与 `enable()` **之后**调用。
   */
  reset(): void;
  /** 标记 target 当前值由用户/调用方保留，后续 displayName 变化不再自动覆盖。 */
  markManual(): void;
}

export interface SlugSuggestionConfig {
  /** 源：人类可读显示名。 */
  displayName: FormControl<string>;
  /** 目标：机器键（FieldDefinition.name / DocumentType.typeCode）。 */
  target: FormControl<string>;
  /** 调后端 LLM 端点；emit 已 sanitize 的 slug（失败 / 空时 emit ''）。 */
  suggest: (displayName: string) => Observable<string>;
  /** 本地、零依赖的回退（如 `field_3`），LLM 无结果时用。 */
  fallback: (displayName: string) => string;
  destroyRef: DestroyRef;
  /** 选填：建议请求进行中时切换（用于 spinner / 提示文案）。 */
  onPending?: (pending: boolean) => void;
}

export function wireSlugSuggestion(config: SlugSuggestionConfig): SlugSuggestionHandle {
  let manuallyEdited = false;

  // 自动接管 target 的条件：调用方启用 target 且尚未把它标记为手动值。
  // 编辑态若需要保留既有机器键，调用方应先 disable 再 reset，随后 enable 并 markManual()。
  const autoManaged = (): boolean => config.target.enabled && !manuallyEdited;

  // target 上的真实键入 → 标记为手动，停止自动覆盖。
  // 下方程序化写入都用 { emitEvent: false }，不会触达这里。
  config.target.valueChanges.pipe(takeUntilDestroyed(config.destroyRef)).subscribe(() => {
    manuallyEdited = true;
  });

  // 过期键防护（防抖前、立即执行）：displayName 一变就清空 target，使其在新建议落定前保持为空。
  // target 是 required，空值 → form.invalid → Save 被禁用，杜绝保存基于旧显示名的过期机器键。
  config.displayName.valueChanges.pipe(takeUntilDestroyed(config.destroyRef)).subscribe(() => {
    if (autoManaged() && config.target.value !== '') {
      config.target.setValue('', { emitEvent: false });
    }
  });

  config.displayName.valueChanges
    .pipe(
      debounceTime(400),
      map(v => (v ?? '').trim()),
      distinctUntilChanged(),
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
      // 二道防线：结果回来时若已进入编辑态 / 已手动改 / displayName 又变了（与捕获时不一致），丢弃，
      // 避免写入基于过期 displayName 的 slug。
      if (!autoManaged() || text !== (config.displayName.value ?? '').trim()) {
        return;
      }
      config.target.setValue(slug || config.fallback(text), { emitEvent: false });
    });

  return {
    reset: () => {
      manuallyEdited = false;
      // 复位 pending：重开创建表单时关掉可能残留的"正在生成"提示，调用方无需再手动清。
      config.onPending?.(false);
    },
    markManual: () => {
      manuallyEdited = true;
      config.onPending?.(false);
    },
  };
}
