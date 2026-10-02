import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useAttached, useBackend, useMonitoringActions, useQuality, useSnapshot, useStatus, useTick } from "@/hooks/useSidecar";
import { useNav } from "@/hooks/useNav";
import { formatDuration, formatEnergy, formatKwhValue, formatMoney, formatWatts } from "@/lib/format";
import { costVisibility, isRecording, sessionEnergy, type MonitoringQuality } from "@/lib/monitoring";
import { Num } from "@/components/Num";
import { LiveChart } from "@/components/LiveChart/LiveChart";
import { Badge, CompletenessBadge, type BadgeKind } from "@/components/ui/Badge";
import { Hint } from "@/components/ui/Hint";
import { Notice } from "@/components/ui/Notice";
import { ErrorPanel, Loading, PageHeader } from "@/components/ui/States";
import { StatCard } from "@/components/ui/StatCard";
import type { ReadingState, TickMessage } from "@/lib/types";

const QUALITY_VIEW: Record<MonitoringQuality, { badge: BadgeKind | null; tone: string }> = {
  starting: { badge: null, tone: "border-surface-border bg-surface-1" },
  complete: { badge: "complete", tone: "border-accent-green/30 bg-accent-green/10" },
  partial: { badge: "partial", tone: "border-amber-500/30 bg-amber-500/10" },
  sensorUnavailable: { badge: "unavailable", tone: "border-amber-500/30 bg-amber-500/10" },
  stopped: { badge: null, tone: "border-surface-border bg-surface-1" },
  unavailable: { badge: "unavailable", tone: "border-red-500/30 bg-red-500/10" },
};

/** Whether a channel currently has a usable (valid) reading. */
const isValid = (state: ReadingState | undefined) => state === "valid";

export function Dashboard() {
  const { t } = useTranslation();
  const attached = useAttached();
  const quality = useQuality();
  const tick = useTick();
  const backend = useBackend();
  const snapshot = useSnapshot();
  const status = useStatus();
  const { start, stop } = useMonitoringActions();
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<unknown>(null);

  const running = isRecording(quality);
  const backendUp = backend.state === "running";

  const toggle = async () => {
    setBusy(true);
    setActionError(null);
    try {
      await (running ? stop() : start());
    } catch (e) {
      setActionError(e);
    } finally {
      setBusy(false);
    }
  };

  if (!attached) return <Loading />;

  const view = QUALITY_VIEW[quality];

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title={t("dashboard.title")} subtitle={t("dashboard.subtitle")} />

      <section className={`card flex flex-wrap items-center justify-between gap-3 ${view.tone}`} aria-labelledby="monitoring-state">
        <div className="flex min-w-0 flex-col gap-1.5">
          <div className="flex flex-wrap items-center gap-2">
            <h2 id="monitoring-state" className="text-sm font-semibold">
              {t(`dashboard.quality.${quality}`)}
            </h2>
            {view.badge && <Badge kind={view.badge} />}
          </div>
          {quality !== "complete" && <p className="max-w-2xl text-xs text-ink-secondary">{t(`dashboard.qualityHelp.${quality}`)}</p>}
          {snapshot && running && (
            <p className="text-xs text-ink-muted">
              {t("dashboard.interval")}: <Num>{snapshot.intervalMs} ms</Num>
            </p>
          )}
        </div>
        <button type="button" className={`btn ${running ? "" : "btn-primary"}`} onClick={() => void toggle()} disabled={busy || quality === "starting" || !backendUp}>
          {running ? t("dashboard.stopMonitoring") : t("dashboard.startMonitoring")}
        </button>
      </section>

      {actionError !== null && <ErrorPanel error={actionError} compact />}
      {status?.state === "error" && (
        <Notice tone="error" title={t("dashboard.monitorError")}>
          <p>{t("dashboard.monitorErrorHint")}</p>
          {status.message && (
            <details className="mt-1 text-ink-secondary">
              <summary className="cursor-pointer select-none">{t("errors.details")}</summary>
              <p dir="ltr" className="mt-1 break-words font-mono text-[11px]">
                {status.message}
              </p>
            </details>
          )}
        </Notice>
      )}

      <PowerSection tick={tick} quality={quality} />
      <SessionSection tick={tick} quality={quality} />
      <SensorSection tick={tick} />
      <LiveChart />
    </div>
  );
}

function noReading(quality: MonitoringQuality, t: (key: string) => string): string {
  return quality === "starting" ? t("dashboard.noReading.waiting") : t("dashboard.noReading.stopped");
}

