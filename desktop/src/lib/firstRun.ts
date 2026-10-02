import type { AppState } from "@/lib/types";

/**
 * Whether to offer the first-run introduction. Only when the sidecar has *said* it has not been completed:
 * while the state is unknown (loading, backend down, an error) nothing is shown, so a missing answer can
 * neither flash the dialog at a returning user nor trap a new one. Dismissing it with Escape hides it for this
 * session only; it comes back until the user continues once.
 */
export function shouldShowFirstRun(state: AppState | null, dismissedThisSession: boolean): boolean {
  return state !== null && state.firstRunCompleted === false && !dismissedThisSession;
}
