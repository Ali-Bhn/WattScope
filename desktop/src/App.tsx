import { useCallback, useEffect, useMemo, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { BackendBanner } from "@/components/BackendBanner";
import { Dashboard } from "@/components/Dashboard/Dashboard";
import { FirstRun } from "@/components/FirstRun";
import { Gaming } from "@/components/Gaming/Gaming";
import { Reports } from "@/components/Reports/Reports";
import { Settings } from "@/components/Settings/Settings";
import { useBackend, useIsRecording, useStartMonitoringStore, sidecarRequest } from "@/hooks/useSidecar";
import { NavContext, type SettingsSection, type View } from "@/hooks/useNav";
import { shouldShowFirstRun } from "@/lib/firstRun";
import type { AppState } from "@/lib/types";

const VIEWS: View[] = ["dashboard", "gaming", "reports", "settings"];

export default function App() {
  const { t } = useTranslation();
  useStartMonitoringStore(); // the window's single set of subscriptions (see lib/store.ts)

  const [view, setView] = useState<View>("dashboard");
  const [section, setSection] = useState<SettingsSection | undefined>(undefined);

  const navigate = useCallback((next: View, nextSection?: SettingsSection) => {
    setView(next);
    setSection(nextSection);
  }, []);
  const nav = useMemo(() => ({ navigate }), [navigate]);

  // The tray's "Settings" item asks the window to open that page.
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    let cancelled = false;
    listen<View>("navigate", (e) => navigate(e.payload)).then(
      (fn) => {
        if (cancelled) fn();
        else unlisten = fn;
      },
      () => undefined,
    );
    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [navigate]);

  return (
    <NavContext.Provider value={nav}>
      <div className="flex h-full min-w-0">
        <nav aria-label={t("nav.label")} className="flex w-44 shrink-0 flex-col gap-1 border-e border-surface-border bg-surface-1 p-3">
          <div className="mb-3 flex items-center justify-between gap-2 px-2">
            <span className="text-sm font-semibold tracking-wide text-ink-primary">{t("app.title")}</span>
            <RecordingDot />
          </div>
          {VIEWS.map((v) => (
            <NavButton key={v} active={view === v} onClick={() => navigate(v)}>
              {t(`nav.${v}`)}
            </NavButton>
          ))}
        </nav>

        <main className="flex min-w-0 flex-1 flex-col gap-4 overflow-y-auto p-5">
          <BackendBanner />
          {view === "dashboard" && <Dashboard />}
          {view === "gaming" && <Gaming />}
          {view === "reports" && <Reports />}
          {view === "settings" && <Settings section={section} />}
        </main>
      </div>
      <FirstRunGate navigate={navigate} />
    </NavContext.Provider>
  );
}

/** A small always-visible indicator of whether WattScope is recording -- shape and label, not just colour. */
function RecordingDot() {
  const { t } = useTranslation();
  const recording = useIsRecording();
  const backend = useBackend();
  const down = backend.state === "restarting" || backend.state === "failed";
  const label = down ? t("nav.status.down") : recording ? t("nav.status.recording") : t("nav.status.stopped");
  return (
    <span className="flex items-center gap-1 text-[11px] text-ink-secondary" title={label}>
      <span aria-hidden="true" className={down ? "text-red-300" : recording ? "text-accent-green" : "text-ink-muted"}>
        {down ? "✕" : recording ? "●" : "○"}
      </span>
      <span className="sr-only">{label}</span>
    </span>
  );
}

function NavButton({ active, children, onClick }: { active: boolean; children: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={active ? "page" : undefined}
      className={`rounded-md px-3 py-2 text-start text-sm font-medium transition-colors ${active ? "bg-surface-3 text-ink-primary" : "text-ink-secondary hover:bg-surface-2"}`}
    >
      {children}
    </button>
  );
}

/** Loads the persisted first-run state once, and offers the introduction only if it has not been completed. */
function FirstRunGate({ navigate }: { navigate: (view: View, section?: SettingsSection) => void }) {
  const [state, setState] = useState<AppState | null>(null);
  const [dismissed, setDismissed] = useState(false);
  const backend = useBackend();

  // Ask (again) whenever the monitor becomes reachable; an unanswered question shows nothing.
  useEffect(() => {
    if (backend.state !== "running") return;
    let cancelled = false;
    sidecarRequest<AppState>({ cmd: "get_app_state" }).then(
      (s) => !cancelled && setState(s),
      () => undefined,
    );
    return () => {
      cancelled = true;
    };
  }, [backend.state]);

  const complete = async (then?: () => void) => {
    try {
      await sidecarRequest({ cmd: "complete_first_run" });
      setState((s) => (s ? { ...s, firstRunCompleted: true } : s));
    } catch {
      setDismissed(true); // could not persist: do not trap the user; it is offered again next launch
    }
    then?.();
  };

  if (!shouldShowFirstRun(state, dismissed)) return null;
  return <FirstRun onContinue={() => void complete()} onSetPrice={() => void complete(() => navigate("settings", "electricity"))} onDismiss={() => setDismissed(true)} />;
}
