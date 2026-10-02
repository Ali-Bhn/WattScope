// Display formatting, in one place. Numbers, dates and times follow the UI
// language's conventions (decimal separator, date order), but the *digits*
// stay Latin and the calendar stays Gregorian in every language: a Jalali date
// or Eastern Arabic digits on a power reading or a log timestamp would read as
// a different number to anyone comparing with another tool. Units (W, Wh, kWh,
// EUR/CPU/GPU) are technical identifiers and are never translated. Pair every
// call site with <Num> so the text stays left-to-right inside RTL layouts.
//
// Formatting is display-only: persisted and transmitted values are always
// machine-readable (invariant decimal, UTC ISO-8601) and never pass through here.

export type FormatLanguage = "en" | "de" | "fa";

// "-u-nu-latn" forces Latin digits; "-ca-gregory" the Gregorian calendar.
const LOCALE_TAGS: Record<FormatLanguage, string> = {
  en: "en-US-u-nu-latn-ca-gregory",
  de: "de-DE-u-nu-latn-ca-gregory",
  fa: "fa-IR-u-nu-latn-ca-gregory",
};

let language: FormatLanguage = "en";

/** Called by i18n when the UI language changes; components re-render through useTranslation. */
export function setFormatLanguage(lang: string) {
  language = lang === "de" || lang === "fa" ? lang : "en";
}

export function formatLanguage(): FormatLanguage {
  return language;
}

function tag(): string {
  return LOCALE_TAGS[language];
}

export function formatNumber(value: number, minDecimals: number, maxDecimals = minDecimals): string {
  return value.toLocaleString(tag(), { minimumFractionDigits: minDecimals, maximumFractionDigits: maxDecimals });
}

export function formatWatts(watts: number | null, decimals = 1): string {
  if (watts === null || !Number.isFinite(watts)) return "--";
  return `${formatNumber(watts, decimals)} W`;
}

export function formatKwh(joules: number, decimals = 3): string {
  return `${formatNumber(joules / 3_600_000, decimals)} kWh`;
}

export const JOULES_PER_KWH = 3_600_000;
export const JOULES_PER_WH = 3_600;

/**
 * Energy for a single headline value: Wh below 0.1 kWh (so the first minutes of a
 * session do not read "0.002 kWh"), kWh above. Tables use formatKwh so a column keeps one unit.
 */
export function formatEnergy(joules: number): string {
  if (!Number.isFinite(joules)) return "--";
  return Math.abs(joules) < 0.1 * JOULES_PER_KWH ? `${formatNumber(joules / JOULES_PER_WH, 1)} Wh` : formatKwh(joules);
}

export function formatKwhValue(kwh: number): string {
  return formatEnergy(kwh * JOULES_PER_KWH);
}

/** Money with the ISO currency code (EUR 1.23): unambiguous across languages, and never a guess at a symbol. */
export function formatMoney(amount: number | null, currency: string, decimals = 2): string {
  if (amount === null || !Number.isFinite(amount)) return "--";
  try {
    return amount.toLocaleString(tag(), {
      style: "currency",
      currency,
      currencyDisplay: "code",
      minimumFractionDigits: decimals,
      maximumFractionDigits: decimals,
    });
  } catch {
    return `${formatNumber(amount, decimals)} ${currency}`; // not a valid ISO code: still show the number honestly
  }
}

/** A tariff such as 0.305 EUR/kWh: at least 2 decimals, up to the 6 the backend accepts, no padding beyond what was entered. */
export function formatPrice(pricePerKwh: number, currency: string): string {
  return `${formatNumber(pricePerKwh, 2, 6)} ${currency}/kWh`;
}

/** hh:mm:ss (more than 99 hours simply keeps counting). Technical, not locale-dependent. */
export function formatDuration(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(Number.isFinite(totalSeconds) ? totalSeconds : 0));
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  const pad = (n: number) => n.toString().padStart(2, "0");
  return `${pad(h)}:${pad(m)}:${pad(s)}`;
}

/** Parses a .NET TimeSpan string ("00:01:02.5", "1.02:03:04") into seconds; null when it is not one. */
export function parseTimeSpanSeconds(text: string | null | undefined): number | null {
  if (!text) return null;
  const match = /^(-)?(?:(\d+)\.)?(\d+):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(text.trim());
  if (!match) return null;
  const [, sign, days, h, m, s, fraction] = match;
  const seconds = Number(days ?? 0) * 86_400 + Number(h) * 3600 + Number(m) * 60 + Number(s) + (fraction ? Number(`0.${fraction}`) : 0);
  return sign ? -seconds : seconds;
}

const DATE_TIME: Intl.DateTimeFormatOptions = {
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hour12: false,
};

/** A persisted UTC instant in the user's local time zone. */
export function formatLocalDateTime(utcIso: string): string {
  const date = new Date(utcIso);
  if (Number.isNaN(date.getTime())) return "--";
  return date.toLocaleString(tag(), DATE_TIME);
}

/** Local calendar date of a "YYYY-MM-DD" key (no time zone conversion: it already is a local date). */
export function formatDayKey(key: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(key);
  if (!match) return key;
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return date.toLocaleDateString(tag(), { year: "numeric", month: "2-digit", day: "2-digit" });
}

export function formatMonthKey(key: string): string {
  const match = /^(\d{4})-(\d{2})$/.exec(key);
  if (!match) return key;
  const date = new Date(Number(match[1]), Number(match[2]) - 1, 1);
  return date.toLocaleDateString(tag(), { year: "numeric", month: "long" });
}

export function formatPercent(fraction: number, decimals = 0): string {
  return `${formatNumber(fraction * 100, decimals)}%`;
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "--";
  if (bytes < 1024) return `${formatNumber(bytes, 0)} B`;
  const units = ["KiB", "MiB", "GiB", "TiB"];
  let value = bytes / 1024;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024;
    i++;
  }
  return `${formatNumber(value, value < 10 ? 2 : 1)} ${units[i]}`;
}
