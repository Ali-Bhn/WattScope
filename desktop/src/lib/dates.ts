// Local-calendar helpers. Persisted data is UTC; the user's days and months are
// *local*. Reports are requested by local day/month key ("YYYY-MM-DD" /
// "YYYY-MM") and the backend converts the key to the exact UTC range with the
// machine's time zone (DST-aware) -- the UI never does that arithmetic itself.
// The one trap this file exists for: `toISOString().slice(0, 7)` is the *UTC*
// month, which is wrong for a few hours around every month boundary.

const pad = (n: number, width = 2) => String(n).padStart(width, "0");

export function localDayKey(date: Date = new Date()): string {
  return `${pad(date.getFullYear(), 4)}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

export function localMonthKey(date: Date = new Date()): string {
  return `${pad(date.getFullYear(), 4)}-${pad(date.getMonth() + 1)}`;
}

const DAY_RE = /^(\d{4})-(\d{2})-(\d{2})$/;
const MONTH_RE = /^(\d{4})-(\d{2})$/;

/** A valid, existing calendar day ("2026-02-30" is not). */
export function isValidDayKey(key: string): boolean {
  const m = DAY_RE.exec(key);
  if (!m) return false;
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])];
  const date = new Date(y, mo - 1, d);
  return date.getFullYear() === y && date.getMonth() === mo - 1 && date.getDate() === d;
}

export function isValidMonthKey(key: string): boolean {
  const m = MONTH_RE.exec(key);
  return !!m && Number(m[2]) >= 1 && Number(m[2]) <= 12;
}

/** Moves a day key by whole calendar days (not 24 h steps, so DST days are handled). */
export function shiftDayKey(key: string, deltaDays: number): string {
  const m = DAY_RE.exec(key);
  if (!m) return key;
  return localDayKey(new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]) + deltaDays));
}

export function shiftMonthKey(key: string, deltaMonths: number): string {
  const m = MONTH_RE.exec(key);
  if (!m) return key;
  return localMonthKey(new Date(Number(m[1]), Number(m[2]) - 1 + deltaMonths, 1));
}

export function isFutureDayKey(key: string, now: Date = new Date()): boolean {
  return key > localDayKey(now);
}

export function isFutureMonthKey(key: string, now: Date = new Date()): boolean {
  return key > localMonthKey(now);
}

/** "2026-10-02T14:30" from a <input type="datetime-local"> (local wall-clock time) to a UTC ISO instant; null when invalid. */
export function localInputToUtcIso(value: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/.test(value)) return null;
  const date = new Date(value); // a date-time without an offset is parsed as local time
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

/** The value a <input type="datetime-local"> expects for a date, in local time (minute precision). */
export function toLocalInputValue(date: Date): string {
  return `${localDayKey(date)}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}
