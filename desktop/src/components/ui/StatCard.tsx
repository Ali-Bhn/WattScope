import type { ReactNode } from "react";
import { Badge, type BadgeKind } from "./Badge";

export function StatCard({
  label,
  value,
  badge,
  sub,
  hint,
}: {
  label: string;
  value: ReactNode;
  badge?: BadgeKind;
  sub?: ReactNode;
  /** Optional technical explanation shown through a Hint beside the label. */
  hint?: ReactNode;
}) {
  return (
    <div className="card flex min-w-0 flex-col gap-1.5">
      {/* The label may wrap onto two lines; the badge wraps below it when the card is narrow -- nothing is clipped. */}
      <div className="flex flex-wrap items-start justify-between gap-x-2 gap-y-1">
        <span className="label flex min-w-0 items-center gap-1">
          {label}
          {hint}
        </span>
        {badge && (
          <span className="shrink-0">
            <Badge kind={badge} />
          </span>
        )}
      </div>
      <div className="min-w-0 break-words text-xl font-semibold tabular-nums">{value}</div>
      {sub && <div className="text-xs text-ink-secondary">{sub}</div>}
    </div>
  );
}
