import { describe, expect, it } from "vitest";
import { isCurrencyCode, parseNumber, parseSetting, SPECS, validationKey, validationParams, type SpecName } from "./validation";

describe("number parsing", () => {
  it("accepts a dot or a comma as the decimal separator and returns a canonical dot form", () => {
    expect(parseSetting("0.30", "price")).toEqual({ ok: true, value: 0.3, canonical: "0.30" });
    expect(parseSetting("0,30", "price")).toEqual({ ok: true, value: 0.3, canonical: "0.30" });
    expect(parseSetting("  .5 ", "price")).toEqual({ ok: true, value: 0.5, canonical: "0.5" });
    expect(parseSetting("2.", "price")).toEqual({ ok: true, value: 2, canonical: "2" });
  });

  it("rejects empty and non-numeric input with a specific reason", () => {
    expect(parseSetting("", "price")).toEqual({ ok: false, error: "empty" });
    expect(parseSetting("   ", "price")).toEqual({ ok: false, error: "empty" });
    expect(parseSetting("abc", "price")).toEqual({ ok: false, error: "notNumber" });
    expect(parseSetting("1e3", "price")).toEqual({ ok: false, error: "notNumber" });
    expect(parseSetting("-0.1", "price")).toEqual({ ok: false, error: "notNumber" });
    expect(parseSetting("1,234.5", "otherWatts")).toEqual({ ok: false, error: "notNumber" }); // ambiguous thousands separator
    expect(parseSetting("0x10", "otherWatts")).toEqual({ ok: false, error: "notNumber" });
    expect(parseSetting("NaN", "otherWatts")).toEqual({ ok: false, error: "notNumber" });
    expect(parseSetting("Infinity", "otherWatts")).toEqual({ ok: false, error: "notNumber" });
  });

  it("is strict about whole numbers where a setting is a count", () => {
    expect(parseSetting("1000", "intervalMs")).toMatchObject({ ok: true, value: 1000 });
    expect(parseSetting("1000.5", "intervalMs")).toEqual({ ok: false, error: "notInteger" });
    expect(parseSetting("7.0", "retentionDays")).toEqual({ ok: true, value: 7, canonical: "7.0" }); // 7.0 is a whole number
  });

  it("limits decimal places only where the setting has a limit, ignoring trailing zeros", () => {
    expect(parseSetting("0.123456", "price")).toMatchObject({ ok: true });
    expect(parseSetting("0.1234567", "price")).toEqual({ ok: false, error: "tooManyDecimals" });
    expect(parseSetting("0.1234560", "price")).toMatchObject({ ok: true }); // the 7th digit is a zero, not a precision claim
    expect(parseSetting("40.25", "otherWatts")).toEqual({ ok: false, error: "tooManyDecimals" });
  });
});

describe("ranges (every setting states its limits)", () => {
  const edge = (name: SpecName, low: string, high: string, below: string, above: string) => {
    expect(parseSetting(low, name).ok, `${name} low`).toBe(true);
    expect(parseSetting(high, name).ok, `${name} high`).toBe(true);
    expect(parseSetting(below, name), `${name} below`).toEqual({ ok: false, error: "belowMin" });
    expect(parseSetting(above, name), `${name} above`).toEqual({ ok: false, error: "aboveMax" });
  };

  it("electricity price: 0 to 100 per kWh", () => {
    expect(parseSetting("0", "price").ok).toBe(true);
    expect(parseSetting("100", "price").ok).toBe(true);
    expect(parseSetting("100.01", "price")).toEqual({ ok: false, error: "aboveMax" });
  });

  it("other components: 0 to 2000 W", () => {
    expect(parseSetting("0", "otherWatts").ok).toBe(true);
    expect(parseSetting("2000", "otherWatts").ok).toBe(true);
    expect(parseSetting("2000.5", "otherWatts")).toEqual({ ok: false, error: "aboveMax" });
  });

  it("PSU efficiency: 50 to 100 %", () => edge("psuPercent", "50", "100", "49.9", "100.1"));
  it("sampling interval: 250 to 60000 ms", () => edge("intervalMs", "250", "60000", "249", "60001"));
  it("raw retention: 0 to 3650 days", () => {
    expect(parseSetting("0", "retentionDays").ok).toBe(true);
    expect(parseSetting("3650", "retentionDays").ok).toBe(true);
    expect(parseSetting("3651", "retentionDays")).toEqual({ ok: false, error: "aboveMax" });
  });
  it("history deletion age: 30 to 3650 days", () => edge("historyDays", "30", "3650", "29", "3651"));

  it("the UI ranges are inside what the backend accepts (the backend is still the authority)", () => {
    // Mirrors AppHandlers / EstimationSettings / SqliteEnergyStore.
    expect(SPECS.price.max).toBeLessThanOrEqual(100);
    expect(SPECS.otherWatts.max).toBeLessThanOrEqual(2000);
    expect(SPECS.psuPercent.min).toBeGreaterThan(0);
    expect(SPECS.psuPercent.max).toBeLessThanOrEqual(100);
    expect(SPECS.intervalMs.min).toBeGreaterThanOrEqual(250);
    expect(SPECS.intervalMs.max).toBeLessThanOrEqual(60_000);
    expect(SPECS.retentionDays.max).toBeLessThanOrEqual(3650);
    expect(SPECS.historyDays.min).toBeGreaterThanOrEqual(30);
  });
});

describe("messages", () => {
  it("maps each error to a localization key and supplies the real limits to interpolate", () => {
    expect(validationKey("aboveMax")).toBe("validation.aboveMax");
    expect(validationParams("psuPercent")).toEqual({ min: 50, max: 100, decimals: 1 });
    expect(validationParams("intervalMs")).toEqual({ min: 250, max: 60000, decimals: 0 });
  });

  it("parseNumber works with an ad-hoc spec", () => {
    expect(parseNumber("5", { min: 1, max: 10, integer: true })).toMatchObject({ ok: true, value: 5 });
    expect(parseNumber("11", { min: 1, max: 10 })).toEqual({ ok: false, error: "aboveMax" });
  });
});

describe("currency codes", () => {
  it("are exactly three upper-case letters", () => {
    expect(isCurrencyCode("EUR")).toBe(true);
    expect(isCurrencyCode("eur")).toBe(false);
    expect(isCurrencyCode("EU")).toBe(false);
    expect(isCurrencyCode("EURO")).toBe(false);
    expect(isCurrencyCode("E1R")).toBe(false);
    expect(isCurrencyCode("")).toBe(false);
  });
});
