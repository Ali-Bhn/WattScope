import { useTranslation } from "react-i18next";
import type { EstimateCompleteness } from "@/lib/types";

/**
 * The four honesty states of every figure, plus Complete and Assumed. Each is a glyph AND a word (never
 * colour alone), with one fixed meaning everywhere in the app:
 *
 *  MEASURED    read from a hardware sensor (CPU package, GPU board)
 *  ESTIMATED   derived from measurements and assumptions (system/wall power, energy, cost)
 *  PARTIAL     an estimate with an input missing: a lower bound
 *  UNAVAILABLE no value exists right now (never shown as zero)
 *  COMPLETE    every required input was measured
 *  ASSUMED     a number the user set, not a measurement
 */
export type BadgeKind = "measured" | "estimated" | "partial" | "unavailable" | "complete" | "assumed";

const STYLE: Record<BadgeKind, { glyph: string; className: string }> = {
  measured: { glyph: "●", className: "bg-accent-blue/15 text-accent-blue" },
  estimated: { glyph: "≈", className: "bg-accent-purple/15 text-accent-purple" },
  partial: { glyph: "◐", className: "bg-amber-500/15 text-amber-300" },
  unavailable: { glyph: "○", className: "bg-surface-3 text-ink-secondary" },
  complete: { glyph: "✓", className: "bg-accent-green/15 text-accent-green" },
  assumed: { glyph: "△", className: "bg-surface-3 text-ink-secondary" },
};

export function Badge({ kind, children }: { kind: BadgeKind; children?: string }) {
  const { t } = useTranslation();
  const style = STYLE[kind];
  return (
    <span className={`badge ${style.className}`}>
      <span aria-hidden="true">{style.glyph}</span>
      {children ?? t(`badge.${kind}`)}
    </span>
  );
}

export function badgeForCompleteness(completeness: EstimateCompleteness): BadgeKind {
  return completeness === "complete" ? "complete" : completeness === "partial" ? "partial" : "unavailable";
}

/** Completeness as a badge, with the word the rest of the app uses for it ("Complete" / "Partial" / "No estimate"). */
export function CompletenessBadge({ completeness }: { completeness: EstimateCompleteness }) {
  const { t } = useTranslation();
  return <Badge kind={badgeForCompleteness(completeness)}>{t(`completeness.${completeness}`)}</Badge>;
}
