import { describe, expect, it } from "vitest";
import { applyStatus, costVisibility, deriveQuality, type MonitoringView } from "./monitoring";
import type { StatusMessage, TickMessage } from "./types";

const baseTick: TickMessage = {
  type: "tick",
  sessionId: 1,
  sequence: 1,
  utc: "2026-01-01T00:00:00Z",
  cpuWatts: 60,
  cpuState: "valid",
  gpuWatts: 40,
  gpuState: "valid",
  otherComponentsWatts: 50,
  estimatedTotalWatts: 170,
  estimateCompleteness: "complete",
  sessionCompleteness: "complete",
  sessionSeconds: 10,
  sessionKwh: 0.01,
  sessionCostApprox: 0.003,
  currency: "EUR",
  elevated: true,
};

const partialTick: TickMessage = {
  ...baseTick,
  cpuWatts: null,
  cpuState: "implausibleZero",
  estimatedTotalWatts: 100,
  estimateCompleteness: "partial",
  sessionCompleteness: "partial",
  sessionCostApprox: null,
};

const running: MonitoringView = { recording: true, monitorDown: false, lastTick: null };
const status = (state: StatusMessage["state"]): StatusMessage => ({ type: "status", state });

describe("monitoring quality", () => {
  it("is stopped when nothing is recording", () => {
    expect(deriveQuality({ recording: false, monitorDown: false, lastTick: null })).toBe("stopped");
  });

  it("is starting until the first sample of a recording arrives", () => {
    expect(deriveQuality(running)).toBe("starting");
  });

  it("is complete only when the live estimate is complete", () => {
    expect(deriveQuality({ ...running, lastTick: baseTick })).toBe("complete");
  });

  it("is partial -- never complete -- when CPU power is unavailable", () => {
    expect(deriveQuality({ ...running, lastTick: partialTick })).toBe("partial");
  });

  it("is sensor-unavailable when no required input is valid", () => {
    expect(deriveQuality({ ...running, lastTick: { ...partialTick, gpuWatts: null, gpuState: "unavailable", estimateCompleteness: "none" } })).toBe("sensorUnavailable");
  });

  it("is unavailable when the monitor process is down, regardless of any stale tick", () => {
    expect(deriveQuality({ recording: true, monitorDown: true, lastTick: baseTick })).toBe("unavailable");
  });
});

describe("lifecycle events", () => {
  it("recording clears the stale tick and marks the monitor up", () => {
    const next = applyStatus({ recording: false, monitorDown: true, lastTick: baseTick }, status("recording"));
    expect(next).toEqual({ recording: true, monitorDown: false, lastTick: null });
  });

  it.each(["idle", "stopped", "exiting"] as const)("%s means not recording", (state) => {
    expect(applyStatus({ ...running, lastTick: baseTick }, status(state)).recording).toBe(false);
  });

  it.each(["restarting", "failed"] as const)("%s marks the monitor down", (state) => {
    const next = applyStatus({ ...running, lastTick: baseTick }, status(state));
    expect(next.monitorDown).toBe(true);
    expect(next.recording).toBe(false);
  });

  it.each(["recovered", "suspended", "resumed", "error"] as const)("%s is informational only", (state) => {
    const view = { ...running, lastTick: baseTick };
    expect(applyStatus(view, status(state))).toBe(view);
  });
});

describe("cost visibility", () => {
  it("shows a cost for a complete session", () => {
    expect(costVisibility(baseTick)).toBe("complete");
  });

  it("withholds the cost for a partial session", () => {
    expect(costVisibility(partialTick)).toBe("withheldPartial");
  });

  it("withholds the cost even if a value were present on a partial session", () => {
    expect(costVisibility({ ...partialTick, sessionCostApprox: 0 })).toBe("withheldPartial");
  });

  it("shows nothing before the first sample", () => {
    expect(costVisibility(null)).toBe("none");
  });
});
