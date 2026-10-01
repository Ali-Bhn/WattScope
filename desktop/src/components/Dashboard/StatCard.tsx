import type { ReactNode } from "react";

export function StatCard({
  label,
  value,
  badge,
  sub,
}: {
  label: string;
  value: ReactNode;
  badge?: { text: string; tone: "measured" | "estimated" | "neutral" | "warn" };
  sub?: ReactNode;
}) {
  const badgeClass =
    badge?.tone === "measured"
      ? "bg-accent-blue/15 text-accent-blue"
      : badge?.tone === "estimated"
        ? "bg-accent-purple/15 text-accent-purple"
        : badge?.tone === "warn"
          ? "bg-amber-500/15 text-amber-400"
          : "bg-surface-3 text-ink-secondary";

  return (
    <div className="card flex flex-col gap-1.5">
      <div className="flex items-center justify-between">
        <span className="label">{label}</span>
        {badge && <span className={`rounded px-1.5 py-0.5 text-[10px] font-semibold ${badgeClass}`}>{badge.text}</span>}
      </div>
      <div className="text-xl font-semibold tabular-nums">{value}</div>
      {sub && <div className="text-xs text-ink-secondary">{sub}</div>}
    </div>
  );
}
