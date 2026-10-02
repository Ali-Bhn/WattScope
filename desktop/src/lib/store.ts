import { applyBackend, applyStatus, deriveQuality, type MonitoringQuality, type MonitoringView } from "@/lib/monitoring";
import type { BackendState, MonitoringSnapshot, StatusMessage, TickMessage } from "@/lib/types";

/**
 * The one place the window listens to the host and the sidecar. Before this,
 * every page created its own subscriptions and re-rendered on every tick; now
 * there is exactly one listener per event for the whole window, pages read
 * *slices* of the state (so a page that only needs "is it elevated?" never
 * re-renders per sample), and the live-chart history lives here -- bounded, and
 * surviving navigation between pages.
 *
 * It does not *own* monitoring: the sidecar does, and keeps running while the
 * window is hidden. The store only attaches to it (see `attach`).
 */

export interface SidecarRequest {
  cmd: string;
  [key: string]: unknown;
}

/** The side-effecting edges, injected so the store is testable without Tauri. */
export interface StoreIO {
  request<T>(request: SidecarRequest): Promise<T>;
  /** Subscribes to a host event; returns an unsubscribe function. */
  listen<T>(event: string, handler: (payload: T) => void): () => void;
  getBackendState(): Promise<BackendState>;
  /** The webview's own visibility (document.hidden), if observable. */
  onDocumentVisibility?(handler: (visible: boolean) => void): () => void;
}

/** One chart point. A point of all nulls is a deliberate break in the line (the window was hidden, or a gap). */
export interface HistoryPoint {
  cpu: number | null;
  gpu: number | null;
  total: number | null;
}

/** 5 minutes at the default 1 s interval; holds proportionally less wall-clock time at a shorter interval. */
export const HISTORY_CAPACITY = 300;

/** A silence this long between two ticks (window hidden, sleep, restart) breaks the chart line instead of joining across it. */
export const HISTORY_GAP_MS = 5000;

export interface MonitoringState {
  view: MonitoringView;
  status: StatusMessage | null;
  snapshot: MonitoringSnapshot | null;
  backend: BackendState;
  /** The host window is shown *and* the document is visible. Polling and drawing stop when false. */
  uiVisible: boolean;
  /** True once the first attach attempt finished, so pages can tell "loading" from "no data". */
  attached: boolean;
}

const BREAK: HistoryPoint = { cpu: null, gpu: null, total: null };

export function createMonitoringStore(io: StoreIO) {
  let state: MonitoringState = {
    view: { recording: false, monitorDown: false, lastTick: null },
    status: null,
    snapshot: null,
    backend: { state: "starting" },
    uiVisible: true,
    attached: false,
  };
  const listeners = new Set<() => void>();
  const tickListeners = new Set<(tick: TickMessage) => void>();
  const history: HistoryPoint[] = [];
  let lastTickMs: number | null = null;
  let hostVisible = true;
  let docVisible = true;
  let unsubscribers: (() => void)[] = [];
  let started = false;

  const set = (next: Partial<MonitoringState>) => {
    state = { ...state, ...next };
    for (const l of listeners) l();
  };

  const pushHistory = (tick: TickMessage) => {
    const ms = Date.parse(tick.utc);
    if (lastTickMs !== null && Number.isFinite(ms) && ms - lastTickMs > HISTORY_GAP_MS) history.push(BREAK);
    lastTickMs = Number.isFinite(ms) ? ms : lastTickMs;
    history.push({ cpu: tick.cpuWatts, gpu: tick.gpuWatts, total: tick.estimatedTotalWatts });
    if (history.length > HISTORY_CAPACITY) history.splice(0, history.length - HISTORY_CAPACITY);
  };

  const resetHistory = () => {
    history.length = 0;
    lastTickMs = null;
  };

  const recomputeVisibility = () => {
    const visible = hostVisible && docVisible;
    if (visible === state.uiVisible) return;
    set({ uiVisible: visible });
    if (visible) void attach();
  };

  /** Asks the sidecar what is happening now (the host forwards no ticks while hidden, so state may be stale). */
  async function attach(): Promise<void> {
    try {
      const snapshot = await io.request<MonitoringSnapshot>({ cmd: "get_monitoring_state" });
      set({
        snapshot,
        view: { ...state.view, recording: snapshot.state === "recording", monitorDown: false, lastTick: snapshot.lastTick },
        attached: true,
      });
    } catch {
      // The sidecar is not answering (starting, restarting or gone); the backend state says which.
      set({ attached: true });
    }
  }

  function onTick(tick: TickMessage) {
    pushHistory(tick);
    set({ view: { ...state.view, recording: true, monitorDown: false, lastTick: tick } });
    for (const l of tickListeners) l(tick);
  }

  function onStatus(status: StatusMessage) {
    if (status.state === "recording" || status.state === "stopped" || status.state === "idle") resetHistory();
    set({ status, view: applyStatus(state.view, status) });
    if (status.state === "recording" || status.state === "idle") void attach();
  }

  function onBackend(backend: BackendState) {
    set({ backend, view: applyBackend(state.view, backend) });
    if (backend.state === "running") void attach();
  }

  return {
    getState: () => state,
    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    /** Per-tick callback outside React (the canvas chart): no state, no re-render. */
    subscribeTicks(listener: (tick: TickMessage) => void) {
      tickListeners.add(listener);
      return () => tickListeners.delete(listener);
    },
    getHistory: (): readonly HistoryPoint[] => history,

    /** Starts listening. Idempotent; returns the matching stop function. */
    start(): () => void {
      if (started) return () => undefined;
      started = true;
      unsubscribers = [
        io.listen<TickMessage>("sidecar-tick", onTick),
        io.listen<StatusMessage>("sidecar-status", onStatus),
        io.listen<BackendState>("backend-state", onBackend),
        io.listen<boolean>("ui-visibility", (visible) => {
          hostVisible = visible;
          recomputeVisibility();
        }),
      ];
      if (io.onDocumentVisibility) {
        unsubscribers.push(
          io.onDocumentVisibility((visible) => {
            docVisible = visible;
            recomputeVisibility();
          }),
        );
      }
      void io.getBackendState().then(onBackend, () => undefined);
      void attach();
      return () => {
        for (const off of unsubscribers) off();
        unsubscribers = [];
        started = false;
      };
    },

    attach,
    async startMonitoring() {
      await io.request({ cmd: "start_monitoring" });
    },
    async stopMonitoring() {
      await io.request({ cmd: "stop_monitoring" });
    },
  };
}

export type MonitoringStore = ReturnType<typeof createMonitoringStore>;

export function qualityOf(state: MonitoringState): MonitoringQuality {
  return deriveQuality(state.view);
}
