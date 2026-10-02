// Development-only stand-in for the Tauri host and the sidecar, so the UI can be rendered in a plain
// browser (for example headless Edge screenshots at several window sizes, in each language). It is loaded
// only by `vite build --mode mock` / `vite --mode mock` (see main.tsx); a normal production build contains
// none of it. Everything here is synthetic -- it never reads a real database and never talks to hardware.
//
// URL parameters:  ?s=<scenario>&lang=<en|de|fa>&view=<dashboard|gaming|reports|settings>
// Scenarios: complete | partial | stopped | failed | restarting | empty | firstrun

import { emit } from "@tauri-apps/api/event";
import { mockIPC } from "@tauri-apps/api/mocks";
import type { EnergyTotals } from "@/lib/types";

const params = new URLSearchParams(location.search);
const scenario = params.get("s") ?? "complete";
const view = params.get("view");

const J = 3_600_000;
const totals = (over: Partial<EnergyTotals>): EnergyTotals => ({
  monitoredSeconds: 0,
  cpuPackageJoules: 0,
  cpuPackageSeconds: 0,
  gpuBoardJoules: 0,
  gpuBoardSeconds: 0,
  estimatedCompleteJoules: 0,
  estimatedCompleteSeconds: 0,
  estimatedPartialJoules: 0,
  estimatedPartialSeconds: 0,
  isEmpty: true,
  ...over,
});

const completeTotals = totals({ monitoredSeconds: 21_600, cpuPackageJoules: 1.1 * J, cpuPackageSeconds: 21_600, gpuBoardJoules: 0.9 * J, gpuBoardSeconds: 21_600, estimatedCompleteJoules: 2.4 * J, estimatedCompleteSeconds: 21_600, isEmpty: false });
const mixedTotals = totals({
  monitoredSeconds: 36_000,
  cpuPackageJoules: 1.1 * J,
  cpuPackageSeconds: 21_600,
  gpuBoardJoules: 1.6 * J,
  gpuBoardSeconds: 36_000,
  estimatedCompleteJoules: 2.4 * J,
  estimatedCompleteSeconds: 21_600,
  estimatedPartialJoules: 1.4 * J,
  estimatedPartialSeconds: 14_400,
  isEmpty: false,
});
const partialTotals = totals({ monitoredSeconds: 7_200, gpuBoardJoules: 0.3 * J, gpuBoardSeconds: 7_200, estimatedPartialJoules: 0.5 * J, estimatedPartialSeconds: 7_200, isEmpty: false });

const cost = (amount: number | null, extra: Record<string, unknown> = {}) => ({
  currency: "EUR",
  estimatedCost: amount,
  estimatedCostRounded: amount,
  pricedJoules: 0,
  unpricedJoules: 0,
  partialEstimateCost: null,
  pricesApplied: [],
  precision: "exact",
  ...extra,
});

const report = (title: string, t: EnergyTotals, c = cost(0.72)) => ({
  title,
  periodStartUtc: new Date().toISOString(),
  periodEndUtc: new Date().toISOString(),
  totals: t,
  gapCount: 2,
  gapDuration: "00:03:12",
  completeness: "complete",
  cost: c,
  assumptions: ["Estimated wall power = (measured CPU package power + measured GPU board power + other components) / PSU efficiency.", "Other components: 50 W (assumed constant, not measured).", "PSU efficiency: 90 % (assumed constant, not measured).", "This is an estimate, not a measurement of wall power."],
});

const empty = scenario === "empty" || scenario === "firstrun";
const recording = scenario === "complete" || scenario === "partial";
const partial = scenario === "partial";

