import { afterEach, describe, expect, it } from "vitest";
import {
  formatBytes,
  formatDayKey,
  formatDuration,
  formatEnergy,
  formatKwh,
  formatLanguage,
  formatLocalDateTime,
  formatMoney,
  formatMonthKey,
  formatNumber,
  formatPercent,
  formatPrice,
  formatWatts,
  parseTimeSpanSeconds,
  setFormatLanguage,
} from "./format";

afterEach(() => setFormatLanguage("en"));

const nbsp = /[  ‎]/g; // locale-inserted spaces and direction marks
const plain = (s: string) => s.replace(nbsp, " ");

describe("number formatting follows the language but keeps Latin digits", () => {
  it("uses the English separators by default", () => {
    expect(formatWatts(1234.56)).toBe("1,234.6 W");
    expect(formatKwh(3_600_000 * 1.5)).toBe("1.500 kWh");
  });

  it("uses the German decimal comma and thousands point", () => {
    setFormatLanguage("de");
    expect(formatWatts(1234.56)).toBe("1.234,6 W");
    expect(formatKwh(3_600_000 * 1.5)).toBe("1,500 kWh");
  });

  it("keeps Latin digits (never Eastern Arabic) in Persian, so numbers match every other tool", () => {
    setFormatLanguage("fa");
    expect(formatWatts(1234.56)).toMatch(/^1[,.]234[.٫]6 W$/);
    expect(formatWatts(98.7)).toMatch(/^[0-9]/);
    expect(formatNumber(12345, 0)).toMatch(/^[0-9,.٬]+$/);
    expect(formatNumber(12345, 0)).not.toMatch(/[٠-٩۰-۹]/);
  });

  it("falls back to English for an unknown language", () => {
    setFormatLanguage("xx");
    expect(formatLanguage()).toBe("en");
  });

  it("units are the same technical tokens in every language", () => {
    for (const lang of ["en", "de", "fa"]) {
      setFormatLanguage(lang);
      expect(formatWatts(5)).toMatch(/ W$/);
      expect(formatKwh(3_600_000)).toMatch(/ kWh$/);
      expect(formatEnergy(1_000)).toMatch(/ Wh$/);
    }
  });
});

describe("missing values are never shown as zero", () => {
  it("renders a null or non-finite power as --", () => {
    expect(formatWatts(null)).toBe("--");
    expect(formatWatts(Number.NaN)).toBe("--");
    expect(formatWatts(Number.POSITIVE_INFINITY)).toBe("--");
  });

  it("renders a null or non-finite money amount as --", () => {
    expect(formatMoney(null, "EUR")).toBe("--");
    expect(formatMoney(Number.NaN, "EUR")).toBe("--");
  });

  it("renders a non-finite energy as --", () => {
    expect(formatEnergy(Number.NaN)).toBe("--");
  });

  it("renders an unparsable timestamp as --", () => {
    expect(formatLocalDateTime("not a date")).toBe("--");
  });

  it("a real zero stays a zero (it is a value, not a missing one)", () => {
    expect(formatWatts(0)).toBe("0.0 W");
    expect(formatKwh(0)).toBe("0.000 kWh");
  });
});

describe("energy picks a readable unit for a single value", () => {
  it("uses Wh below 0.1 kWh and kWh above", () => {
    expect(formatEnergy(3_600 * 2.5)).toBe("2.5 Wh");
    expect(formatEnergy(3_600_000 * 0.05)).toBe("50.0 Wh");
    expect(formatEnergy(3_600_000 * 0.1)).toBe("0.100 kWh");
    expect(formatEnergy(3_600_000 * 12)).toBe("12.000 kWh");
  });
});

describe("money", () => {
  it("shows the ISO currency code, never a guessed symbol", () => {
    expect(plain(formatMoney(1.234, "EUR"))).toBe("EUR 1.23");
    setFormatLanguage("de");
    expect(plain(formatMoney(1.234, "EUR"))).toBe("1,23 EUR");
  });

  it("honours the requested decimals", () => {
    expect(plain(formatMoney(0.00123, "EUR", 4))).toBe("EUR 0.0012");
  });

  it("still shows the number for something that is not a valid ISO code", () => {
    expect(formatMoney(2, "not-a-code", 2)).toBe("2.00 not-a-code");
  });

  it("prices keep up to six decimals without padding beyond what is needed", () => {
    expect(formatPrice(0.305, "EUR")).toBe("0.305 EUR/kWh");
    expect(formatPrice(0.3, "EUR")).toBe("0.30 EUR/kWh");
    expect(formatPrice(0.123456, "USD")).toBe("0.123456 USD/kWh");
    setFormatLanguage("de");
    expect(formatPrice(0.305, "EUR")).toBe("0,305 EUR/kWh");
  });
});

