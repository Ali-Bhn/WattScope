import { useCallback, useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { applyStatus, deriveQuality, type MonitoringQuality, type MonitoringView } from "@/lib/monitoring";
import type { MonitoringSnapshot, StatusMessage, TickMessage } from "@/lib/types";

export interface SidecarRequest {
  cmd: string;
  [key: string]: unknown;
}

interface SidecarResponse<T> {
  type: "response";
  id: number | null;
  ok: boolean;
  data: T | null;
  error: string | null;
}

/** Sends one request to the sidecar (via the Rust `sidecar_request` command) and resolves with its typed response. */
export function useSidecarRequest() {
  return useCallback(async <T,>(request: SidecarRequest): Promise<T> => {
    const response = await invoke<SidecarResponse<T>>("sidecar_request", { request });
    if (!response.ok) {
      throw new Error(response.error ?? "The sidecar request failed for an unknown reason.");
    }
    return response.data as T;
  }, []);
}

function subscribe<T>(event: string, handler: (payload: T) => void): () => void {
  let unlisten: UnlistenFn | undefined;
  let cancelled = false;
  listen<T>(event, (e) => handler(e.payload)).then((fn) => {
    if (cancelled) fn();
    else unlisten = fn;
  });
  return () => {
    cancelled = true;
    unlisten?.();
  };
}

export interface MonitoringState {
  quality: MonitoringQuality;
  tick: TickMessage | null;
  status: StatusMessage | null;
  snapshot: MonitoringSnapshot | null;
  start: () => Promise<void>;
  stop: () => Promise<void>;
}

/**
 * The UI's whole view of monitoring. It does not *own* monitoring -- the
 * sidecar does, and keeps it running while the window is hidden or closed
 * to the tray -- so this hook only attaches: it asks the sidecar for the
 * current state on mount and whenever the window becomes visible again
 * (the host stops forwarding ticks while hidden), then follows events.
 * Reopening the dashboard therefore shows the same session, not a new one.
 */
export function useMonitoring(): MonitoringState {
  const request = useSidecarRequest();
  const [view, setView] = useState<MonitoringView>({ recording: false, monitorDown: false, lastTick: null });
  const [status, setStatus] = useState<StatusMessage | null>(null);
  const [snapshot, setSnapshot] = useState<MonitoringSnapshot | null>(null);

  const attach = useCallback(async () => {
    try {
      const s = await request<MonitoringSnapshot>({ cmd: "get_monitoring_state" });
      setSnapshot(s);
      setView({ recording: s.state === "recording", monitorDown: false, lastTick: s.lastTick });
    } catch {
      // The sidecar is not answering (starting, restarting or gone); events will say which.
    }
  }, [request]);

  useEffect(() => {
    void attach();
    const offTick = subscribe<TickMessage>("sidecar-tick", (tick) =>
      setView((v) => ({ ...v, recording: true, monitorDown: false, lastTick: tick })),
    );
    const offStatus = subscribe<StatusMessage>("sidecar-status", (s) => {
      setStatus(s);
      setView((v) => applyStatus(v, s));
      if (s.state === "recording" || s.state === "idle") void attach();
    });
    const offVisible = subscribe<boolean>("ui-visibility", (visible) => {
      if (visible) void attach();
    });
    return () => {
      offTick();
      offStatus();
      offVisible();
    };
  }, [attach]);

  const start = useCallback(async () => {
    await request({ cmd: "start_monitoring" });
  }, [request]);
  const stop = useCallback(async () => {
    await request({ cmd: "stop_monitoring" });
  }, [request]);

  return { quality: deriveQuality(view), tick: view.lastTick, status, snapshot, start, stop };
}
