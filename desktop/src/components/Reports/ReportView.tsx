import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Num } from "@/components/Num";
import { Badge, CompletenessBadge } from "@/components/ui/Badge";
import { Hint } from "@/components/ui/Hint";
import { EmptyState } from "@/components/ui/States";
import { StatCard } from "@/components/ui/StatCard";
import { formatDuration, formatKwh, formatMoney, parseTimeSpanSeconds } from "@/lib/format";
import { completenessOf, costModel, energyModel, hasData } from "@/lib/reports";
import type { EnergyReport } from "@/lib/types";

/**
 * One period's energy and cost, laid out so that nothing is merged dishonestly: Complete and Partial
 * estimated energy side by side (never one figure), measured CPU/GPU energy as measured, cost only for
 * priced complete energy with the partial lower bound and unpriced energy reported on their own, and a
 * missing measurement shown as "--" rather than 0.
 */
export function ReportView({ report, title, children }: { report: EnergyReport; title: string; children?: ReactNode }) {
  const { t } = useTranslation();
  const { totals, cost } = report;

  if (!hasData(report)) {
    return (
      <EmptyState title={title}>
        {t("reports.noData")}
        {children}
      </EmptyState>
    );
  }

  const energy = energyModel(totals);
  const money = costModel(cost, totals);
  const completeness = completenessOf(totals);
  const gapSeconds = parseTimeSpanSeconds(report.gapDuration) ?? 0;

  return (
    <section className="flex min-w-0 flex-col gap-3" aria-label={title}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-sm font-semibold">{title}</h2>
        <CompletenessBadge completeness={completeness} />
      </div>

      <div className="grid grid-cols-[repeat(auto-fit,minmax(12rem,1fr))] gap-3">
        <StatCard label={t("reports.monitoredTime")} value={<Num>{formatDuration(totals.monitoredSeconds)}</Num>} />

        <StatCard
          label={t("reports.systemEnergy")}
          hint={<Hint>{t("reports.hints.systemEnergy")}</Hint>}
          badge={energy.kind === "none" ? "unavailable" : energy.kind === "partial" ? "partial" : "estimated"}
          value={
            <Num>
              {energy.kind === "none" ? "--" : energy.kind === "partial" ? `≥ ${formatKwh(energy.partialJoules)}` : formatKwh(energy.completeJoules)}
            </Num>
          }
          sub={
            energy.kind === "mixed" ? (
              <span className="flex flex-col gap-0.5">
                <span>{t("reports.completeOnly")}</span>
                <span className="flex items-center gap-1">
                  <Badge kind="partial" />
                  <Num>≥ {formatKwh(energy.partialJoules)}</Num>
                </span>
              </span>
            ) : energy.kind === "partial" ? (
              t("reports.partialOnly")
            ) : energy.kind === "none" ? (
              t("reports.noEstimate")
            ) : undefined
          }
        />

        <StatCard
          label={t("terms.cpuEnergy")}
          badge={totals.cpuPackageSeconds > 0 ? "measured" : "unavailable"}
          value={<Num>{totals.cpuPackageSeconds > 0 ? formatKwh(totals.cpuPackageJoules) : "--"}</Num>}
          sub={totals.cpuPackageSeconds > 0 ? <Num>{formatDuration(totals.cpuPackageSeconds)}</Num> : t("reports.notMeasured")}
        />
        <StatCard
          label={t("terms.gpuEnergy")}
          badge={totals.gpuBoardSeconds > 0 ? "measured" : "unavailable"}
          value={<Num>{totals.gpuBoardSeconds > 0 ? formatKwh(totals.gpuBoardJoules) : "--"}</Num>}
          sub={totals.gpuBoardSeconds > 0 ? <Num>{formatDuration(totals.gpuBoardSeconds)}</Num> : t("reports.notMeasured")}
        />

        <StatCard
          label={t("terms.cost")}
          badge={money.kind === "amount" ? "estimated" : money.kind === "partialOnly" ? "partial" : "unavailable"}
          value={
            <Num>
              {money.kind === "amount"
                ? formatMoney(money.amount, money.currency)
                : money.kind === "partialOnly"
                  ? `≥ ${formatMoney(money.partialLowerBound, money.currency)}`
                  : "--"}
            </Num>
          }
          sub={
            money.kind === "amount" ? (
              money.incomplete ? t("reports.costCompleteOnly") : undefined
            ) : money.kind === "partialOnly" ? (
              t("reports.costPartialOnly")
            ) : money.kind === "unpricedOnly" ? (
              t("reports.costNoPrice")
            ) : (
              t("reports.costNone")
            )
          }
        />
      </div>

      <ul className="flex flex-col gap-1 text-xs text-ink-secondary">
        {money.kind === "amount" && money.partialLowerBound !== null && (
          <li>
            {t("reports.partialCostNote")}: <Num>≥ {formatMoney(money.partialLowerBound, money.currency)}</Num>
          </li>
        )}
        {money.unpricedJoules > 0 && (
          <li>
            <Num>{formatKwh(money.unpricedJoules)}</Num> {t("reports.unpriced")}
          </li>
        )}
        {report.gapCount > 0 && (
          <li>
            {t("reports.gaps", { count: report.gapCount })}: <Num>{formatDuration(gapSeconds)}</Num> — {t("reports.gapsNote")}
          </li>
        )}
        {energy.uncoveredSeconds > 1 && (
          <li>
            <Num>{formatDuration(energy.uncoveredSeconds)}</Num> {t("reports.uncovered")}
          </li>
        )}
        {cost.precision === "exactReplayed" && <li className="text-accent-blue">{t("reports.replayedNotice")}</li>}
        {cost.precision === "approximateProportional" && <li className="text-amber-300">{t("reports.approxNotice")}</li>}
      </ul>

      {report.assumptions.length > 0 && (
        <details className="text-xs text-ink-muted">
          <summary className="cursor-pointer select-none">{t("reports.assumptions")}</summary>
          <p className="mt-1 max-w-prose">{t("reports.assumptionsNote")}</p>
          {/* The backend's own wording of the settings used: technical text, English by design. */}
          <ul lang="en" dir="ltr" className="mt-1 list-inside list-disc">
            {report.assumptions.map((a) => (
              <li key={a}>{a}</li>
            ))}
          </ul>
        </details>
      )}
      {children}
    </section>
  );
}
