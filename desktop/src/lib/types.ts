// Mirrors WattScope.Probe's --serve JSON protocol exactly (camelCase, via
// System.Text.Json "Web" defaults) -- see
// src/WattScope.Probe/Serve/ServeProtocol.cs and docs/ARCHITECTURE.md.
// Domain objects (EnergyReport, CostBreakdown, ...) are the C# records
// serialized as-is; keep these in sync with the C# side by hand, since nothing
// here is code-generated.

export type ReadingState =
  | "valid"
  | "missing"
  | "unavailable"
  | "nonFinite"
  | "negative"
  | "aboveMaximum"
  | "implausibleZero"
  | "ambiguous"
  | "notPresent";

export type EstimateCompleteness = "complete" | "partial" | "none";

export type PriceAllocationPrecision = "exact" | "exactReplayed" | "approximateProportional";

export type SessionStatus = "active" | "stopped" | "interrupted";

export interface TickMessage {
  type: "tick";
  sessionId: number;
  sequence: number;
  utc: string;
  cpuWatts: number | null;
  cpuState: ReadingState;
  gpuWatts: number | null;
  gpuState: ReadingState;
  otherComponentsWatts: number;
  estimatedTotalWatts: number | null;
  estimateCompleteness: EstimateCompleteness;
  /** Completeness of the whole session so far; a cost is only ever shown for a complete session. */
  sessionCompleteness: EstimateCompleteness;
  sessionSeconds: number;
  /** Complete estimated energy so far. Never includes sessionPartialKwh. */
  sessionKwh: number;
  /** Partial (lower-bound) estimated energy so far, reported separately. */
  sessionPartialKwh: number;
  sessionCostApprox: number | null;
  currency: string | null;
  elevated: boolean;
  /** The current gaming context (small summary), or null when there is none. Detection itself happens in the sidecar, never in the UI. */
  gaming: GamingLiveState | null;
}

export interface StatusMessage {
  type: "status";
  state: "idle" | "recording" | "recovered" | "suspended" | "resumed" | "stopped" | "exiting" | "restarting" | "failed" | "error";
  sessionId?: number | null;
  elevated?: boolean | null;
  cpuAvailable?: boolean | null;
  gpuAvailable?: boolean | null;
  message?: string | null;
}

/** The sidecar *process*, as supervised by the host (distinct from whether monitoring is recording). */
export type BackendPhase = "starting" | "running" | "restarting" | "failed";

/** Why the host stopped retrying; localized by the UI (the host's message is technical detail only). */
export type BackendFailureReason = "recorderLockHeld" | "databaseError" | "tooManyRestarts" | "spawnFailed";

export interface BackendState {
  state: BackendPhase;
  reason?: BackendFailureReason | null;
  message?: string | null;
}

export interface HostInfo {
  appVersion: string;
  tauriVersion: string;
  webviewVersion: string | null;
  backend: BackendState;
}

export interface AppState {
  firstRunCompleted: boolean;
  appVersion: string | null;
  schemaVersion: number;
}

export interface PriceInfo {
  /** The schedule's currency, or null while no price exists yet. */
  currency: string | null;
  defaultCurrency: string;
  current: ElectricityPrice | null;
  entries: ElectricityPrice[];
  maxPricePerKwh: number;
  maxDecimals: number;
}

export interface PricePreview {
  effectiveFromUtc: string;
  affectsRecordedEnergy: boolean;
  affectedSessions: number;
  affectedMonitoredSeconds: number;
}

export interface Diagnostics {
  appVersion: string | null;
  runtime: string;
  os: string | null;
  architecture: string | null;
  elevated: boolean;
  databasePath: string;
  schemaVersion: number;
  databaseBytes: number;
  monitoringState: "idle" | "recording";
  monitoringIntervalMs: number;
  sessionId: number | null;
  completeness: EstimateCompleteness | null;
  cpuAvailable: boolean;
  gpuAvailable: boolean;
  providers: { id: string; available: boolean; reason: string | null; requiresElevation: boolean }[] | null;
  sensors: { channel: string; provider: string; hardware: string; sensor: string }[] | null;
}

export interface HistoryDeletionPreview {
  cutoffUtc: string;
  sessions: number;
  energyBuckets: number;
  powerSamples: number;
  gaps: number;
  gamingSessions: number;
  oldestStartedUtc: string | null;
  newestEndedUtc: string | null;
}

export interface MaintenanceResult {
  rawSamplesPruned: number;
  freePagesBefore: number;
  freePagesAfter: number;
  bytesBefore: number;
  bytesAfter: number;
}

export interface EnergyTotals {
  monitoredSeconds: number;
  cpuPackageJoules: number;
  cpuPackageSeconds: number;
  gpuBoardJoules: number;
  gpuBoardSeconds: number;
  estimatedCompleteJoules: number;
  estimatedCompleteSeconds: number;
  estimatedPartialJoules: number;
  estimatedPartialSeconds: number;
  isEmpty: boolean;
}

export interface ElectricityPrice {
  effectiveFromUtc: string;
  pricePerKwh: number;
  currency: string;
}

export interface CostBreakdown {
  currency: string;
  estimatedCost: number | null;
  estimatedCostRounded: number | null;
  pricedJoules: number;
  unpricedJoules: number;
  partialEstimateCost: number | null;
  pricesApplied: ElectricityPrice[];
  precision: PriceAllocationPrecision;
}

export interface EnergyReport {
  title: string;
  periodStartUtc: string;
  periodEndUtc: string;
  totals: EnergyTotals;
  gapCount: number;
  gapDuration: string;
  completeness: EstimateCompleteness;
  cost: CostBreakdown;
  assumptions: string[];
}

