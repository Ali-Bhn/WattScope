// What the user is told when something fails. Raw messages from the host and
// the sidecar are technical (and English); they are kept for a collapsed
// "Technical details" line, while the primary text is a localized message chosen
// by the *kind* of failure. Stack traces never reach the UI.

export type ErrorKind =
  | "backendUnavailable" // the sidecar is starting, restarting or gone
  | "timeout" // the sidecar did not answer in time
  | "database" // the database could not be read or written
  | "validation" // the request was understood and refused (bad input)
  | "internal"; // anything else

export interface DescribedError {
  kind: ErrorKind;
  /** The original technical message, for details/diagnostics. */
  detail: string;
}

export function errorMessage(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (typeof error === "string") return error;
  try {
    return JSON.stringify(error) ?? String(error); // JSON.stringify(undefined) is undefined, not a string
  } catch {
    return String(error);
  }
}

export function describeError(error: unknown): DescribedError {
  const detail = errorMessage(error).replace(/^Error:\s*/, "");
  const lower = detail.toLowerCase();

  if (lower.includes("sidecar is not running") || lower.includes("closed before responding") || lower.includes("sidecar state poisoned")) {
    return { kind: "backendUnavailable", detail };
  }
  if (lower.includes("did not respond in time")) return { kind: "timeout", detail };
  if (lower.includes("database could not") || lower.includes("database is locked") || lower.includes("sqlite") || lower.includes("disk i/o") || lower.includes("malformed")) {
    return { kind: "database", detail };
  }
  if (lower.startsWith("internal error")) return { kind: "internal", detail };
  // Requests the sidecar understood and refused (ArgumentException messages, "... is required", "... does not exist").
  return { kind: "validation", detail };
}

export const ERROR_KEYS: Record<ErrorKind, { title: string; hint: string }> = {
  backendUnavailable: { title: "errors.backendUnavailable.title", hint: "errors.backendUnavailable.hint" },
  timeout: { title: "errors.timeout.title", hint: "errors.timeout.hint" },
  database: { title: "errors.database.title", hint: "errors.database.hint" },
  validation: { title: "errors.validation.title", hint: "errors.validation.hint" },
  internal: { title: "errors.internal.title", hint: "errors.internal.hint" },
};
