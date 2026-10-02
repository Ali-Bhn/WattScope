import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { describeError, ERROR_KEYS } from "@/lib/errors";
import { Notice } from "./Notice";

/** A loading state that is announced, with motion that respects reduced-motion preferences. */
export function Loading({ label }: { label?: string }) {
  const { t } = useTranslation();
  return (
    <div role="status" className="flex items-center gap-2 py-3 text-sm text-ink-secondary">
      <span className="spinner" aria-hidden="true" />
      {label ?? t("common.loading")}
    </div>
  );
}

/** "Nothing here yet" with a reason and, where one exists, the next step -- never a blank panel. */
export function EmptyState({ title, children, action }: { title: ReactNode; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="card flex flex-col items-start gap-2">
      <span className="text-sm font-semibold">{title}</span>
      {children && <p className="max-w-prose text-sm text-ink-secondary">{children}</p>}
      {action}
    </div>
  );
}

/**
 * A failure as a person should see it: a localized title and what to do about it, with the raw technical
 * message collapsed underneath. Never a stack trace.
 */
export function ErrorPanel({ error, onRetry, compact = false }: { error: unknown; onRetry?: () => void; compact?: boolean }) {
  const { t } = useTranslation();
  const { kind, detail } = describeError(error);
  const keys = ERROR_KEYS[kind];
  return (
    <Notice tone="error" title={t(keys.title)}>
      {!compact && <p>{t(keys.hint)}</p>}
      <div className="mt-1 flex flex-wrap items-center gap-3">
        {onRetry && (
          <button type="button" className="btn" onClick={onRetry}>
            {t("common.retry")}
          </button>
        )}
        <details className="text-ink-secondary">
          <summary className="cursor-pointer select-none">{t("errors.details")}</summary>
          <p dir="ltr" className="mt-1 break-words font-mono text-[11px]">
            {detail}
          </p>
        </details>
      </div>
    </Notice>
  );
}

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: string; actions?: ReactNode }) {
  return (
    <header className="flex flex-wrap items-start justify-between gap-3">
      <div className="min-w-0">
        <h1 className="text-lg font-semibold">{title}</h1>
        {subtitle && <p className="mt-0.5 max-w-prose text-sm text-ink-secondary">{subtitle}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  );
}
