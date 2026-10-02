import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import type { SettingsSection } from "@/hooks/useNav";
import { PageHeader } from "@/components/ui/States";
import { DataSection } from "./DataSection";
import { ElectricitySection } from "./ElectricitySection";
import { ApplicationSection, LanguageSection } from "./LanguageAndApplication";
import { EstimationSection, MonitoringSection } from "./MonitoringAndEstimation";

const SECTIONS: SettingsSection[] = ["monitoring", "estimation", "electricity", "data", "language", "application"];

function scrollToSection(section: SettingsSection) {
  const target = document.getElementById(`settings-${section}`);
  if (!target) return;
  const reduce = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
  target.scrollIntoView({ behavior: reduce ? "auto" : "smooth", block: "start" });
  target.querySelector<HTMLElement>("h2")?.focus?.();
}

/** Settings, in six groups. Every editable value validates against a stated range and shows its unit. */
export function Settings({ section }: { section?: SettingsSection }) {
  const { t } = useTranslation();

  // Another page (the tray, "Set a price" on the Dashboard, first run) can ask for a particular group.
  useEffect(() => {
    if (section) requestAnimationFrame(() => scrollToSection(section));
  }, [section]);

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title={t("settings.title")} subtitle={t("settings.subtitle")} />

      <nav aria-label={t("settings.jump")} className="flex flex-wrap gap-2">
        {SECTIONS.map((s) => (
          <button key={s} type="button" className="btn btn-quiet border-surface-border" onClick={() => scrollToSection(s)}>
            {t(`settings.groups.${s}`)}
          </button>
        ))}
      </nav>

      <MonitoringSection />
      <EstimationSection />
      <ElectricitySection />
      <DataSection />
      <LanguageSection />
      <ApplicationSection />
    </div>
  );
}
