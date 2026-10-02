// Input validation for every user-editable setting: one table of ranges, one
// parser. The ranges mirror what the backend enforces (the backend is still the
// authority and re-validates); validating here only gives an immediate, specific
// message instead of a round trip. Parsing is locale-tolerant ("0,30" and "0.30")
// but strict about everything else, and the *canonical* text it returns is what
// is sent over the wire -- money never goes through a JavaScript float.

export interface NumberSpec {
  min: number;
  max: number;
  integer?: boolean;
  maxDecimals?: number;
}

export type ValidationError = "empty" | "notNumber" | "notInteger" | "belowMin" | "aboveMax" | "tooManyDecimals";

export type ParseResult =
  | { ok: true; value: number; canonical: string }
  | { ok: false; error: ValidationError };

/** Ranges of every editable setting. Keep in sync with the backend (AppHandlers, EstimationSettings, SqliteEnergyStore). */
export const SPECS = {
  /** Electricity price per kWh; the backend refuses above 100 and more than 6 decimals. */
  price: { min: 0, max: 100, maxDecimals: 6 },
  /** Assumed draw of everything not measured (EstimationSettings.Validate: 0..2000 W). */
  otherWatts: { min: 0, max: 2000, maxDecimals: 1 },
  /** PSU efficiency in percent. The backend allows (0, 100]; below 50 % is not a real PSU, so the UI is stricter. */
  psuPercent: { min: 50, max: 100, maxDecimals: 1 },
  /** Sampling interval in ms (SqliteEnergyStore: 250..60000). */
  intervalMs: { min: 250, max: 60_000, integer: true },
  /** Raw-sample retention in days (0..3650). */
  retentionDays: { min: 0, max: 3650, integer: true },
  /** Delete finished history older than this many days (the backend refuses under 30). */
  historyDays: { min: 30, max: 3650, integer: true },
} as const satisfies Record<string, NumberSpec>;

export type SpecName = keyof typeof SPECS;

const NUMBER_RE = /^(\d+([.,]\d*)?|[.,]\d+)$/;

export function parseNumber(text: string, spec: NumberSpec): ParseResult {
  const trimmed = text.trim();
  if (trimmed === "") return { ok: false, error: "empty" };
  if (!NUMBER_RE.test(trimmed)) return { ok: false, error: "notNumber" };
  const canonical = trimmed.replace(",", ".").replace(/^\./, "0.").replace(/\.$/, "");
  const [, fraction = ""] = canonical.split(".");
  const value = Number(canonical);
  if (!Number.isFinite(value)) return { ok: false, error: "notNumber" };
  if (spec.integer && !Number.isInteger(value)) return { ok: false, error: "notInteger" };
  if (spec.maxDecimals !== undefined && fraction.replace(/0+$/, "").length > spec.maxDecimals) return { ok: false, error: "tooManyDecimals" };
  if (value < spec.min) return { ok: false, error: "belowMin" };
  if (value > spec.max) return { ok: false, error: "aboveMax" };
  return { ok: true, value, canonical };
}

export function parseSetting(text: string, name: SpecName): ParseResult {
  return parseNumber(text, SPECS[name]);
}

/** Localization key of the message for an error ("validation.aboveMax" -> "Enter a value of at most {{max}}."). */
export function validationKey(error: ValidationError): string {
  return `validation.${error}`;
}

/** Interpolation values for the message (range and decimals), so every language states the real limits. */
export function validationParams(name: SpecName): { min: number; max: number; decimals: number } {
  const spec: NumberSpec = SPECS[name];
  return { min: spec.min, max: spec.max, decimals: spec.maxDecimals ?? 0 };
}

/** A 3-letter ISO 4217-shaped currency code (the backend checks the same shape). */
export function isCurrencyCode(text: string): boolean {
  return /^[A-Z]{3}$/.test(text);
}