let seq = 0;
const tickAt = (offsetSeconds: number) => {
  const wobble = Math.sin((seq + offsetSeconds) / 7) * 18 + Math.random() * 6;
  const cpu = partial ? null : 62 + wobble;
  const gpu = 118 + wobble * 2;
  const total = ((cpu ?? 0) + gpu + 50) / 0.9;
  const live = scenario === "complete" || scenario === "partial";
  seq++;
  return {
    type: "tick",
    sessionId: 8,
    sequence: seq,
    utc: new Date(Date.now() + offsetSeconds * 1000).toISOString(),
    cpuWatts: cpu,
    cpuState: partial ? "implausibleZero" : "valid",
    gpuWatts: gpu,
    gpuState: "valid",
    otherComponentsWatts: 50,
    estimatedTotalWatts: total,
    estimateCompleteness: partial ? "partial" : "complete",
    sessionCompleteness: partial ? "partial" : "complete",
    sessionSeconds: 3_725 + seq,
    sessionKwh: partial ? 0 : 0.412,
    sessionPartialKwh: partial ? 0.298 : 0,
    sessionCostApprox: partial ? null : 0.1236,
    currency: "EUR",
    elevated: !partial,
    gaming: live
      ? {
          state: "active",
          sessionId: 3,
          gameName: scenario === "partial" ? "Call of Duty: Warzone" : "Elden Ring",
          exeName: "game.exe",
          isManual: false,
          source: "knownCatalog",
          startedUtc: new Date(Date.now() - 3_600_000).toISOString(),
          durationSeconds: 3_600 + seq,
          monitoredSeconds: 3_590 + seq,
          completeness: partial ? "partial" : "complete",
          systemKwh: 0.31,
          systemIsLowerBound: partial,
          costStatus: partial ? "withheldPartial" : "priced",
          cost: partial ? null : 0.0931,
          currency: "EUR",
        }
      : null,
  };
};

const snapshot = () => ({
  state: recording ? "recording" : "idle",
  sessionId: recording ? 8 : null,
  elevated: !partial,
  cpuAvailable: true,
  gpuAvailable: true,
  intervalMs: 1000,
  lastTick: recording ? tickAt(0) : null,
  alreadyRunning: false,
});

const gamingSession = (id: number, name: string, isPartial: boolean, minutesAgo: number) => ({
  id,
  monitoringSessionId: 7,
  gameKey: name.toLowerCase().replace(/\W/g, ""),
  exeName: "game.exe",
  displayName: name,
  source: "knownCatalog",
  isManual: false,
  startedUtc: new Date(Date.now() - minutesAgo * 60_000).toISOString(),
  endedUtc: new Date(Date.now() - (minutesAgo - 95) * 60_000).toISOString(),
  status: "ended",
  endReason: "processExited",
  durationSeconds: 5_700,
  monitoredSeconds: 5_640,
  totals: isPartial ? partialTotals : completeTotals,
  completeness: isPartial ? "partial" : "complete",
  avgCpuWatts: isPartial ? null : 71.2,
  avgGpuWatts: 190.4,
  avgSystemWatts: 330.5,
  peakCpuWatts: isPartial ? null : 118,
  peakGpuWatts: 288,
  peakSystemWatts: 470,
  systemJoules: 0.62 * J,
  systemIsLowerBound: isPartial,
  costStatus: isPartial ? "withheldPartial" : "priced",
  cost: isPartial ? null : 0.19,
  lowerBoundCost: isPartial ? 0.11 : null,
  currency: "EUR",
  costPrecision: "exact",
});

const priceEntries = [
  { effectiveFromUtc: "2025-01-01T00:00:00Z", pricePerKwh: 0.2899, currency: "EUR" },
  { effectiveFromUtc: "2026-03-01T00:00:00Z", pricePerKwh: 0.305, currency: "EUR" },
];

const dbInfo = {
  schemaVersion: 4,
  databaseBytes: 18_350_080,
  walBytes: 4_120_000,
  freeBytes: 0,
  sessions: 42,
  powerSamples: 604_800,
  energyBuckets: 3_920,
  priceSplitRows: 3_920,
  gaps: 11,
  availabilityRows: 120,
  prices: 2,
  gamingSessions: 17,
  gameMappings: 2,
  rawRetentionDays: 7,
  oldestSampleUtc: new Date(Date.now() - 7 * 86_400_000).toISOString(),
  newestSampleUtc: new Date().toISOString(),
};

