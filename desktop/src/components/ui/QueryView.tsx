import type { ReactNode } from "react";
import type { QueryResult } from "@/hooks/useQuery";
import { ErrorPanel, Loading } from "./States";

/**
 * Renders a query's three states deliberately: loading, error (with Retry), ready. When a refresh fails but an
 * earlier answer exists, that answer stays on screen under a compact error instead of vanishing.
 */
export function QueryView<T>({ query, children, loadingLabel }: { query: QueryResult<T>; children: (data: T) => ReactNode; loadingLabel?: string }) {
  const { state, reload } = query;
  if (state.status === "loading") return <Loading label={loadingLabel} />;
  if (state.status === "error" && state.data === null) return <ErrorPanel error={state.error} onRetry={() => void reload()} />;
  return (
    <>
      {state.status === "error" && <ErrorPanel error={state.error} onRetry={() => void reload()} compact />}
      {state.data !== null && children(state.data)}
    </>
  );
}
