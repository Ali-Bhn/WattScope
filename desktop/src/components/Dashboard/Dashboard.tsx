import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMonitoring } from "@/hooks/useSidecar";
import { formatDuration, formatKwh, formatMoney, formatWatts } from "@/lib/format";
import { costVisibility, type MonitoringQuality } from "@/lib/monitoring";
import { Num } from "@/components/Num";
import { StatCard } from "./StatCard";
import { LiveChart } from "@/components/LiveChart/LiveChart";
import type { ReadingState } from "@/lib/types";

/** Why a channel has no value right now -- never a substituted zero (non-negotiable principle #1/#2). */
function unavailableReason(state: ReadingState, t: (key: string) => string): string {
  if (state === "implausibleZero") return t("dashboard.notElevated");
  return `${t("dashboard.unavailable")} (${state})`;
}

const QUALITY_TONE: Record<MonitoringQuality, string> = {
  starting: "border-surface-border bg-surface-1 text-ink-secondary",
  complete: "border-accent-green/30 bg-accent-green/10 text-accent-green",
  partial: "border-amber-500/30 bg-amber-500/10 text-amber-300",
  sensorUnavailable: "border-amber-500/30 bg-amber-500/10 text-amber-300",
  stopped: "border-surface-border bg-surface-1 text-ink-secondary",
  unavailable: "border-red-500/30 bg-red-500/10 text-red-300",
};

export function Dashboard() {
  const { t } = useTranslation();
  const { quality, tick, status, start, stop } = useMonitoring();
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const running = quality === "starting" || quality === "complete" || quality === "partial" || quality === "sensorUnavailable";
  const cost = costVisibility(tick);

  const toggle = async () => {
    setBusy(true);
    setActionError(null);
    try {
      await (running ? stop() : start());
    } catch (e) {
      setActionError(String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col gap-4">
      <div className={`card flex flex-wrap items-center justify-between gap-3 ${QUALITY_TONE[quality]}`}>
        <div className="flex flex-col gap-1">
          <span className="text-sm font-semibold">{t(`dashboard.quality.${quality}`)}</span>
          {quality !== "complete" && <span className="max-w-2xl text-xs opacity-90">{t(`dashboard.qualityHelp.${quality}`)}</span>}
        </div>
        <button type="button" className="btn" onClick={toggle} disabled={busy || quality === "starting"}>
          {running ? t("dashboard.stopMonitoring") : t("dashboard.startMonitoring")}
        </button>
      </div>

      {(actionError || (status && (status.state === "error" || status.state === "failed" || status.state === "restarting") && status.message)) && (
        <div className="card border-red-500/30 bg-red-500/10 text-sm text-red-300">{actionError ?? status?.message}</div>
      )}

      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5">
        <StatCard
          label={t("dashboard.systemPower")}
          value={<Num>{formatWatts(tick?.estimatedTotalWatts ?? null)}</Num>}
          badge={{ text: t("dashboard.estimatedBadge"), tone: "estimated" }}
          sub={tick ? t(`dashboard.completeness.${tick.estimateCompleteness}`) : undefined}
        />
        <StatCard
          label={t("dashboard.cpuPower")}
          value={<Num>{tick && tick.cpuState !== "valid" ? "--" : formatWatts(tick?.cpuWatts ?? null)}</Num>}
          badge={{ text: t("dashboard.measuredBadge"), tone: "measured" }}
          sub={tick && tick.cpuState !== "valid" ? unavailableReason(tick.cpuState, t) : undefined}
        />
        <StatCard
          label={t("dashboard.gpuPower")}
          value={<Num>{tick && tick.gpuState !== "valid" ? "--" : formatWatts(tick?.gpuWatts ?? null)}</Num>}
          badge={{ text: t("dashboard.measuredBadge"), tone: "measured" }}
          sub={tick && tick.gpuState !== "valid" ? unavailableReason(tick.gpuState, t) : undefined}
        />
        <StatCard
          label={t("dashboard.otherPower")}
          value={<Num>{formatWatts(tick?.otherComponentsWatts ?? null)}</Num>}
          badge={{ text: t("settings.assumption", "assumption"), tone: "neutral" }}
        />
        <StatCard label={t("dashboard.sessionDuration")} value={<Num>{formatDuration(tick?.sessionSeconds ?? 0)}</Num>} />
        <StatCard
          label={t("dashboard.sessionEnergy")}
          value={<Num>{formatKwh((tick?.sessionKwh ?? 0) * 3_600_000)}</Num>}
          badge={{ text: t("dashboard.estimatedBadge"), tone: "estimated" }}
        />
        <StatCard
          label={t("dashboard.sessionCost")}
          value={<Num>{cost === "complete" && tick?.sessionCostApprox != null ? formatMoney(tick.sessionCostApprox, tick.currency ?? "EUR", 4) : "n/a"}</Num>}
          sub={cost === "complete" ? t("dashboard.approxCost") : cost === "withheldPartial" ? t("dashboard.costWithheld") : undefined}
        />
        <StatCard
          label={t("dashboard.sensorAvailability")}
          value={
            <span className="text-sm">
              CPU {tick?.cpuState === "valid" ? "✓" : "✕"} / GPU {tick?.gpuState === "valid" ? "✓" : "✕"}
            </span>
          }
        />
        <StatCard
          label={t("dashboard.estimateCompleteness")}
          value={<span className="text-sm">{tick ? t(`dashboard.completeness.${tick.estimateCompleteness}`) : "--"}</span>}
        />
      </div>

      <LiveChart />
    </div>
  );
}
