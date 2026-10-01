// Numbers, units, and timestamps are always formatted with fixed
// (Latin-digit, Gregorian) conventions regardless of UI language -- Part H
// explicitly requires these stay readable, not just literally translated,
// and a Jalali calendar or Eastern Arabic digits on a log timestamp would be
// actively confusing, not a faithful localization. Pair every call site with
// the <Num> component so the span is also forced to ltr direction/isolation
// when embedded in RTL surrounding text.
const NUMERIC_LOCALE = "en-US";

export function formatWatts(watts: number | null, decimals = 1): string {
  if (watts === null || !Number.isFinite(watts)) return "--";
  return `${watts.toLocaleString(NUMERIC_LOCALE, { minimumFractionDigits: decimals, maximumFractionDigits: decimals })} W`;
}

export function formatKwh(joules: number, decimals = 3): string {
  const kwh = joules / 3_600_000;
  return `${kwh.toLocaleString(NUMERIC_LOCALE, { minimumFractionDigits: decimals, maximumFractionDigits: decimals })} kWh`;
}

export function formatMoney(amount: number | null, currency: string, decimals = 2): string {
  if (amount === null) return "--";
  return amount.toLocaleString(NUMERIC_LOCALE, {
    style: "currency",
    currency,
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  });
}

export function formatDuration(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  const pad = (n: number) => n.toString().padStart(2, "0");
  return `${pad(h)}:${pad(m)}:${pad(s)}`;
}

export function formatLocalDateTime(utcIso: string): string {
  return new Date(utcIso).toLocaleString(NUMERIC_LOCALE, {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  });
}

export function formatPercent(fraction: number, decimals = 0): string {
  return `${(fraction * 100).toLocaleString(NUMERIC_LOCALE, { minimumFractionDigits: decimals, maximumFractionDigits: decimals })}%`;
}
