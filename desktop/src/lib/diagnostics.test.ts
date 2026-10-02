import { describe, expect, it } from "vitest";
import { buildDiagnosticsText, redactPath } from "./diagnostics";
import type { Diagnostics, HostInfo } from "./types";

const diag: Diagnostics = {
  appVersion: "0.1.0.0",
  runtime: ".NET 10.0.0",
  os: "Microsoft Windows 11 Pro 10.0.26300",
  architecture: "X64",
  elevated: false,
  databasePath: "C:\\Users\\Jane Doe\\AppData\\Local\\WattScope\\wattscope.db",
  schemaVersion: 4,
  databaseBytes: 123456,
  monitoringState: "recording",
  monitoringIntervalMs: 1000,
  sessionId: 12,
  completeness: "partial",
  cpuAvailable: false,
  gpuAvailable: true,
  providers: [
    { id: "Nvml", available: true, reason: null, requiresElevation: false },
    { id: "LibreHardwareMonitor", available: true, reason: "CPU power needs elevation", requiresElevation: true },
  ],
  sensors: [{ channel: "gpuBoard", provider: "Nvml", hardware: "NVIDIA GeForce RTX 5070 Ti", sensor: "GPU Board Power" }],
};

const host: HostInfo = { appVersion: "0.1.0", tauriVersion: "2.1.0", webviewVersion: "130.0.2849.80", backend: { state: "running" } };

describe("path redaction", () => {
  it("replaces the profile folder in the usual database location with %LOCALAPPDATA%", () => {
    expect(redactPath("C:\\Users\\Jane Doe\\AppData\\Local\\WattScope\\wattscope.db")).toBe("%LOCALAPPDATA%\\WattScope\\wattscope.db");
    expect(redactPath("D:\\Users\\jane\\appdata\\local\\WattScope\\x.db")).toBe("%LOCALAPPDATA%\\WattScope\\x.db");
  });

  it("replaces any other path under a user profile with %USERPROFILE%", () => {
    expect(redactPath("C:\\Users\\Jane Doe\\Documents\\scratch.db")).toBe("%USERPROFILE%\\Documents\\scratch.db");
  });

  it("leaves a path outside any user profile alone", () => {
    expect(redactPath("D:\\data\\wattscope.db")).toBe("D:\\data\\wattscope.db");
    expect(redactPath("C:\\ProgramData\\WattScope\\x.db")).toBe("C:\\ProgramData\\WattScope\\x.db");
  });
});

describe("diagnostic text", () => {
  const text = buildDiagnosticsText(diag, host, "2026-10-02T10:00:00.000Z");

  it("contains the facts a bug report needs", () => {
    expect(text).toContain("WattScope version: 0.1.0");
    expect(text).toContain("Database schema version: 4");
    expect(text).toContain("WebView2: 130.0.2849.80");
    expect(text).toContain(".NET runtime: .NET 10.0.0");
    expect(text).toContain("Monitoring: recording");
    expect(text).toContain("Estimate completeness: partial");
    expect(text).toContain("CPU package sensor available: no");
    expect(text).toContain("GPU board sensor available: yes");
    expect(text).toContain("Background monitor: running");
    expect(text).toContain("gpuBoard: NVIDIA GeForce RTX 5070 Ti / GPU Board Power [Nvml]");
    expect(text).toContain("LibreHardwareMonitor: available (needs Administrator) -- CPU power needs elevation");
    expect(text).toContain("Generated (UTC): 2026-10-02T10:00:00.000Z");
  });

  it("does not carry the user's account name (the profile folder is redacted)", () => {
    expect(text).not.toContain("Jane");
    expect(text).toContain("%LOCALAPPDATA%\\WattScope\\wattscope.db");
  });

  it("does not dump anything beyond the allowed facts: it is short and has no database contents", () => {
    expect(text.split("\n").length).toBeLessThan(40);
    expect(text.length).toBeLessThan(2500);
  });

  it("says n/a rather than printing 'null' or 'undefined' for unknown values", () => {
    const sparse = buildDiagnosticsText({ ...diag, appVersion: null, os: null, architecture: null, completeness: null, providers: null, sensors: null }, null, "x");
    expect(sparse).not.toMatch(/null|undefined/);
    expect(sparse).toContain("n/a");
    expect(sparse).toContain("(none reported)");
    expect(sparse).toContain("(none selected)");
  });

  it("includes the reason code when the background monitor has failed", () => {
    const failed = buildDiagnosticsText(diag, { ...host, backend: { state: "failed", reason: "databaseError" } }, "x");
    expect(failed).toContain("Background monitor: failed (databaseError)");
  });
});
