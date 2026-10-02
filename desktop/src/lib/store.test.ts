import { describe, expect, it, vi } from "vitest";
import { createMonitoringStore, HISTORY_CAPACITY, HISTORY_GAP_MS, qualityOf, type SidecarRequest, type StoreIO } from "./store";
import type { BackendState, MonitoringSnapshot, StatusMessage, TickMessage } from "./types";

const tick = (over: Partial<TickMessage> = {}): TickMessage => ({
  type: "tick",
  sessionId: 1,
  sequence: 1,
  utc: "2026-01-01T00:00:00.000Z",
  cpuWatts: 60,
  cpuState: "valid",
  gpuWatts: 40,
  gpuState: "valid",
  otherComponentsWatts: 50,
  estimatedTotalWatts: 170,
  estimateCompleteness: "complete",
  sessionCompleteness: "complete",
  sessionSeconds: 10,
  sessionKwh: 0.01,
  sessionPartialKwh: 0,
  sessionCostApprox: 0.003,
  currency: "EUR",
  elevated: true,
  gaming: null,
  ...over,
});

const snapshot = (over: Partial<MonitoringSnapshot> = {}): MonitoringSnapshot => ({
  state: "idle",
  sessionId: null,
  elevated: false,
  cpuAvailable: true,
  gpuAvailable: true,
  intervalMs: 1000,
  lastTick: null,
  alreadyRunning: false,
  ...over,
});

/** A fake host: records subscriptions, lets a test emit events, and answers requests. */
function harness(options: { snapshot?: MonitoringSnapshot | Error; backend?: BackendState } = {}) {
  const handlers = new Map<string, Set<(payload: never) => void>>();
  const requests: SidecarRequest[] = [];
  let docVisibility: ((visible: boolean) => void) | undefined;
  let subscribes = 0;
  let unsubscribes = 0;

  const io: StoreIO = {
    async request<T>(request: SidecarRequest): Promise<T> {
      requests.push(request);
      if (request.cmd === "get_monitoring_state") {
        const s = options.snapshot ?? snapshot();
        if (s instanceof Error) throw s;
        return s as T;
      }
      return {} as T;
    },
    listen<T>(event: string, handler: (payload: T) => void) {
      subscribes++;
      const set = handlers.get(event) ?? new Set();
      set.add(handler as (payload: never) => void);
      handlers.set(event, set);
      return () => {
        unsubscribes++;
        set.delete(handler as (payload: never) => void);
      };
    },
    getBackendState: async () => options.backend ?? { state: "running" },
    onDocumentVisibility(handler) {
      docVisibility = handler;
      return () => {
        docVisibility = undefined;
      };
    },
  };

  const store = createMonitoringStore(io);
  const emit = <T>(event: string, payload: T) => handlers.get(event)?.forEach((h) => (h as (p: T) => void)(payload));
  const flush = () => new Promise((resolve) => setTimeout(resolve, 0));
  return { store, emit, flush, requests, setDocVisible: (v: boolean) => docVisibility?.(v), counts: () => ({ subscribes, unsubscribes }) };
}

const status = (state: StatusMessage["state"]): StatusMessage => ({ type: "status", state });

describe("one set of subscriptions for the whole window", () => {
  it("subscribes exactly once per event, however many times start is called", () => {
    const h = harness();
    const stop = h.store.start();
    h.store.start();
    h.store.start();
    // tick, status, backend-state, ui-visibility
    expect(h.counts().subscribes).toBe(4);
    stop();
    expect(h.counts().unsubscribes).toBe(4);
  });

  it("can be started again after it was stopped (no leaked listeners, no duplicates)", () => {
    const h = harness();
    h.store.start()();
    h.store.start()();
    expect(h.counts().subscribes).toBe(8);
    expect(h.counts().unsubscribes).toBe(8);
  });
});

