import type { BackendState, StatusMessage, TickMessage } from "@/lib/types";

/**
 * What the user should be told about monitoring right now. Exactly one of
 * these at a time -- never a blend, and never "complete" without evidence.
 */
export type MonitoringQuality =
  | "starting" // the monitor is up but no sample has arrived yet
  | "complete" // every required input is being measured
  | "partial" // some required input (usually CPU package power) is unavailable
  | "sensorUnavailable" // no required input is available
  | "stopped" // monitoring is not running (stopped by the user, or idle)
  | "unavailable"; // the monitor process itself is not running (crashed / gave up)

export interface MonitoringView {
  /** Whether the sidecar reports an active recording. */
  recording: boolean;
  /** The sidecar process failed and automatic restarts were exhausted (or it is mid-restart). */
  monitorDown: boolean;
  lastTick: TickMessage | null;
}

export function deriveQuality(view: MonitoringView): MonitoringQuality {
  if (view.monitorDown) return "unavailable";
  if (!view.recording) return "stopped";
  if (!view.lastTick) return "starting";
  switch (view.lastTick.estimateCompleteness) {
    case "complete":
      return "complete";
    case "partial":
      return "partial";
    default:
      return "sensorUnavailable";
  }
}

/** True while WattScope is recording (including the moment before the first sample). */
export function isRecording(quality: MonitoringQuality): boolean {
  return quality === "starting" || quality === "complete" || quality === "partial" || quality === "sensorUnavailable";
}

/** Folds a lifecycle event into the view. Pure, so the state machine is unit-testable. */
export function applyStatus(view: MonitoringView, status: StatusMessage): MonitoringView {
  switch (status.state) {
    case "recording":
      return { ...view, recording: true, monitorDown: false, lastTick: null };
    case "idle":
    case "stopped":
    case "exiting":
      return { ...view, recording: false, monitorDown: false, lastTick: null };
    case "restarting":
    case "failed":
      return { ...view, recording: false, monitorDown: true, lastTick: null };
    default:
      return view; // recovered / suspended / resumed / error: informational, not a state change
  }
}

/**
 * Folds the host's view of the sidecar process into the monitoring view. A process that is restarting or
 * has failed cannot be recording, whatever the last status said; a running one changes nothing (the
 * monitoring status events decide whether it is recording).
 */
export function applyBackend(view: MonitoringView, backend: BackendState): MonitoringView {
  if (backend.state === "restarting" || backend.state === "failed") {
    return { ...view, recording: false, monitorDown: true, lastTick: null };
  }
  return view.monitorDown && backend.state === "running" ? { ...view, monitorDown: false } : view;
}

/**
 * A cost is shown only for a Complete session that has a price; otherwise the reason is what is shown:
 * an incomplete estimate ("withheldPartial") is a different reason from there being no electricity price
 * ("noPrice"), and the two are never conflated.
 */
export function costVisibility(tick: TickMessage | null): "none" | "complete" | "withheldPartial" | "noPrice" {
  if (!tick) return "none";
  if (tick.sessionCompleteness === "complete") return tick.sessionCostApprox !== null ? "complete" : "noPrice";
  return "withheldPartial";
}

/**
 * The headline energy of the running session. A Complete session shows its complete energy; otherwise the
 * figure is a lower bound (complete + partial energy, "at least") and says so. No energy yet is "none",
 * never a zero.
 */
export type SessionEnergyView =
  | { kind: "none" }
  | { kind: "complete"; kwh: number }
  | { kind: "lowerBound"; kwh: number };

export function sessionEnergy(tick: TickMessage | null): SessionEnergyView {
  if (!tick) return { kind: "none" };
  const partial = tick.sessionPartialKwh ?? 0;
  if (tick.sessionCompleteness === "none" && tick.sessionKwh === 0 && partial === 0) return { kind: "none" };
  if (tick.sessionCompleteness === "complete") return { kind: "complete", kwh: tick.sessionKwh };
  return { kind: "lowerBound", kwh: tick.sessionKwh + partial };
}
