import { describe, expect, it } from "vitest";
import { describeError, errorMessage, ERROR_KEYS, type ErrorKind } from "./errors";

describe("error classification (what the user is told is chosen by kind, not by raw text)", () => {
  it.each([
    ["the sidecar is not running", "backendUnavailable"],
    ["the sidecar closed before responding", "backendUnavailable"],
    ["sidecar state poisoned", "backendUnavailable"],
    ["the sidecar did not respond in time", "timeout"],
    ["The database could not be opened: unable to open database file", "database"],
    ["SQLite Error 5: 'database is locked'.", "database"],
    ["disk I/O error", "database"],
    ["Internal error; see the log for detail.", "internal"],
    ["pricePerKwh is required.", "validation"],
    ["Session 7 does not exist.", "validation"],
    ["Another monitoring session is already active in this database. At most one session may be active at a time.", "validation"],
  ] as [string, ErrorKind][])("%s -> %s", (message, kind) => {
    expect(describeError(new Error(message)).kind).toBe(kind);
  });

  it("keeps the technical text for the collapsed details, without the 'Error:' prefix", () => {
    expect(describeError("Error: the sidecar is not running").detail).toBe("the sidecar is not running");
  });

  it("accepts a plain string, an Error and arbitrary values without throwing", () => {
    expect(errorMessage("x")).toBe("x");
    expect(errorMessage(new Error("y"))).toBe("y");
    expect(errorMessage({ a: 1 })).toBe('{"a":1}');
    expect(() => describeError(undefined)).not.toThrow();
    expect(() => describeError(null)).not.toThrow();
  });

  it("never carries a stack trace into the message", () => {
    const e = new Error("boom");
    expect(describeError(e).detail).toBe("boom");
    expect(describeError(e).detail).not.toContain("    at ");
  });

  it("has a title and a hint key for every kind", () => {
    for (const kind of ["backendUnavailable", "timeout", "database", "validation", "internal"] as ErrorKind[]) {
      expect(ERROR_KEYS[kind].title).toMatch(/^errors\./);
      expect(ERROR_KEYS[kind].hint).toMatch(/^errors\./);
    }
  });
});
