import { useCallback, useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import type { StatusMessage, TickMessage } from "@/lib/types";

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

/** The sidecar's most recent lifecycle event (recording started, suspended, stopped, error, ...). */
export function useSidecarStatus(): StatusMessage | null {
  const [status, setStatus] = useState<StatusMessage | null>(null);

  useEffect(() => {
    let unlisten: UnlistenFn | undefined;
    let cancelled = false;
    listen<StatusMessage>("sidecar-status", (event) => setStatus(event.payload)).then((fn) => {
      if (cancelled) fn();
      else unlisten = fn;
    });
    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, []);

  return status;
}

/**
 * The most recent sample, for live dashboard numbers that should re-render
 * every tick. The live chart does NOT use this -- it subscribes to the same
 * event itself and draws to a canvas imperatively, so one more open tab or
 * card never costs the chart an extra re-render (Part E/J).
 */
export function useLatestTick(): TickMessage | null {
  const [tick, setTick] = useState<TickMessage | null>(null);

  useEffect(() => {
    let unlisten: UnlistenFn | undefined;
    let cancelled = false;
    listen<TickMessage>("sidecar-tick", (event) => setTick(event.payload)).then((fn) => {
      if (cancelled) fn();
      else unlisten = fn;
    });
    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, []);

  return tick;
}
