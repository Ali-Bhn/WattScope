import { describe, expect, it } from "vitest";
import { shouldShowFirstRun } from "./firstRun";
import type { AppState } from "./types";

const state = (firstRunCompleted: boolean): AppState => ({ firstRunCompleted, appVersion: "0.1.0", schemaVersion: 4 });

describe("first-run introduction", () => {
  it("is offered when the sidecar says it has not been completed", () => {
    expect(shouldShowFirstRun(state(false), false)).toBe(true);
  });

  it("is not offered once completed (completion is persisted by the sidecar, so this survives restarts)", () => {
    expect(shouldShowFirstRun(state(true), false)).toBe(false);
  });

  it("is not shown while the state is unknown (loading, backend down, error) -- no flash for returning users", () => {
    expect(shouldShowFirstRun(null, false)).toBe(false);
  });

  it("dismissing it for the session hides it, but only for the session", () => {
    expect(shouldShowFirstRun(state(false), true)).toBe(false);
    expect(shouldShowFirstRun(state(false), false)).toBe(true);
  });
});
