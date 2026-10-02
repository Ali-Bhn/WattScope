import { describe, expect, it } from "vitest";
import {
  isFutureDayKey,
  isFutureMonthKey,
  isValidDayKey,
  isValidMonthKey,
  localDayKey,
  localInputToUtcIso,
  localMonthKey,
  shiftDayKey,
  shiftMonthKey,
  toLocalInputValue,
} from "./dates";

// Dates are built from local parts so every assertion holds in any time zone the tests run in.
describe("local day and month keys", () => {
  it("uses the local calendar date, not the UTC one", () => {
    // 23:30 local on 31 Jan is already 1 Feb in UTC for any zone east of UTC-0:30 -- the local key must stay January.
    const lateEvening = new Date(2026, 0, 31, 23, 30);
    expect(localDayKey(lateEvening)).toBe("2026-01-31");
    expect(localMonthKey(lateEvening)).toBe("2026-01");
  });

  it("is the local month right after local midnight on the 1st (where toISOString().slice(0, 7) is wrong)", () => {
    const justAfterMidnight = new Date(2026, 1, 1, 0, 10);
    expect(localMonthKey(justAfterMidnight)).toBe("2026-02");
  });

  it("zero-pads", () => {
    expect(localDayKey(new Date(2026, 2, 5))).toBe("2026-03-05");
    expect(localMonthKey(new Date(2026, 2, 5))).toBe("2026-03");
  });
});

describe("validation", () => {
  it("accepts only real calendar days", () => {
    expect(isValidDayKey("2026-02-28")).toBe(true);
    expect(isValidDayKey("2028-02-29")).toBe(true); // leap day
    expect(isValidDayKey("2026-02-29")).toBe(false);
    expect(isValidDayKey("2026-13-01")).toBe(false);
    expect(isValidDayKey("2026-1-1")).toBe(false);
    expect(isValidDayKey("")).toBe(false);
  });

  it("accepts only real months", () => {
    expect(isValidMonthKey("2026-12")).toBe(true);
    expect(isValidMonthKey("2026-00")).toBe(false);
    expect(isValidMonthKey("2026-13")).toBe(false);
    expect(isValidMonthKey("26-01")).toBe(false);
  });
});

describe("moving between days and months", () => {
  it("steps whole calendar days across month and year ends", () => {
    expect(shiftDayKey("2026-01-31", 1)).toBe("2026-02-01");
    expect(shiftDayKey("2026-01-01", -1)).toBe("2025-12-31");
    expect(shiftDayKey("2028-02-28", 1)).toBe("2028-02-29");
  });

  it("steps whole calendar days across a daylight-saving change (a day is not always 24 h)", () => {
    // 2026-03-29 is the spring-forward day in much of Europe; stepping by calendar days must still land on the next date.
    expect(shiftDayKey("2026-03-28", 1)).toBe("2026-03-29");
    expect(shiftDayKey("2026-03-29", 1)).toBe("2026-03-30");
    expect(shiftDayKey("2026-10-25", 1)).toBe("2026-10-26");
  });

  it("steps months across year ends", () => {
    expect(shiftMonthKey("2026-12", 1)).toBe("2027-01");
    expect(shiftMonthKey("2026-01", -1)).toBe("2025-12");
    expect(shiftMonthKey("2026-03", -12)).toBe("2025-03");
  });

  it("leaves an invalid key untouched rather than inventing a date", () => {
    expect(shiftDayKey("nope", 1)).toBe("nope");
    expect(shiftMonthKey("nope", 1)).toBe("nope");
  });

  it("knows what is in the future", () => {
    const now = new Date(2026, 5, 15, 12, 0);
    expect(isFutureDayKey("2026-06-16", now)).toBe(true);
    expect(isFutureDayKey("2026-06-15", now)).toBe(false);
    expect(isFutureMonthKey("2026-07", now)).toBe(true);
    expect(isFutureMonthKey("2026-06", now)).toBe(false);
  });
});

describe("datetime-local input conversion", () => {
  it("round-trips a local wall-clock time through a UTC instant", () => {
    const local = new Date(2026, 9, 2, 14, 30);
    const iso = localInputToUtcIso(toLocalInputValue(local));
    expect(iso).toBe(local.toISOString());
  });

  it("formats the value an input expects, at minute precision", () => {
    expect(toLocalInputValue(new Date(2026, 0, 5, 9, 7, 59))).toBe("2026-01-05T09:07");
  });

  it("rejects anything that is not a date-time", () => {
    expect(localInputToUtcIso("")).toBeNull();
    expect(localInputToUtcIso("2026-10-02")).toBeNull();
    expect(localInputToUtcIso("2026-13-45T25:61")).toBeNull();
    expect(localInputToUtcIso("tomorrow")).toBeNull();
  });
});
