import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@/hooks/useQuery";
import { useNav } from "@/hooks/useNav";
import { Num } from "@/components/Num";
import { CompletenessBadge } from "@/components/ui/Badge";
import { EmptyState, PageHeader } from "@/components/ui/States";
import { QueryView } from "@/components/ui/QueryView";
import { Tabs } from "@/components/ui/Tabs";
import { formatDayKey, formatDuration, formatKwh, formatLocalDateTime, formatMonthKey, formatMoney } from "@/lib/format";
import { isFutureDayKey, isFutureMonthKey, isValidDayKey, isValidMonthKey, localDayKey, localMonthKey, shiftDayKey, shiftMonthKey } from "@/lib/dates";
import { completenessOf, energyModel } from "@/lib/reports";
import { costDisplay } from "@/lib/gaming";
import type { DailyEnergy, EnergyTotals, GamingSessionSummary, SessionListEntry, SummaryResponse } from "@/lib/types";
import { ReportView } from "./ReportView";

type Tab = "today" | "day" | "month" | "sessions";

export function Reports() {
  const { t } = useTranslation();
  const [tab, setTab] = useState<Tab>("today");

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title={t("reports.title")} subtitle={t("reports.subtitle")} />
      <Tabs
        label={t("reports.title")}
        value={tab}
        onChange={setTab}
        tabs={[
          { id: "today", label: t("reports.tabs.today") },
          { id: "day", label: t("reports.tabs.day") },
          { id: "month", label: t("reports.tabs.month") },
          { id: "sessions", label: t("reports.tabs.sessions") },
        ]}
      >
        {tab === "today" && <TodayTab />}
        {tab === "day" && <DayTab />}
        {tab === "month" && <MonthTab />}
        {tab === "sessions" && <SessionsTab />}
      </Tabs>
    </div>
  );
}

// ---------------------------------------------------------------- day

/** A local day's report. Today refreshes on the recorder's flush cadence (while visible); a past day is final. */
function DayReport({ day, live }: { day: string; live: boolean }) {
  const { t } = useTranslation();
  const request = useMemo(() => ({ cmd: "get_summary", day }), [day]);
  const query = useQuery<SummaryResponse>(request, { pollMs: live ? 10_000 : undefined });
  const title = live ? t("reports.titles.today", { date: formatDayKey(day) }) : t("reports.titles.day", { date: formatDayKey(day) });

  return (
    <>
      <QueryView query={query}>{(data) => <ReportView report={data.report} title={title} />}</QueryView>
      <DayGaming day={day} live={live} />
    </>
  );
}

function TodayTab() {
  // Recomputed per render so a window left open past midnight moves on to the new day.
  return <DayReport day={localDayKey()} live />;
}

function DayTab() {
  const { t } = useTranslation();
  const [day, setDay] = useState(() => localDayKey());
  const valid = isValidDayKey(day);
  const today = localDayKey();

  return (
    <>
      <div className="flex flex-wrap items-end gap-2">
        <label className="flex flex-col gap-1 text-sm">
          <span className="label">{t("reports.selectDay")}</span>
          <input type="date" className="input" value={day} max={today} onChange={(e) => setDay(e.target.value)} />
        </label>
        <button type="button" className="btn" aria-label={t("reports.previousDay")} onClick={() => setDay((d) => shiftDayKey(d, -1))} disabled={!valid}>
          ‹ {t("reports.previous")}
        </button>
        <button type="button" className="btn" aria-label={t("reports.nextDay")} onClick={() => setDay((d) => shiftDayKey(d, 1))} disabled={!valid || isFutureDayKey(shiftDayKey(day, 1))}>
          {t("reports.next")} ›
        </button>
        <button type="button" className="btn btn-quiet" onClick={() => setDay(today)} disabled={day === today}>
          {t("reports.tabs.today")}
        </button>
      </div>
      {valid ? <DayReport day={day} live={day === today} /> : <EmptyState title={t("reports.invalidDate")} />}
    </>
  );
}

