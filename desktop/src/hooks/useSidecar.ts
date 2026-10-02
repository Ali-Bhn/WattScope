import { useCallback, useEffect, useRef, useSyncExternalStore } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { createMonitoringStore, qualityOf, type MonitoringState, type SidecarRequest, type StoreIO } from "@/lib/store";
import { isRecording, type MonitoringQuality } from "@/lib/monitoring";
import { errorMessage } from "@/lib/errors";
import type { BackendState, MonitoringSnapshot, StatusMessage, TickMessage } from "@/lib/types";

export type { SidecarRequest };

interface SidecarResponse<T> {
  type: "response";
  id: number | null;
  ok: boolean;
  data: T | null;
  error: string | null;
}

/** Calls the sidecar through the host. Rejects with an Error carrying the (technical) message. */
export async function sidecarRequest<T>(request: SidecarRequest): Promise<T> {
  let response: SidecarResponse<T>;
  try {
    response = await invoke<SidecarResponse<T>>("sidecar_request", { request });
  } catch (e) {
    throw new Error(errorMessage(e)); // the host's own failure ("the sidecar is not running", a timeout)
  }
  if (!response.ok) throw new Error(response.error ?? "The sidecar request failed for an unknown reason.");
  return response.data as T;
}

/** Stable function identity, so it is safe in effect dependency lists. */
export function useSidecarRequest() {
  return useCallback(<T,>(request: SidecarRequest) => sidecarRequest<T>(request), []);
}

function tauriListen<T>(event: string, handler: (payload: T) => void): () => void {
  let unlisten: UnlistenFn | undefined;
  let cancelled = false;
  listen<T>(event, (e) => handler(e.payload))
    .then((fn) => {
      if (cancelled) fn();
      else unlisten = fn;
    })
    .catch(() => undefined); // not inside Tauri (a plain browser): nothing to listen to
  return () => {
    cancelled = true;
    unlisten?.();
  };
}

const tauriIO: StoreIO = {
  request: sidecarRequest,
  listen: tauriListen,
  getBackendState: () => invoke<BackendState>("get_backend_state"),
  onDocumentVisibility(handler) {
    const onChange = () => handler(!document.hidden);
    document.addEventListener("visibilitychange", onChange);
    return () => document.removeEventListener("visibilitychange", onChange);
  },
};

/** The window's single monitoring store (see lib/store.ts). */
export const monitoringStore = createMonitoringStore(tauriIO);

/** Reads one slice of the store; re-renders only when that slice changes (it must return a primitive or a stable reference). */
function useSlice<T>(select: (state: MonitoringState) => T): T {
  return useSyncExternalStore(monitoringStore.subscribe, () => select(monitoringStore.getState()));
}

/** Call once at the top of the app: starts the single set of subscriptions and stops them on unmount. */
export function useStartMonitoringStore() {
  useEffect(() => monitoringStore.start(), []);
}

export const useQuality = (): MonitoringQuality => useSlice(qualityOf);
export const useTick = (): TickMessage | null => useSlice((s) => s.view.lastTick);
export const useStatus = (): StatusMessage | null => useSlice((s) => s.status);
export const useSnapshot = (): MonitoringSnapshot | null => useSlice((s) => s.snapshot);
export const useBackend = (): BackendState => useSlice((s) => s.backend);
export const useUiVisible = (): boolean => useSlice((s) => s.uiVisible);
export const useAttached = (): boolean => useSlice((s) => s.attached);

/** The live gaming session's id (a primitive): lets a page react to a session starting or ending without re-rendering on every tick. */
export const useGamingSessionId = (): number | null => useSlice((s) => s.view.lastTick?.gaming?.sessionId ?? null);

/** Whether WattScope is recording right now (a primitive, so consumers re-render only when it flips). */
export const useIsRecording = (): boolean => useSlice((s) => isRecording(qualityOf(s)));

export function useMonitoringActions() {
  return {
    start: useCallback(() => monitoringStore.startMonitoring(), []),
    stop: useCallback(() => monitoringStore.stopMonitoring(), []),
    refresh: useCallback(() => monitoringStore.attach(), []),
  };
}

/**
 * Runs `task` now and then every `ms` -- but only while the window is actually visible. A window hidden to the
 * tray therefore does no polling at all, and showing it again refreshes immediately. `task` may change identity
 * freely; the latest one is always used and it never causes the timer to restart.
 */
export function usePolling(task: () => void | Promise<unknown>, ms: number, enabled = true, skipInitialRun = false) {
  const visible = useUiVisible();
  const taskRef = useRef(task);
  taskRef.current = task;
  const ranOnce = useRef(false);

  useEffect(() => {
    if (!enabled || !visible) return;
    let cancelled = false;
    const run = () => {
      if (!cancelled) void Promise.resolve(taskRef.current()).catch(() => undefined);
    };
    // `skipInitialRun`: the caller already fetched on mount; later (re)starts -- the window being shown again -- do refresh.
    if (!(skipInitialRun && !ranOnce.current)) run();
    ranOnce.current = true;
    const id = setInterval(run, ms);
    return () => {
      cancelled = true;
      clearInterval(id);
    };
  }, [ms, enabled, visible, skipInitialRun]);
}
