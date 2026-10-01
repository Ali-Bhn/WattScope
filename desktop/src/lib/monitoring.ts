import type { StatusMessage, TickMessage } from "@/lib/types";

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

/** A cost is shown only for a Complete session; otherwise the reason is what is shown. */
export function costVisibility(tick: TickMessage | null): "none" | "complete" | "withheldPartial" {
  if (!tick) return "none";
  if (tick.sessionCompleteness === "complete" && tick.sessionCostApprox !== null) return "complete";
  return "withheldPartial";
}
