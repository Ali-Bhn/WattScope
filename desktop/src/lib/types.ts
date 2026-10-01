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
  sessionKwh: number;
  sessionCostApprox: number | null;
  currency: string | null;
  elevated: boolean;
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