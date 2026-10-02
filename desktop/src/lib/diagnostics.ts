import type { Diagnostics, HostInfo } from "@/lib/types";

/**
 * The text a user can copy into a bug report. It contains versions, the monitoring state, provider
 * availability and the two selected energy sensors -- and nothing else about the machine: no machine or user
 * name (the profile folder in the database path is replaced by an environment-variable form), no serial
 * numbers, no full sensor inventory, no database contents.
 */

/** Replaces the user's profile folder in a Windows path, so the copied text does not carry their account name. */
export function redactPath(path: string): string {
  const localAppData = /^[A-Za-z]:\\Users\\[^\\]+\\AppData\\Local(\\|$)/i;
  if (localAppData.test(path)) return path.replace(localAppData, (_m, sep: string) => `%LOCALAPPDATA%${sep}`);
  const profile = /^[A-Za-z]:\\Users\\[^\\]+(\\|$)/i;
  if (profile.test(path)) return path.replace(profile, (_m, sep: string) => `%USERPROFILE%${sep}`);
  return path;
}

const yesNo = (value: boolean) => (value ? "yes" : "no");

export function buildDiagnosticsText(diag: Diagnostics, host: HostInfo | null, nowIso: string = new Date().toISOString()): string {
  const lines: string[] = [];
  const add = (label: string, value: string | number | null | undefined) => lines.push(`${label}: ${value === null || value === undefined || value === "" ? "n/a" : value}`);

  lines.push("WattScope diagnostics");
  add("Generated (UTC)", nowIso);
  add("WattScope version", host?.appVersion ?? diag.appVersion);
  add("Monitor (sidecar) version", diag.appVersion);
  add("Tauri", host?.tauriVersion);
  add("WebView2", host?.webviewVersion);
  add(".NET runtime", diag.runtime);
  add("Windows", diag.os);
  add("Architecture", diag.architecture);
  add("Running as Administrator", yesNo(diag.elevated));
  lines.push("");
  add("Background monitor", host ? `${host.backend.state}${host.backend.reason ? ` (${host.backend.reason})` : ""}` : null);
  add("Monitoring", diag.monitoringState);
  add("Sampling interval (ms)", diag.monitoringIntervalMs);
  add("Estimate completeness", diag.completeness);
  add("CPU package sensor available", yesNo(diag.cpuAvailable));
  add("GPU board sensor available", yesNo(diag.gpuAvailable));
  lines.push("");
  add("Database schema version", diag.schemaVersion);
  add("Database size (bytes, incl. WAL)", diag.databaseBytes);
  add("Database location", redactPath(diag.databasePath));
  lines.push("");
  lines.push("Sensor providers:");
  for (const p of diag.providers ?? []) {
    lines.push(`  - ${p.id}: ${p.available ? "available" : "unavailable"}${p.requiresElevation ? " (needs Administrator)" : ""}${p.reason ? ` -- ${p.reason}` : ""}`);
  }
  if (!diag.providers || diag.providers.length === 0) lines.push("  (none reported)");
  lines.push("Selected energy sensors:");
  for (const s of diag.sensors ?? []) lines.push(`  - ${s.channel}: ${s.hardware} / ${s.sensor} [${s.provider}]`);
  if (!diag.sensors || diag.sensors.length === 0) lines.push("  (none selected)");
  return lines.join("\n");
}
