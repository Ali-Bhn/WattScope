import { useTranslation } from "react-i18next";
import { useQuery } from "@/hooks/useQuery";
import { sidecarRequest } from "@/hooks/useSidecar";
import { Badge } from "@/components/ui/Badge";
import { Notice } from "@/components/ui/Notice";
import { QueryView } from "@/components/ui/QueryView";
import { formatNumber } from "@/lib/format";
import type { EstimationSettings } from "@/lib/types";
import { NumberSettingField, SettingBlock, SettingsGroup } from "./parts";
import { useNumberSetting } from "./useNumberSetting";

// ---------------------------------------------------------------- Monitoring

export function MonitoringSection() {
  const { t } = useTranslation();
  const query = useQuery<{ intervalMs: number }>({ cmd: "get_monitoring_interval" });

  return (
    <SettingsGroup id="monitoring" title={t("settings.groups.monitoring")} description={t("settings.monitoring.description")}>
      <QueryView query={query}>{(data) => <IntervalSetting initial={String(data.intervalMs)} />}</QueryView>
    </SettingsGroup>
  );
}

function IntervalSetting({ initial }: { initial: string }) {
  const { t } = useTranslation();
  const setting = useNumberSetting("intervalMs", initial, async (parsed) => {
    await sidecarRequest({ cmd: "set_monitoring_interval", intervalMs: parsed.value });
  });
  const seconds = setting.parsed.ok ? setting.parsed.value / 1000 : null;

  return (
    <SettingBlock title={t("settings.monitoring.interval")}>
      <NumberSettingField
        setting={setting}
        spec="intervalMs"
        label={t("settings.monitoring.interval")}
        unit="ms"
        inputMode="numeric"
        help={
          <>
            {t("settings.monitoring.intervalHelp")}
            {seconds !== null && <span dir="ltr"> ({formatNumber(seconds, 0, 2)} s)</span>}
          </>
        }
      />
    </SettingBlock>
  );
}

// ---------------------------------------------------------------- Energy estimation

const percentText = (efficiency: number) => String(Number((efficiency * 100).toFixed(1)));

export function EstimationSection() {
  const { t } = useTranslation();
  const query = useQuery<EstimationSettings>({ cmd: "get_estimation" });

  return (
    <SettingsGroup id="estimation" title={t("settings.groups.estimation")} description={t("settings.estimation.description")}>
      <QueryView query={query}>{(settings) => <EstimationForm settings={settings} />}</QueryView>
    </SettingsGroup>
  );
}

function EstimationForm({ settings }: { settings: EstimationSettings }) {
  const { t } = useTranslation();

  const other = useNumberSetting("otherWatts", String(settings.otherComponentsWatts), async (parsed) => {
    const updated = await sidecarRequest<EstimationSettings>({ cmd: "set_estimation", otherWatts: parsed.value });
    return String(updated.otherComponentsWatts);
  });
  const psu = useNumberSetting("psuPercent", percentText(settings.psuEfficiency), async (parsed) => {
    const updated = await sidecarRequest<EstimationSettings>({ cmd: "set_estimation", psuEfficiency: parsed.value / 100 });
    return percentText(updated.psuEfficiency);
  });

  return (
    <>
      <SettingBlock>
        <Notice tone="info" title={t("settings.estimation.formulaTitle")}>
          <p>{t("settings.estimation.formula")}</p>
          <p className="mt-1">{t("settings.estimation.notWall")}</p>
        </Notice>
      </SettingBlock>

      <SettingBlock title={t("terms.otherPower")}>
        <div className="flex items-center gap-2">
          <Badge kind="assumed" />
          <span className="text-xs text-ink-secondary">{t("settings.estimation.assumptionNote")}</span>
        </div>
        <NumberSettingField setting={other} spec="otherWatts" label={t("settings.estimation.otherWatts")} unit="W" help={t("settings.estimation.otherWattsHelp")} />
      </SettingBlock>

      <SettingBlock title={t("terms.psuEfficiency")}>
        <div className="flex items-center gap-2">
          <Badge kind="assumed" />
          <span className="text-xs text-ink-secondary">{t("settings.estimation.assumptionNote")}</span>
        </div>
        <NumberSettingField setting={psu} spec="psuPercent" label={t("settings.estimation.psuEfficiency")} unit="%" help={t("settings.estimation.psuHelp")} />
      </SettingBlock>

      <SettingBlock title={t("settings.estimation.inputsTitle")}>
        <ul className="flex flex-col gap-1 text-sm">
          <li className="flex items-center gap-2">
            <Badge kind="measured" />
            {t("terms.cpuPower")}: {settings.includeCpuPackage ? t("settings.estimation.required") : t("settings.estimation.notRequired")}
          </li>
          <li className="flex items-center gap-2">
            <Badge kind="measured" />
            {t("terms.gpuPower")}: {settings.includeGpuBoard ? t("settings.estimation.required") : t("settings.estimation.notRequired")}
          </li>
        </ul>
        <p className="max-w-prose text-xs text-ink-muted">{t("settings.estimation.appliesNext")}</p>
      </SettingBlock>
    </>
  );
}