function PowerSection({ tick, quality }: { tick: TickMessage | null; quality: MonitoringQuality }) {
  const { t } = useTranslation();
  const idle = noReading(quality, t);
  const cpuOk = isValid(tick?.cpuState);
  const gpuOk = isValid(tick?.gpuState);

  const systemBadge: BadgeKind = !tick || tick.estimatedTotalWatts === null ? "unavailable" : tick.estimateCompleteness === "partial" ? "partial" : "estimated";

  return (
    <section className="flex flex-col gap-2" aria-labelledby="power-now">
      <h2 id="power-now" className="text-sm font-semibold text-ink-secondary">
        {t("dashboard.sections.power")}
      </h2>
      <div className="grid grid-cols-[repeat(auto-fit,minmax(12rem,1fr))] gap-3">
        <StatCard
          label={t("terms.systemPower")}
          hint={<Hint>{t("dashboard.hints.systemPower")}</Hint>}
          badge={systemBadge}
          value={
            <Num>
              {tick && tick.estimatedTotalWatts !== null ? `${tick.estimateCompleteness === "partial" ? "≥ " : ""}${formatWatts(tick.estimatedTotalWatts)}` : "--"}
            </Num>
          }
          sub={tick ? <CompletenessLine completeness={tick.estimateCompleteness} /> : idle}
        />
        <StatCard
          label={t("terms.cpuPower")}
          badge={cpuOk ? "measured" : "unavailable"}
          value={<Num>{cpuOk ? formatWatts(tick?.cpuWatts ?? null) : "--"}</Num>}
          sub={tick ? (cpuOk ? undefined : t(`readingState.${tick.cpuState}`)) : idle}
        />
        <StatCard
          label={t("terms.gpuPower")}
          badge={gpuOk ? "measured" : "unavailable"}
          value={<Num>{gpuOk ? formatWatts(tick?.gpuWatts ?? null) : "--"}</Num>}
          sub={tick ? (gpuOk ? undefined : t(`readingState.${tick.gpuState}`)) : idle}
        />
        <StatCard
          label={t("terms.otherPower")}
          hint={<Hint>{t("dashboard.hints.otherPower")}</Hint>}
          badge="assumed"
          value={<Num>{formatWatts(tick?.otherComponentsWatts ?? null, 0)}</Num>}
          sub={t("dashboard.otherSub")}
        />
      </div>
    </section>
  );
}

function CompletenessLine({ completeness }: { completeness: TickMessage["estimateCompleteness"] }) {
  const { t } = useTranslation();
  return completeness === "partial" ? <span>{t("dashboard.lowerBound")}</span> : <span>{t(`completeness.${completeness}`)}</span>;
}

function SessionSection({ tick, quality }: { tick: TickMessage | null; quality: MonitoringQuality }) {
  const { t } = useTranslation();
  const nav = useNav();
  const idle = noReading(quality, t);
  const energy = sessionEnergy(tick);
  const cost = costVisibility(tick);

  const energyBadge: BadgeKind = energy.kind === "lowerBound" ? "partial" : energy.kind === "complete" ? "estimated" : "unavailable";

  return (
    <section className="flex flex-col gap-2" aria-labelledby="session-now">
      <h2 id="session-now" className="text-sm font-semibold text-ink-secondary">
        {t("dashboard.sections.session")}
      </h2>
      <div className="grid grid-cols-[repeat(auto-fit,minmax(12rem,1fr))] gap-3">
        <StatCard label={t("terms.monitoringSession")} value={<Num>{tick ? formatDuration(tick.sessionSeconds) : "--"}</Num>} sub={tick ? t("dashboard.sessionDurationSub") : idle} />
        <StatCard
          label={t("terms.energy")}
          badge={energyBadge}
          value={<Num>{energy.kind === "none" ? "--" : `${energy.kind === "lowerBound" ? "≥ " : ""}${formatKwhValue(energy.kwh)}`}</Num>}
          sub={energy.kind === "lowerBound" ? t("dashboard.lowerBound") : energy.kind === "none" ? idle : undefined}
        />
        <StatCard
          label={t("terms.cost")}
          badge={cost === "complete" ? "estimated" : "unavailable"}
          value={<Num>{cost === "complete" && tick?.sessionCostApprox != null ? formatMoney(tick.sessionCostApprox, tick.currency ?? "EUR", 4) : "--"}</Num>}
          sub={
            cost === "complete" ? (
              t("dashboard.approxCost")
            ) : cost === "withheldPartial" ? (
              t("dashboard.costWithheld")
            ) : cost === "noPrice" ? (
              <button type="button" className="text-accent-blue underline" onClick={() => nav.navigate("settings", "electricity")}>
                {t("dashboard.setPrice")}
              </button>
            ) : (
              idle
            )
          }
        />
      </div>
      {energy.kind === "lowerBound" && tick && (
        <p className="text-xs text-ink-muted">
          {tick.sessionKwh > 0 ? t("dashboard.partialEnergyNote", { complete: formatEnergy(tick.sessionKwh * 3_600_000) }) : t("dashboard.partialEnergyNoteNone")}
        </p>
      )}
    </section>
  );
}

function SensorSection({ tick }: { tick: TickMessage | null }) {
  const { t } = useTranslation();
  const rows: { label: string; state: ReadingState | undefined }[] = [
    { label: t("terms.cpuPower"), state: tick?.cpuState },
    { label: t("terms.gpuPower"), state: tick?.gpuState },
  ];
  const needsAdmin = tick?.cpuState === "implausibleZero" && !tick.elevated;

  return (
    <section className="card flex flex-col gap-3" aria-labelledby="sensor-status">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="sensor-status" className="text-sm font-semibold">
          {t("dashboard.sections.sensors")}
        </h2>
        {tick && <CompletenessBadge completeness={tick.estimateCompleteness} />}
      </div>
      <ul className="flex flex-col divide-y divide-surface-border/60 text-sm">
        {rows.map((row) => (
          <li key={row.label} className="flex flex-wrap items-center justify-between gap-2 py-1.5">
            <span>{row.label}</span>
            <span className="flex items-center gap-2 text-xs text-ink-secondary">
              {/* A working sensor is just MEASURED; only a missing one needs its reason spelled out. */}
              {row.state === undefined ? t("dashboard.noReading.stopped") : isValid(row.state) ? null : t(`readingState.${row.state}`)}
              <Badge kind={isValid(row.state) ? "measured" : "unavailable"} />
            </span>
          </li>
        ))}
      </ul>
      {needsAdmin && <Notice tone="info">{t("dashboard.cpuNeedsAdmin")}</Notice>}
    </section>
  );
}