describe("attaching to the running monitor", () => {
  it("asks the sidecar for its state on start and adopts a running session (a reopened window shows the same session)", async () => {
    const running = tick({ sessionSeconds: 99 });
    const h = harness({ snapshot: snapshot({ state: "recording", sessionId: 7, lastTick: running }) });
    h.store.start();
    await h.flush();
    const s = h.store.getState();
    expect(s.attached).toBe(true);
    expect(s.view.recording).toBe(true);
    expect(s.view.lastTick).toEqual(running);
    expect(s.snapshot?.sessionId).toBe(7);
    expect(qualityOf(s)).toBe("complete");
  });

  it("is 'attached' (so pages stop showing a spinner) even when the sidecar does not answer", async () => {
    const h = harness({ snapshot: new Error("the sidecar is not running") });
    h.store.start();
    await h.flush();
    expect(h.store.getState().attached).toBe(true);
    expect(h.store.getState().view.recording).toBe(false);
  });

  it("re-attaches when the window becomes visible again (no ticks were delivered while hidden)", async () => {
    const h = harness();
    h.store.start();
    await h.flush();
    const before = h.requests.filter((r) => r.cmd === "get_monitoring_state").length;
    h.emit("ui-visibility", false);
    expect(h.store.getState().uiVisible).toBe(false);
    h.emit("ui-visibility", true);
    await h.flush();
    expect(h.store.getState().uiVisible).toBe(true);
    expect(h.requests.filter((r) => r.cmd === "get_monitoring_state").length).toBe(before + 1);
  });

  it("is visible only when both the host window and the document are visible", async () => {
    const h = harness();
    h.store.start();
    h.setDocVisible(false);
    expect(h.store.getState().uiVisible).toBe(false);
    h.emit("ui-visibility", true);
    expect(h.store.getState().uiVisible).toBe(false); // the document is still hidden
    h.setDocVisible(true);
    expect(h.store.getState().uiVisible).toBe(true);
  });
});

describe("events", () => {
  it("a tick marks the monitor up and recording, and is what the quality derives from", () => {
    const h = harness();
    h.store.start();
    h.emit("sidecar-tick", tick({ estimateCompleteness: "partial", sessionCompleteness: "partial", cpuWatts: null, cpuState: "implausibleZero" }));
    expect(qualityOf(h.store.getState())).toBe("partial");
  });

  it("status events drive started / stopped / down", () => {
    const h = harness();
    h.store.start();
    h.emit("sidecar-status", status("recording"));
    expect(qualityOf(h.store.getState())).toBe("starting");
    h.emit("sidecar-tick", tick());
    expect(qualityOf(h.store.getState())).toBe("complete");
    h.emit("sidecar-status", status("stopped"));
    expect(qualityOf(h.store.getState())).toBe("stopped");
    h.emit("sidecar-status", status("restarting"));
    expect(qualityOf(h.store.getState())).toBe("unavailable");
  });

  it("the backend state (starting, running, restarting, failed) is tracked separately from monitoring", async () => {
    const h = harness({ backend: { state: "starting" } });
    h.store.start();
    await h.flush();
    expect(h.store.getState().backend.state).toBe("starting");
    h.emit<BackendState>("backend-state", { state: "running" });
    expect(h.store.getState().backend.state).toBe("running");
    h.emit<BackendState>("backend-state", { state: "restarting", message: "restarting in 2 s" });
    expect(h.store.getState().backend.state).toBe("restarting");
    expect(qualityOf(h.store.getState())).toBe("unavailable");
    h.emit<BackendState>("backend-state", { state: "failed", reason: "tooManyRestarts" });
    expect(h.store.getState().backend.reason).toBe("tooManyRestarts");
    expect(qualityOf(h.store.getState())).toBe("unavailable");
  });

  it("when the backend comes back it re-attaches, so a recovered monitor is shown as it really is", async () => {
    const h = harness({ snapshot: snapshot({ state: "recording", sessionId: 3, lastTick: tick() }) });
    h.store.start();
    await h.flush();
    h.emit<BackendState>("backend-state", { state: "restarting" });
    expect(qualityOf(h.store.getState())).toBe("unavailable");
    h.emit<BackendState>("backend-state", { state: "running" });
    await h.flush();
    expect(qualityOf(h.store.getState())).toBe("complete");
  });

  it("retry after a give-up produces no extra subscriptions or sessions on its own (it is only state)", () => {
    const h = harness();
    h.store.start();
    const before = h.counts().subscribes;
    h.emit<BackendState>("backend-state", { state: "failed", reason: "tooManyRestarts" });
    h.emit<BackendState>("backend-state", { state: "starting" });
    h.emit<BackendState>("backend-state", { state: "running" });
    expect(h.counts().subscribes).toBe(before);
    expect(h.requests.filter((r) => r.cmd === "start_monitoring")).toHaveLength(0);
  });

  it("start and stop go to the sidecar as single requests", async () => {
    const h = harness();
    await h.store.startMonitoring();
    await h.store.stopMonitoring();
    expect(h.requests.map((r) => r.cmd)).toEqual(["start_monitoring", "stop_monitoring"]);
  });
});

