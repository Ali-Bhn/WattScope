import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { sidecarRequest, usePolling, type SidecarRequest } from "@/hooks/useSidecar";

export type QueryState<T> =
  | { status: "loading"; data: null; error: null }
  | { status: "ready"; data: T; error: null }
  | { status: "error"; data: T | null; error: unknown };

export interface QueryResult<T> {
  state: QueryState<T>;
  /** Re-runs the request, keeping the current data on screen while it loads. */
  reload: () => Promise<void>;
}

/**
 * A sidecar request with explicit loading / ready / error states. `request` may be null to stay idle.
 * A response that arrives after the request changed (or the component unmounted) is dropped, so a slow
 * answer can never overwrite a newer one. With `pollMs` it refreshes on an interval -- only while the window
 * is visible (see `usePolling`) -- keeping the previous data on screen instead of flashing a spinner.
 */
export function useQuery<T>(request: SidecarRequest | null, options: { pollMs?: number } = {}): QueryResult<T> {
  const key = request ? JSON.stringify(request) : null;
  const [state, setState] = useState<QueryState<T>>({ status: "loading", data: null, error: null });
  const generation = useRef(0);
  const requestRef = useRef(request);
  requestRef.current = request;

  const run = useCallback(async () => {
    const current = requestRef.current;
    if (!current) return;
    const mine = ++generation.current;
    try {
      const data = await sidecarRequest<T>(current);
      if (mine === generation.current) setState({ status: "ready", data, error: null });
    } catch (error) {
      if (mine === generation.current) setState((previous) => ({ status: "error", data: previous.data, error }));
    }
  }, []);

  // A different request is a different question: show loading, not the previous answer.
  useEffect(() => {
    setState({ status: "loading", data: null, error: null });
    if (key !== null) void run();
    return () => {
      generation.current++; // drop any answer still in flight
    };
  }, [key, run]);

  usePolling(
    () => (options.pollMs ? run() : undefined),
    options.pollMs ?? 60_000,
    key !== null && !!options.pollMs,
    true, // the effect above already fetched on mount
  );

  return useMemo(() => ({ state, reload: run }), [state, run]);
}
