import { useTranslation } from "react-i18next";
import { useLatestTick, useSidecarStatus } from "@/hooks/useSidecar";
import { formatDuration, formatKwh, formatMoney, formatWatts } from "@/lib/format";
import { Num } from "@/components/Num";
import { StatCard } from "./StatCard";
import { LiveChart } from "@/components/LiveChart/LiveChart";
import type { ReadingState } from "@/lib/types";

/** Why a channel has no value right now -- never a substituted zero (non-negotiable principle #1/#2). */
function unavailableReason(state: ReadingState, t: (key: string) => string): string {
  if (state === "implausibleZero") return t("dashboard.notElevated");
  return `${t("dashboard.unavailable")} (${state})`;
}

export function Dashboard() {
  const { t } = useTranslation();
  const tick = useLatestTick();
  const status = useSidecarStatus();

  const statusState = status?.state ?? (tick ? "recording" : undefined);
  const statusLabel = statusState ? t(`dashboard.status.${statusState}`, statusState) : t("common.loading");
  const statusTone = statusState === "error" ? "warn" : statusState === "recording" ? "measured" : "neutral";

  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5">
        <StatCard
          label={t("dashboard.monitoringStatus")}
          value={statusLabel}
          badge={{ text: statusTone === "warn" ? "!" : "", tone: statusTone }}
        />
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
          value={<Num>{tick?.sessionCostApprox != null ? formatMoney(tick.sessionCostApprox, tick.currency ?? "EUR", 4) : "n/a"}</Num>}
          sub={tick?.sessionCostApprox != null ? t("dashboard.approxCost") : undefined}
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

      {status?.message && (
        <div className="card border-amber-500/30 bg-amber-500/10 text-sm text-amber-300">{status.message}</div>
      )}
    </div>
  );
}