describe("notifications", () => {
  it("notifies subscribers on every change and not after unsubscribing", () => {
    const h = harness();
    const listener = vi.fn();
    const off = h.store.subscribe(listener);
    h.store.start();
    h.emit("sidecar-tick", tick());
    expect(listener).toHaveBeenCalled();
    const calls = listener.mock.calls.length;
    off();
    h.emit("sidecar-tick", tick({ sequence: 2 }));
    expect(listener.mock.calls.length).toBe(calls);
  });

  it("delivers ticks to tick listeners (the canvas chart) without needing React state", () => {
    const h = harness();
    h.store.start();
    const seen: number[] = [];
    h.store.subscribeTicks((t) => seen.push(t.sequence));
    h.emit("sidecar-tick", tick({ sequence: 5 }));
    h.emit("sidecar-tick", tick({ sequence: 6 }));
    expect(seen).toEqual([5, 6]);
  });
});

describe("the live-chart history is bounded and honest", () => {
  const at = (i: number) => new Date(Date.UTC(2026, 0, 1, 0, 0, i)).toISOString();

  it("never grows past its capacity, however long monitoring runs", () => {
    const h = harness();
    h.store.start();
    for (let i = 0; i < HISTORY_CAPACITY * 3; i++) h.emit("sidecar-tick", tick({ sequence: i, utc: at(i) }));
    expect(h.store.getHistory().length).toBe(HISTORY_CAPACITY);
  });

  it("keeps the newest points when it drops old ones", () => {
    const h = harness();
    h.store.start();
    for (let i = 0; i < HISTORY_CAPACITY + 10; i++) h.emit("sidecar-tick", tick({ sequence: i, utc: at(i), estimatedTotalWatts: i }));
    const history = h.store.getHistory();
    expect(history[history.length - 1].total).toBe(HISTORY_CAPACITY + 9);
    expect(history[0].total).toBe(10);
  });

  it("records a missing reading as null (a break in the line), never as zero", () => {
    const h = harness();
    h.store.start();
    h.emit("sidecar-tick", tick({ utc: at(0), cpuWatts: null, cpuState: "implausibleZero", estimateCompleteness: "partial" }));
    expect(h.store.getHistory()[0].cpu).toBeNull();
    expect(h.store.getHistory()[0].gpu).toBe(40);
  });

  it("breaks the line across a silence (hidden window, sleep, restart) instead of joining the two sides", () => {
    const h = harness();
    h.store.start();
    h.emit("sidecar-tick", tick({ utc: at(0) }));
    h.emit("sidecar-tick", tick({ utc: at(HISTORY_GAP_MS / 1000 + 30) }));
    const history = h.store.getHistory();
    expect(history).toHaveLength(3);
    expect(history[1]).toEqual({ cpu: null, gpu: null, total: null });
  });

  it("does not insert a break between consecutive ticks", () => {
    const h = harness();
    h.store.start();
    for (let i = 0; i < 5; i++) h.emit("sidecar-tick", tick({ utc: at(i) }));
    expect(h.store.getHistory()).toHaveLength(5);
  });

  it("starts a fresh history for a new run and clears it when monitoring stops", () => {
    const h = harness();
    h.store.start();
    h.emit("sidecar-tick", tick({ utc: at(0) }));
    expect(h.store.getHistory()).toHaveLength(1);
    h.emit("sidecar-status", status("stopped"));
    expect(h.store.getHistory()).toHaveLength(0);
    h.emit("sidecar-tick", tick({ utc: at(1) }));
    h.emit("sidecar-status", status("recording"));
    expect(h.store.getHistory()).toHaveLength(0);
  });
});
