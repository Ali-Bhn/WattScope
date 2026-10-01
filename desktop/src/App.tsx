import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Dashboard } from "@/components/Dashboard/Dashboard";
import { Reports } from "@/components/Reports/Reports";
import { Settings } from "@/components/Settings/Settings";

type View = "dashboard" | "reports" | "settings";

export default function App() {
  const { t } = useTranslation();
  const [view, setView] = useState<View>("dashboard");

  return (
    <div className="flex h-full">
      <nav className="flex w-48 shrink-0 flex-col gap-1 border-e border-surface-border bg-surface-1 p-3">
        <div className="mb-3 px-2 text-sm font-semibold tracking-wide text-ink-primary">{t("app.title")}</div>
        <NavButton active={view === "dashboard"} onClick={() => setView("dashboard")}>
          {t("nav.dashboard")}
        </NavButton>
        <NavButton active={view === "reports"} onClick={() => setView("reports")}>
          {t("nav.reports")}
        </NavButton>
        <NavButton active={view === "settings"} onClick={() => setView("settings")}>
          {t("nav.settings")}
        </NavButton>
      </nav>

      <main className="flex-1 overflow-y-auto p-5">
        {view === "dashboard" && <Dashboard />}
        {view === "reports" && <Reports />}
        {view === "settings" && <Settings />}
      </main>
    </div>
  );
}

function NavButton({ active, children, onClick }: { active: boolean; children: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`rounded-md px-3 py-2 text-start text-sm font-medium transition-colors ${
        active ? "bg-surface-3 text-ink-primary" : "text-ink-secondary hover:bg-surface-2"
      }`}
    >
      {children}
    </button>
  );
}
