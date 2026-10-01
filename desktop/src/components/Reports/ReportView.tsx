import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Num } from "@/components/Num";
import { formatDuration, formatKwh, formatMoney } from "@/lib/format";
import type { EnergyReport } from "@/lib/types";

export function ReportView({ report }: { report: EnergyReport }) {
  const { t } = useTranslation();
  const { totals, cost } = report;

  return (
    <div className="card flex flex-col gap-3">
      <h3 className="text-sm font-semibold">{report.title}</h3>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <Field label={t("reports.duration")} value={<Num>{formatDuration(totals.monitoredSeconds)}</Num>} />
        <Field label={`CPU`} value={<Num>{formatKwh(totals.cpuPackageJoules)}</Num>} />
        <Field label={`GPU`} value={<Num>{formatKwh(totals.gpuBoardJoules)}</Num>} />
        <Field label={t("reports.energy")} value={<Num>{formatKwh(totals.estimatedCompleteJoules)}</Num>} />
      </div>

      <div className="flex flex-wrap items-center gap-3 border-t border-surface-border pt-3">
        <Field
          label={t("reports.cost")}
          value={<Num>{cost.estimatedCostRounded != null ? formatMoney(cost.estimatedCostRounded, cost.currency) : t("reports.costUnavailable")}</Num>}
        />
        {cost.precision === "exactReplayed" && (
          <p className="text-xs text-accent-blue">{t("reports.replayedNotice")}</p>
        )}
        {cost.precision === "approximateProportional" && (
          <p className="text-xs text-amber-400">{t("reports.approxNotice")}</p>
        )}
        {cost.unpricedJoules > 0 && (
          <p className="text-xs text-ink-secondary">
            <Num>{formatKwh(cost.unpricedJoules)}</Num> unpriced
          </p>
        )}
      </div>

      {report.assumptions.length > 0 && (
        <ul className="list-inside list-disc text-xs text-ink-muted">
          {report.assumptions.map((a) => (
            <li key={a}>{a}</li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Field({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="label">{label}</span>
      <span className="text-sm font-medium">{value}</span>
    </div>
  );
}
