import { describe, expect, it } from "vitest";
import en from "@/locales/en.json";
import { COST_STATUS_KEYS, END_REASON_KEYS, SOURCE_KEYS, costDisplay, endLabelKey, isLowerBound, isRunningSession, unmonitoredSeconds } from "./gaming";
import type { GamingEndReason, GamingLiveState } from "./types";

type Tree = { [key: string]: string | Tree };
function lookup(path: string): string | undefined {
  let node: string | Tree | undefined = en as Tree;
  for (const part of path.split(".")) {
    if (typeof node !== "object" || node === null) return undefined;
    node = node[part];
  }
  return typeof node === "string" ? node : undefined;
}

const live = (state: GamingLiveState["state"]): GamingLiveState => ({
  state,
  sessionId: 1,
  gameName: "Call of Duty: Warzone",
  exeName: "cod.exe",
  isManual: false,
  source: "knownCatalog",
  startedUtc: "2026-10-01T10:00:00Z",
  durationSeconds: 5058,
  monitoredSeconds: 5058,
  completeness: "complete",
  systemKwh: 0.47,
  systemIsLowerBound: false,
  costStatus: "priced",
  cost: 0.16,
  currency: "EUR",
});

describe("gaming cost presentation", () => {
  it("shows an amount only for a fully priced, complete session", () => {
    expect(costDisplay("priced", 0.16, "EUR")).toEqual({ kind: "amount", amount: 0.16, currency: "EUR" });
  });

  it("never shows an amount for a partial session -- even if a number is present", () => {
    // A lower-bound cost may exist on the wire; the main cost must not be shown as if it were the session's cost.
    expect(costDisplay("withheldPartial", 0.002, "EUR")).toEqual({ kind: "withheldPartial" });
  });

  it("never shows an amount when no price covers the energy", () => {
    expect(costDisplay("withheldUnpriced", 0.01, "EUR")).toEqual({ kind: "withheldUnpriced" });
  });

  it("does not turn a missing cost into zero", () => {
    expect(costDisplay("priced", null, "EUR")).toEqual({ kind: "none" });
    expect(costDisplay("noEstimate", null, null)).toEqual({ kind: "none" });
  });

  it("falls back to EUR only for the currency symbol, never for the amount", () => {
    expect(costDisplay("priced", 1.5, null)).toEqual({ kind: "amount", amount: 1.5, currency: "EUR" });
  });
});

describe("gaming session helpers", () => {
  it("separates duration from monitored time", () => {
    expect(unmonitoredSeconds(3600, 3000)).toBe(600);
    expect(unmonitoredSeconds(100, 100)).toBe(0);
    expect(unmonitoredSeconds(100, 100.0000001)).toBe(0); // floating-point noise never produces a negative
  });

  it("treats everything but a complete estimate as a lower bound", () => {
    expect(isLowerBound("complete")).toBe(false);
    expect(isLowerBound("partial")).toBe(true);
    expect(isLowerBound("none")).toBe(true);
  });

  it("shows a candidate as 'possible game', not as a running session", () => {
    expect(isRunningSession(live("candidate"))).toBe(false);
    expect(isRunningSession(live("active"))).toBe(true);
    expect(isRunningSession(live("ending"))).toBe(true);
    expect(isRunningSession(null)).toBe(false);
  });

  it("labels a running session as running and a finished one with its end reason", () => {
    expect(endLabelKey({ status: "active", endReason: null })).toBe("gaming.running");
    expect(endLabelKey({ status: "ended", endReason: "manualStop" })).toBe("gaming.endReason.manualStop");
    expect(endLabelKey({ status: "interrupted", endReason: null })).toBe("gaming.endReason.interrupted");
  });
});

describe("gaming localization coverage", () => {
  it("has an English string for every end reason, detection source and withheld-cost status", () => {
    const reasons: GamingEndReason[] = ["processExited", "manualStop", "monitoringStopped", "interrupted", "excluded"];
    for (const reason of reasons) expect(lookup(END_REASON_KEYS[reason]), reason).toBeTruthy();
    for (const key of Object.values(SOURCE_KEYS)) expect(lookup(key), key).toBeTruthy();
    for (const key of Object.values(COST_STATUS_KEYS)) expect(lookup(key), key).toBeTruthy();
  });
});