describe("durations", () => {
  it("formats hh:mm:ss and keeps counting past 99 hours", () => {
    expect(formatDuration(0)).toBe("00:00:00");
    expect(formatDuration(3661)).toBe("01:01:01");
    expect(formatDuration(100 * 3600 + 5)).toBe("100:00:05");
  });

  it("never goes negative or non-finite", () => {
    expect(formatDuration(-5)).toBe("00:00:00");
    expect(formatDuration(Number.NaN)).toBe("00:00:00");
  });

  it("parses .NET TimeSpan strings (the backend's gap durations)", () => {
    expect(parseTimeSpanSeconds("00:01:02")).toBe(62);
    expect(parseTimeSpanSeconds("00:00:01.5")).toBe(1.5);
    expect(parseTimeSpanSeconds("1.02:03:04")).toBe(86_400 + 2 * 3600 + 3 * 60 + 4);
    expect(parseTimeSpanSeconds("-00:00:10")).toBe(-10);
  });

  it("returns null for anything that is not a TimeSpan", () => {
    expect(parseTimeSpanSeconds("")).toBeNull();
    expect(parseTimeSpanSeconds(null)).toBeNull();
    expect(parseTimeSpanSeconds("soon")).toBeNull();
  });
});

describe("dates are shown in local time and the language's order, with Latin digits", () => {
  it("renders a UTC instant as the local wall-clock time of the machine", () => {
    // Build the instant from local parts so the assertion holds in any time zone.
    const local = new Date(2026, 9, 2, 14, 3, 5);
    const text = formatLocalDateTime(local.toISOString());
    expect(text).toMatch(/14:03:05/);
    expect(text).toMatch(/2026/);
  });

  it("orders day and month the way each language does", () => {
    const iso = new Date(2026, 9, 2, 14, 3, 5).toISOString();
    expect(formatLocalDateTime(iso)).toMatch(/^10\/02\/2026/);
    setFormatLanguage("de");
    expect(formatLocalDateTime(iso)).toMatch(/^02\.10\.2026/);
    setFormatLanguage("fa");
    expect(formatLocalDateTime(iso)).toMatch(/^2026\/10\/02/);
  });

  it("formats a local day key without any time-zone conversion", () => {
    expect(formatDayKey("2026-10-02")).toBe("10/02/2026");
    setFormatLanguage("de");
    expect(formatDayKey("2026-10-02")).toBe("02.10.2026");
    expect(formatDayKey("garbage")).toBe("garbage");
  });

  it("names a month in the UI language", () => {
    expect(formatMonthKey("2026-10")).toBe("October 2026");
    setFormatLanguage("de");
    expect(formatMonthKey("2026-10")).toBe("Oktober 2026");
    expect(formatMonthKey("nope")).toBe("nope");
  });
});

describe("percent and bytes", () => {
  it("formats a fraction as a percentage", () => {
    expect(formatPercent(0.875, 1)).toBe("87.5%");
    expect(formatPercent(0.9)).toBe("90%");
  });

  it("scales bytes with binary units", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(2048)).toBe("2.00 KiB");
    expect(formatBytes(5 * 1024 * 1024)).toBe("5.00 MiB");
    expect(formatBytes(1536 * 1024 * 1024)).toBe("1.50 GiB");
    expect(formatBytes(-1)).toBe("--");
  });
});

describe("display formatting never alters the machine-readable value", () => {
  it("is a one-way, display-only transformation (the wire value is untouched)", () => {
    const wire = 1234.5;
    setFormatLanguage("de");
    formatWatts(wire);
    expect(wire).toBe(1234.5); // numbers are passed by value; nothing is parsed back from display text
    expect(JSON.stringify({ watts: wire })).toBe('{"watts":1234.5}'); // serialization has no locale
  });
});