/** Gaming sessions that started on this local day: listed, never summed into the energy report (they are a subset of it, and Partial/Complete are not merged). */
function DayGaming({ day, live }: { day: string; live: boolean }) {
  const { t } = useTranslation();
  const nav = useNav();
  const request = useMemo(() => ({ cmd: "get_gaming_sessions", day }), [day]);
  const query = useQuery<GamingSessionSummary[]>(request, { pollMs: live ? 30_000 : undefined });

  return (
    <QueryView query={query}>
      {(sessions) =>
        sessions.length === 0 ? null : (
          <section className="flex flex-col gap-2" aria-labelledby="day-gaming">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 id="day-gaming" className="text-sm font-semibold">
                {t("reports.gamingOnDay")}
              </h2>
              <button type="button" className="btn btn-quiet" onClick={() => nav.navigate("gaming")}>
                {t("reports.openGaming")}
              </button>
            </div>
            <div className="card overflow-x-auto p-0">
              <table className="table">
                <thead>
                  <tr>
                    <th>{t("gaming.history.game")}</th>
                    <th>{t("gaming.history.start")}</th>
                    <th>{t("gaming.history.duration")}</th>
                    <th>{t("gaming.history.energy")}</th>
                    <th>{t("gaming.history.cost")}</th>
                    <th>{t("gaming.history.completeness")}</th>
                  </tr>
                </thead>
                <tbody>
                  {sessions.map((s) => {
                    const cost = costDisplay(s.costStatus, s.cost, s.currency);
                    return (
                      <tr key={s.id}>
                        <td>
                          <bdi className="font-medium">{s.displayName}</bdi>
                        </td>
                        <td>
                          <Num>{formatLocalDateTime(s.startedUtc)}</Num>
                        </td>
                        <td>
                          <Num>{formatDuration(s.durationSeconds)}</Num>
                        </td>
                        <td>
                          <Num>{s.completeness === "none" ? "--" : `${s.systemIsLowerBound ? "≥ " : ""}${formatKwh(s.systemJoules)}`}</Num>
                        </td>
                        <td>
                          <Num>{cost.kind === "amount" ? formatMoney(cost.amount, cost.currency) : t("common.na")}</Num>
                        </td>
                        <td>
                          <CompletenessBadge completeness={s.completeness} />
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </section>
        )
      }
    </QueryView>
  );
}

// ---------------------------------------------------------------- month

function MonthTab() {
  const { t } = useTranslation();
  const [month, setMonth] = useState(() => localMonthKey());
  const valid = isValidMonthKey(month);
  const request = useMemo(() => (valid ? { cmd: "get_summary", month } : null), [valid, month]);
  const query = useQuery<SummaryResponse>(request, { pollMs: month === localMonthKey() ? 30_000 : undefined });
  const current = localMonthKey();

  return (
    <>
      <div className="flex flex-wrap items-end gap-2">
        <label className="flex flex-col gap-1 text-sm">
          <span className="label">{t("reports.selectMonth")}</span>
          <input type="month" className="input" value={month} max={current} onChange={(e) => setMonth(e.target.value)} />
        </label>
        <button type="button" className="btn" onClick={() => setMonth((m) => shiftMonthKey(m, -1))} disabled={!valid} aria-label={t("reports.previousMonth")}>
          ‹ {t("reports.previous")}
        </button>
        <button type="button" className="btn" onClick={() => setMonth((m) => shiftMonthKey(m, 1))} disabled={!valid || isFutureMonthKey(shiftMonthKey(month, 1))} aria-label={t("reports.nextMonth")}>
          {t("reports.next")} ›
        </button>
        <button type="button" className="btn btn-quiet" onClick={() => setMonth(current)} disabled={month === current}>
          {t("reports.thisMonth")}
        </button>
      </div>
      {!valid ? (
        <EmptyState title={t("reports.invalidDate")} />
      ) : (
        <QueryView query={query}>
          {(data) => (
            <>
              <ReportView report={data.report} title={t("reports.titles.month", { month: formatMonthKey(month) })} />
              {data.daily && data.daily.length > 0 && <DailyTable days={data.daily} currency={data.report.cost.currency} />}
            </>
          )}
        </QueryView>
      )}
    </>
  );
}

function EnergyCells({ totals }: { totals: EnergyTotals }) {
  const e = energyModel(totals);
  return (
    <>
      <td>
        <Num>{e.kind === "complete" || e.kind === "mixed" ? formatKwh(e.completeJoules) : "--"}</Num>
      </td>
      <td>
        <Num>{e.kind === "partial" || e.kind === "mixed" ? `≥ ${formatKwh(e.partialJoules)}` : "--"}</Num>
      </td>
    </>
  );
}

function DailyTable({ days, currency }: { days: DailyEnergy[]; currency: string }) {
  const { t } = useTranslation();
  const anyApproximate = days.some((d) => d.cost.precision !== "exact");
  return (
    <section className="flex flex-col gap-2" aria-labelledby="daily-title">
      <h2 id="daily-title" className="text-sm font-semibold">
        {t("reports.daily")}
      </h2>
      <div className="card overflow-x-auto p-0">
        <table className="table">
          <thead>
            <tr>
              <th>{t("reports.date")}</th>
              <th>{t("reports.monitoredTime")}</th>
              <th>{t("reports.completeEnergy")}</th>
              <th>{t("reports.partialEnergy")}</th>
              <th>{t("terms.cost")}</th>
              <th>{t("reports.completeness")}</th>
            </tr>
          </thead>
          <tbody>
            {days.map((d) => (
              <tr key={d.localDate}>
                <td>
                  <Num>{formatDayKey(d.localDate)}</Num>
                </td>
                <td>
                  <Num>{formatDuration(d.totals.monitoredSeconds)}</Num>
                </td>
                <EnergyCells totals={d.totals} />
                <td>
                  <Num>{d.cost.estimatedCostRounded != null ? formatMoney(d.cost.estimatedCostRounded, currency) : "--"}</Num>
                  {d.cost.precision !== "exact" && (
                    <span className="ms-1 text-amber-300" aria-label={t("reports.approximate")} title={t("reports.approximate")}>
                      *
                    </span>
                  )}
                </td>
                <td>
                  <CompletenessBadge completeness={completenessOf(d.totals)} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="text-xs text-ink-muted">{t("reports.dailyNote")}</p>
      {anyApproximate && <p className="text-xs text-amber-300">* {t("reports.approxNotice")}</p>}
    </section>
  );
}

// ---------------------------------------------------------------- monitoring sessions

function SessionsTab() {
  const { t } = useTranslation();
  const query = useQuery<SessionListEntry[]>({ cmd: "get_sessions" }, { pollMs: 10_000 });
  const [selected, setSelected] = useState<number | null>(null);

  return (
    <QueryView query={query}>
      {(sessions) =>
        sessions.length === 0 ? (
          <EmptyState title={t("reports.noSessions")}>{t("reports.noSessionsHelp")}</EmptyState>
        ) : (
          <>
            <div className="card overflow-x-auto p-0">
              <table className="table">
                <thead>
                  <tr>
                    <th>{t("terms.monitoringSession")}</th>
                    <th>{t("reports.started")}</th>
                    <th>{t("reports.monitoredTime")}</th>
                    <th>{t("reports.completeEnergy")}</th>
                    <th>{t("reports.partialEnergy")}</th>
                    <th>{t("reports.completeness")}</th>
                  </tr>
                </thead>
                <tbody>
                  {sessions.map((s) => (
                    <tr key={s.id} className={s.id === selected ? "bg-surface-2" : "hover:bg-surface-2/60"}>
                      <td>
                        <button type="button" className="text-start font-medium text-accent-blue underline-offset-2 hover:underline" aria-pressed={s.id === selected} onClick={() => setSelected(s.id === selected ? null : s.id)}>
                          #{s.id}
                        </button>{" "}
                        <span className="text-xs text-ink-muted">{t(`reports.status.${s.status}`)}</span>
                      </td>
                      <td>
                        <Num>{formatLocalDateTime(s.startedUtc)}</Num>
                      </td>
                      <td>
                        <Num>{formatDuration(s.totals.monitoredSeconds)}</Num>
                      </td>
                      <EnergyCells totals={s.totals} />
                      <td>
                        <CompletenessBadge completeness={completenessOf(s.totals)} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="text-xs text-ink-muted">{t("reports.sessionsNote")}</p>
            {selected !== null && <SessionDetail id={selected} />}
          </>
        )
      }
    </QueryView>
  );
}

function SessionDetail({ id }: { id: number }) {
  const { t } = useTranslation();
  const request = useMemo(() => ({ cmd: "get_summary", sessionId: id }), [id]);
  const query = useQuery<SummaryResponse>(request);
  return (
    <QueryView query={query}>
      {(data) => (
        <ReportView report={data.report} title={t("reports.titles.session", { id })}>
          {data.session?.endReason && (
            <details className="text-xs text-ink-muted">
              <summary className="cursor-pointer select-none">{t("errors.details")}</summary>
              <p lang="en" dir="ltr" className="mt-1">
                {data.session.endReason}
              </p>
            </details>
          )}
        </ReportView>
      )}
    </QueryView>
  );
}
