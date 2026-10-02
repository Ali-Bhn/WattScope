import type { CostBreakdown, EnergyReport, EnergyTotals, EstimateCompleteness } from "@/lib/types";

/**
 * Presentation rules for energy totals and costs, shared by Reports, the
 * Dashboard and Gaming. Pure, so the honesty rules are unit-tested:
 *
 *  - Complete and Partial estimated energy are *never added into one figure*.
 *    Complete energy covers time in which every required input was measured;
 *    Partial energy covers time in which one was missing (a lower bound).
 *    Where both exist they are reported side by side.
 *  - A missing figure is "none", never a zero.
 *  - Cost belongs to the complete energy that had a price; a partial cost is a
 *    separate lower bound, and energy consumed before any price existed is
 *    reported as unpriced, not as free.
 */

export type EnergyKind = "none" | "complete" | "partial" | "mixed";

export interface EnergyModel {
  kind: EnergyKind;
  completeJoules: number;
  partialJoules: number;
  /** Seconds of monitored time with no usable estimate (neither complete nor partial). */
  uncoveredSeconds: number;
}

/** Floating-point summation noise only; never a tolerance for missing data. */
const EPSILON_SECONDS = 1e-6;

export function energyModel(totals: EnergyTotals): EnergyModel {
  const hasComplete = totals.estimatedCompleteSeconds > EPSILON_SECONDS;
  const hasPartial = totals.estimatedPartialSeconds > EPSILON_SECONDS;
  const kind: EnergyKind = hasComplete && hasPartial ? "mixed" : hasComplete ? "complete" : hasPartial ? "partial" : "none";
  return {
    kind,
    completeJoules: totals.estimatedCompleteJoules,
    partialJoules: totals.estimatedPartialJoules,
    uncoveredSeconds: Math.max(0, totals.monitoredSeconds - totals.estimatedCompleteSeconds - totals.estimatedPartialSeconds),
  };
}

export type CostKind = "amount" | "partialOnly" | "unpricedOnly" | "none";

export interface CostModel {
  kind: CostKind;
  /** Cost of the complete energy that had a price (rounded by the backend). */
  amount: number | null;
  /** Lower-bound cost of the partial energy, reported separately and never added to `amount`. */
  partialLowerBound: number | null;
  unpricedJoules: number;
  currency: string;
  /** True when the amount only covers part of the report's energy (partial or unpriced energy exists next to it). */
  incomplete: boolean;
}

export function costModel(cost: CostBreakdown, totals: EnergyTotals): CostModel {
  const energy = energyModel(totals);
  const amount = cost.estimatedCostRounded;
  const partialLowerBound = cost.partialEstimateCost ?? null;
  const unpriced = cost.unpricedJoules > 0;
  const kind: CostKind =
    amount !== null ? "amount" : partialLowerBound !== null ? "partialOnly" : unpriced && energy.completeJoules > 0 ? "unpricedOnly" : "none";
  return {
    kind,
    amount,
    partialLowerBound,
    unpricedJoules: cost.unpricedJoules,
    currency: cost.currency,
    incomplete: amount !== null && (energy.kind === "mixed" || energy.kind === "partial" || unpriced),
  };
}

/** The completeness a report/day/session carries, derived from its totals (the same rule the backend uses). */
export function completenessOf(totals: EnergyTotals): EstimateCompleteness {
  const e = energyModel(totals);
  if (e.kind === "none") return "none";
  return totals.estimatedCompleteSeconds >= totals.monitoredSeconds * (1 - 1e-9) ? "complete" : "partial";
}

export function hasData(report: EnergyReport): boolean {
  return report.totals.monitoredSeconds > 0 || !report.totals.isEmpty;
}

/** Sums monthly/daily rows for a footer without ever merging Complete into Partial. */
export function sumTotals(rows: readonly EnergyTotals[]): EnergyTotals {
  const zero: EnergyTotals = {
    monitoredSeconds: 0,
    cpuPackageJoules: 0,
    cpuPackageSeconds: 0,
    gpuBoardJoules: 0,
    gpuBoardSeconds: 0,
    estimatedCompleteJoules: 0,
    estimatedCompleteSeconds: 0,
    estimatedPartialJoules: 0,
    estimatedPartialSeconds: 0,
    isEmpty: true,
  };
  return rows.reduce<EnergyTotals>(
    (acc, t) => ({
      monitoredSeconds: acc.monitoredSeconds + t.monitoredSeconds,
      cpuPackageJoules: acc.cpuPackageJoules + t.cpuPackageJoules,
      cpuPackageSeconds: acc.cpuPackageSeconds + t.cpuPackageSeconds,
      gpuBoardJoules: acc.gpuBoardJoules + t.gpuBoardJoules,
      gpuBoardSeconds: acc.gpuBoardSeconds + t.gpuBoardSeconds,
      estimatedCompleteJoules: acc.estimatedCompleteJoules + t.estimatedCompleteJoules,
      estimatedCompleteSeconds: acc.estimatedCompleteSeconds + t.estimatedCompleteSeconds,
      estimatedPartialJoules: acc.estimatedPartialJoules + t.estimatedPartialJoules,
      estimatedPartialSeconds: acc.estimatedPartialSeconds + t.estimatedPartialSeconds,
      isEmpty: acc.isEmpty && t.isEmpty,
    }),
    zero,
  );
}