function handle(cmd: string, args: Record<string, unknown> | undefined): unknown {
  switch (cmd) {
    case "get_backend_state":
      return scenario === "failed" ? { state: "failed", reason: "tooManyRestarts", message: "The monitor keeps stopping unexpectedly (exit code Some(-1))." } : scenario === "restarting" ? { state: "restarting", message: "restarting in 2 s" } : { state: "running" };
    case "get_host_info":
      return { appVersion: "0.1.0", tauriVersion: "2.1.0", webviewVersion: "130.0.2849.80", backend: handle("get_backend_state", undefined) };
    case "retry_sidecar":
      return { state: "starting" };
    case "set_ui_language":
    case "exit_app":
    case "hide_to_tray":
      return null;
    case "sidecar_request":
      return respond(args?.request as Record<string, unknown>);
    default:
      return null;
  }
}

function respond(request: Record<string, unknown>) {
  const ok = (data: unknown) => ({ type: "response", id: request.id ?? 1, ok: true, data, error: null });
  if (scenario === "failed" || scenario === "restarting") return { type: "response", id: 1, ok: false, data: null, error: "the sidecar is not running" };
  switch (request.cmd) {
    case "get_monitoring_state":
      return ok(snapshot());
    case "get_app_state":
      return ok({ firstRunCompleted: scenario !== "firstrun", appVersion: "0.1.0.0", schemaVersion: 4 });
    case "get_summary":
      if (empty) return ok({ report: report("empty", totals({}), cost(null)) });
      if (request.month) {
        const days = [27, 28, 29, 30, 1, 2].map((d, i) => ({ localDate: `2026-${d > 20 ? "09" : "10"}-${String(d).padStart(2, "0")}`, totals: i === 3 ? mixedTotals : i === 4 ? partialTotals : completeTotals, cost: i === 4 ? cost(null, { partialEstimateCost: 0.15 }) : cost(0.72, i === 2 ? { precision: "approximateProportional" } : {}) }));
        return ok({ report: report("month", mixedTotals, cost(3.14, { partialEstimateCost: 0.43, unpricedJoules: 0.2 * J })), daily: days });
      }
      if (request.sessionId) return ok({ report: report("session", partial ? partialTotals : mixedTotals, partial ? cost(null, { partialEstimateCost: 0.15 }) : cost(0.81, { partialEstimateCost: 0.12 })), session: { endReason: "Stopped by user" } });
      return ok({ report: report("day", mixedTotals, cost(0.81, { partialEstimateCost: 0.12 })) });
    case "get_sessions":
      return ok(
        empty
          ? []
          : [
              { id: 8, startedUtc: new Date(Date.now() - 3_700_000).toISOString(), endedUtc: null, status: recording ? "active" : "stopped", endReason: null, totals: partial ? partialTotals : completeTotals },
              { id: 7, startedUtc: new Date(Date.now() - 86_400_000).toISOString(), endedUtc: new Date().toISOString(), status: "stopped", endReason: "Stopped by user", totals: mixedTotals },
              { id: 6, startedUtc: new Date(Date.now() - 2 * 86_400_000).toISOString(), endedUtc: new Date().toISOString(), status: "interrupted", endReason: "Recovered", totals: partialTotals },
            ],
      );
    case "get_price_info":
      return ok(empty ? { currency: null, defaultCurrency: "EUR", current: null, entries: [], maxPricePerKwh: 100, maxDecimals: 6 } : { currency: "EUR", defaultCurrency: "EUR", current: priceEntries[1], entries: priceEntries, maxPricePerKwh: 100, maxDecimals: 6 });
    case "preview_price":
      return ok({ effectiveFromUtc: request.effectiveFrom, affectsRecordedEnergy: true, affectedSessions: 3, affectedMonitoredSeconds: 93_000 });
    case "get_estimation":
      return ok({ otherComponentsWatts: 50, psuEfficiency: 0.9, includeCpuPackage: true, includeGpuBoard: true });
    case "get_monitoring_interval":
      return ok({ intervalMs: 1000 });
    case "get_retention":
      return ok({ days: 7 });
    case "get_db_info":
      return ok(empty ? { ...dbInfo, sessions: 0, powerSamples: 0, energyBuckets: 0, gamingSessions: 0, oldestSampleUtc: null, newestSampleUtc: null } : dbInfo);
    case "preview_history_deletion":
      return ok({ cutoffUtc: new Date().toISOString(), sessions: 12, energyBuckets: 960, powerSamples: 0, gaps: 3, gamingSessions: 5, oldestStartedUtc: new Date(Date.now() - 400 * 86_400_000).toISOString(), newestEndedUtc: new Date(Date.now() - 370 * 86_400_000).toISOString() });
    case "get_diagnostics":
      return ok({
        appVersion: "0.1.0.0",
        runtime: ".NET 10.0.0",
        os: "Microsoft Windows 11 Pro 10.0.26300",
        architecture: "X64",
        elevated: !partial,
        databasePath: "C:\\Users\\Someone\\AppData\\Local\\WattScope\\wattscope.db",
        schemaVersion: 4,
        databaseBytes: 22_470_080,
        monitoringState: recording ? "recording" : "idle",
        monitoringIntervalMs: 1000,
        sessionId: recording ? 8 : null,
        completeness: partial ? "partial" : "complete",
        cpuAvailable: !partial,
        gpuAvailable: true,
        providers: [
          { id: "LibreHardwareMonitor", available: true, reason: null, requiresElevation: true },
          { id: "Nvml", available: true, reason: null, requiresElevation: false },
        ],
        sensors: [
          { channel: "cpuPackage", provider: "LibreHardwareMonitor", hardware: "Intel Core i9-14900KF", sensor: "CPU Package" },
          { channel: "gpuBoard", provider: "Nvml", hardware: "NVIDIA GeForce RTX 5070 Ti", sensor: "GPU Board Power" },
        ],
      });
    case "get_gaming_state":
      return ok({ available: true, monitoring: recording, live: null, pollIntervalSeconds: 5 });
    case "get_gaming_sessions":
      return ok(empty ? [] : [gamingSession(3, "Elden Ring", false, 240), gamingSession(2, "Call of Duty: Warzone", true, 1500), gamingSession(1, "بازی نمونه", false, 3000)]);
    case "get_gaming_session":
      return ok({ summary: gamingSession(Number(request.gamingSessionId) || 3, "Elden Ring", false, 240), exePath: "C:\\Games\\EldenRing\\eldenring.exe", lastObservationUtc: null, gaps: { count: 1, seconds: 60, items: [] }, cost: cost(0.19) });
    case "get_game_aggregates":
      return ok(
        empty
          ? []
          : [
              { gameKey: "eldenring", displayName: "Elden Ring", sessionCount: 5, totalDurationSeconds: 28_500, totalMonitoredSeconds: 28_200, averageSessionDurationSeconds: 5_700, avgCpuWatts: 70, avgGpuWatts: 190, peakCpuWatts: 118, peakGpuWatts: 288, complete: { sessions: 4, monitoredSeconds: 22_560, systemJoules: 2.1 * J, avgSystemWatts: 335, cost: 0.64, unpricedSessions: 0 }, partial: { sessions: 1, monitoredSeconds: 5_640, systemJoules: 0.3 * J, avgSystemWatts: 190, cost: null, unpricedSessions: 0 }, noEstimateSessions: 0, currency: "EUR" },
            ],
      );
    case "get_game_mappings":
      return ok(empty ? [] : [{ id: 1, exeName: "mygame.exe", exePath: "", kind: "game", displayName: "My Game", updatedUtc: new Date().toISOString() }, { id: 2, exeName: "launcher.exe", exePath: "D:\\Apps\\launcher.exe", kind: "excluded", displayName: null, updatedUtc: new Date().toISOString() }]);
    case "get_running_executables":
      return ok([{ exeName: "eldenring.exe", exePath: null, instances: 1 }]);
    default:
      return ok({});
  }
}

export async function installMock() {
  mockIPC((cmd, payload) => handle(cmd, payload as Record<string, unknown> | undefined), { shouldMockEvents: true });

  // Drive the same events the real host would: a backfilled history, then one tick per second while recording.
  window.setTimeout(async () => {
    if (recording) {
      for (let i = -150; i < 0; i++) await emit("sidecar-tick", tickAt(i));
      await emit("sidecar-status", { type: "status", state: "recording" });
      for (let i = -150; i < 0; i++) await emit("sidecar-tick", tickAt(i));
      window.setInterval(() => void emit("sidecar-tick", tickAt(0)), 1000);
    }
    if (view) await emit("navigate", view);
  }, 400);
}
