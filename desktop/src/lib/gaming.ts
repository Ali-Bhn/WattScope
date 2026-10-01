import type {
  GamingCostStatus,
  GamingEndReason,
  GamingLiveState,
  GamingSessionSummary,
  GameDetectionSource,
  EstimateCompleteness,
} from "@/lib/types";

/**
 * Presentation rules for gaming analytics. Pure, so the honesty rules are
 * unit-tested: a cost is only ever *shown as an amount* when the sidecar says
 * it is fully priced from a Complete estimate; a partial estimate is a lower
 * bound and says so; nothing here turns a missing value into a zero.
 */

export type CostDisplay =
  | { kind: "amount"; amount: number; currency: string }
  | { kind: "withheldPartial" }
  | { kind: "withheldUnpriced" }
  | { kind: "none" };

export function costDisplay(status: GamingCostStatus, cost: number | null, currency: string | null): CostDisplay {
  if (status === "priced" && cost !== null) return { kind: "amount", amount: cost, currency: currency ?? "EUR" };
  if (status === "withheldPartial") return { kind: "withheldPartial" };
  if (status === "withheldUnpriced") return { kind: "withheldUnpriced" };
  return { kind: "none" };
}

/** Wall-clock length minus the part that was actually monitored (sleep, stalls, a restarted monitor). */
export function unmonitoredSeconds(durationSeconds: number, monitoredSeconds: number): number {
  return Math.max(0, durationSeconds - monitoredSeconds);
}

/** True when a session's system figures are lower bounds, i.e. anything but a Complete estimate. */
export function isLowerBound(completeness: EstimateCompleteness): boolean {
  return completeness !== "complete";
}

/** The live state worth showing as a running gaming session (a candidate is only a "possible game"). */
export function isRunningSession(live: GamingLiveState | null | undefined): boolean {
  return !!live && live.state !== "candidate";
}

export const END_REASON_KEYS: Record<GamingEndReason, string> = {
  processExited: "gaming.endReason.processExited",
  manualStop: "gaming.endReason.manualStop",
  monitoringStopped: "gaming.endReason.monitoringStopped",
  interrupted: "gaming.endReason.interrupted",
  excluded: "gaming.endReason.excluded",
};

export const SOURCE_KEYS: Record<GameDetectionSource, string> = {
  userMapping: "gaming.source.userMapping",
  knownCatalog: "gaming.source.knownCatalog",
  launcherChild: "gaming.source.launcherChild",
  libraryPath: "gaming.source.libraryPath",
  manual: "gaming.source.manual",
};

export const COST_STATUS_KEYS: Record<Exclude<GamingCostStatus, "priced">, string> = {
  withheldPartial: "gaming.costStatus.withheldPartial",
  withheldUnpriced: "gaming.costStatus.withheldUnpriced",
  noEstimate: "gaming.costStatus.noEstimate",
};

/** History row label for how a session ended (a running session has no end reason yet). */
export function endLabelKey(session: Pick<GamingSessionSummary, "status" | "endReason">): string {
  if (session.status === "active") return "gaming.running";
  return session.endReason ? END_REASON_KEYS[session.endReason] : "gaming.endReason.interrupted";
}