export interface DailyEnergy {
  localDate: string;
  totals: EnergyTotals;
  cost: CostBreakdown;
}

export interface EstimationSettings {
  otherComponentsWatts: number;
  psuEfficiency: number;
  includeCpuPackage: boolean;
  includeGpuBoard: boolean;
}

export interface EnergySensorBinding {
  channel: "cpuPackage" | "gpuBoard";
  providerId: string;
  sensorId: string;
  hardwareId: string;
  hardwareName: string;
  sensorName: string;
}

export interface MonitoringSession {
  id: number;
  startedUtc: string;
  endedUtc: string | null;
  lastObservationUtc: string | null;
  status: SessionStatus;
  endReason: string | null;
  estimation: EstimationSettings;
  sensors: EnergySensorBinding[];
}

export interface SessionListEntry {
  id: number;
  startedUtc: string;
  endedUtc: string | null;
  status: SessionStatus;
  endReason: string | null;
  totals: EnergyTotals;
}

export interface StorageStatistics {
  schemaVersion: number;
  databaseBytes: number;
  walBytes: number;
  freeBytes: number;
  sessions: number;
  powerSamples: number;
  energyBuckets: number;
  priceSplitRows: number;
  gaps: number;
  availabilityRows: number;
  prices: number;
  gamingSessions: number;
  gameMappings: number;
  rawRetentionDays: number;
  oldestSampleUtc: string | null;
  newestSampleUtc: string | null;
}

export interface SummaryResponse {
  report: EnergyReport;
  daily?: DailyEnergy[];
  session?: MonitoringSession;
}

/** What the sidecar's MonitoringController reports; lets a (re)opened UI attach to the running session. */
export interface MonitoringSnapshot {
  state: "idle" | "recording";
  sessionId: number | null;
  elevated: boolean;
  cpuAvailable: boolean;
  gpuAvailable: boolean;
  intervalMs: number;
  lastTick: TickMessage | null;
  alreadyRunning: boolean;
}

// ---------------------------------------------------------------- gaming (Milestone 005)

export type GamingPhase = "candidate" | "active" | "ending";
export type GamingCostStatus = "priced" | "withheldPartial" | "withheldUnpriced" | "noEstimate";
export type GameDetectionSource = "userMapping" | "knownCatalog" | "launcherChild" | "libraryPath" | "manual";
export type GamingSessionStatus = "active" | "ended" | "interrupted";
export type GamingEndReason = "processExited" | "manualStop" | "monitoringStopped" | "interrupted" | "excluded";
export type GameMappingKind = "game" | "excluded";

export interface GamingLiveState {
  state: GamingPhase;
  sessionId: number | null;
  gameName: string;
  exeName: string;
  isManual: boolean;
  source: GameDetectionSource;
  startedUtc: string;
  durationSeconds: number;
  monitoredSeconds: number;
  completeness: EstimateCompleteness;
  /** Estimated system energy so far; a lower bound when systemIsLowerBound. */
  systemKwh: number;
  systemIsLowerBound: boolean;
  costStatus: GamingCostStatus;
  cost: number | null;
  currency: string | null;
}

export interface GamingSessionSummary {
  id: number;
  monitoringSessionId: number;
  gameKey: string;
  exeName: string;
  displayName: string;
  source: GameDetectionSource;
  isManual: boolean;
  startedUtc: string;
  endedUtc: string | null;
  status: GamingSessionStatus;
  endReason: GamingEndReason | null;
  durationSeconds: number;
  monitoredSeconds: number;
  totals: EnergyTotals;
  completeness: EstimateCompleteness;
  avgCpuWatts: number | null;
  avgGpuWatts: number | null;
  avgSystemWatts: number | null;
  peakCpuWatts: number | null;
  peakGpuWatts: number | null;
  peakSystemWatts: number | null;
  systemJoules: number;
  systemIsLowerBound: boolean;
  costStatus: GamingCostStatus;
  cost: number | null;
  lowerBoundCost: number | null;
  currency: string | null;
  costPrecision: PriceAllocationPrecision;
}

export interface GamingSessionDetail {
  summary: GamingSessionSummary;
  exePath: string | null;
  lastObservationUtc: string | null;
  gaps: { count: number; seconds: number; items: { reason: string; startUtc: string; endUtc: string; seconds: number }[] };
  cost: CostBreakdown;
}

export interface GameAggregateGroup {
  sessions: number;
  monitoredSeconds: number;
  systemJoules: number;
  avgSystemWatts: number | null;
  cost: number | null;
  unpricedSessions: number;
}

export interface GameAggregate {
  gameKey: string;
  displayName: string;
  sessionCount: number;
  totalDurationSeconds: number;
  totalMonitoredSeconds: number;
  averageSessionDurationSeconds: number;
  avgCpuWatts: number | null;
  avgGpuWatts: number | null;
  peakCpuWatts: number | null;
  peakGpuWatts: number | null;
  complete: GameAggregateGroup;
  partial: GameAggregateGroup;
  noEstimateSessions: number;
  currency: string | null;
}

export interface GameMapping {
  id: number;
  exeName: string;
  exePath: string;
  kind: GameMappingKind;
  displayName: string | null;
  updatedUtc: string;
}

export interface GamingStateResponse {
  available: boolean;
  monitoring: boolean;
  live: GamingLiveState | null;
  pollIntervalSeconds: number | null;
}

export interface RunningExecutable {
  exeName: string;
  exePath: string | null;
  instances: number;
}