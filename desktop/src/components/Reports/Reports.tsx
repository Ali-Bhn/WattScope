import { useEffect, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useSidecarRequest } from "@/hooks/useSidecar";
import { Num } from "@/components/Num";
import { formatDuration, formatKwh, formatLocalDateTime, formatMoney } from "@/lib/format";
import { ReportView } from "./ReportView";
import type { DailyEnergy, SessionListEntry, SummaryResponse } from "@/lib/types";

type Tab = "current" | "previous" | "monthly";

export function Reports() {
  const { t } = useTranslation();
  const [tab, setTab] = useState<Tab>("current");

  return (
    <div className="flex flex-col gap-4">
      <div className="flex gap-2">
        <TabButton active={tab === "current"} onClick={() => setTab("current")}>
          {t("reports.currentSession")}
        </TabButton>
        <TabButton active={tab === "previous"} onClick={() => setTab("previous")}>
          {t("reports.previousSessions")}
        </TabButton>
        <TabButton active={tab === "monthly"} onClick={() => setTab("monthly")}>
          {t("reports.monthly")}
        </TabButton>
      </div>

      {tab === "current" && <CurrentSessionTab />}
      {tab === "previous" && <PreviousSessionsTab />}
      {tab === "monthly" && <MonthlyTab />}
    </div>
  );
}

function TabButton({ active, children, onClick }: { active: boolean; children: ReactNode; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
        active ? "bg-accent-green text-surface-0" : "bg-surface-2 text-ink-secondary hover:bg-surface-3"
      }`}
    >
      {children}
    </button>
  );
}

function CurrentSessionTab() {
  const request = useSidecarRequest();
  const [data, setData] = useState<SummaryResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const load = () =>
      request<SummaryResponse>({ cmd: "get_summary" })
        .then((d) => {
          if (!cancelled) {
            setData(d);
            setError(null);
          }
        })
        .catch((e) => {
          if (!cancelled) setError(String(e));
        });
    load();
    const interval = setInterval(load, 10_000); // reports reconcile every 10s flush, same cadence as the recorder
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
  }, [request]);

  if (error) return <ErrorBox message={error} />;
  if (!data) return <Loading />;
  return <ReportView report={data.report} />;
}

function PreviousSessionsTab() {
  const { t } = useTranslation();
  const request = useSidecarRequest();
  const [sessions, setSessions] = useState<SessionListEntry[] | null>(null);
  const [selected, setSelected] = useState<SummaryResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    request<SessionListEntry[]>({ cmd: "get_sessions" })
      .then(setSessions)
      .catch((e) => setError(String(e)));
  }, [request]);

  if (error) return <ErrorBox message={error} />;
  if (!sessions) return <Loading />;
  if (sessions.length === 0) return <p className="text-sm text-ink-secondary">{t("reports.noSessions")}</p>;

  return (
    <div className="flex flex-col gap-3">
      <div className="card overflow-x-auto p-0">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-surface-border text-ink-secondary">
              <th className="p-2 text-start">{t("reports.session")}</th>
              <th className="p-2 text-start">{t("reports.started")}</th>
              <th className="p-2 text-start">{t("reports.duration")}</th>
              <th className="p-2 text-start">{t("reports.energy")}</th>
            </tr>
          </thead>
          <tbody>
            {sessions.map((s) => (
              <tr
                key={s.id}
                className="cursor-pointer border-b border-surface-border/50 hover:bg-surface-2"
                onClick={() => request<SummaryResponse>({ cmd: "get_summary", sessionId: s.id }).then(setSelected)}
              >
                <td className="p-2">
                  #{s.id} <span className="text-ink-muted">({s.status})</span>
                </td>
                <td className="p-2">
                  <Num>{formatLocalDateTime(s.startedUtc)}</Num>
                </td>
                <td className="p-2">
                  <Num>{formatDuration(s.totals.monitoredSeconds)}</Num>
                </td>
                <td className="p-2">
                  <Num>{formatKwh(s.totals.estimatedCompleteJoules)}</Num>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {selected && <ReportView report={selected.report} />}
    </div>
  );
}

function MonthlyTab() {
  const { t } = useTranslation();
  const request = useSidecarRequest();
  const [month, setMonth] = useState(() => new Date().toISOString().slice(0, 7));
  const [data, setData] = useState<SummaryResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    request<SummaryResponse>({ cmd: "get_summary", month })
      .then((d) => {
        setData(d);
        setError(null);
      })
      .catch((e) => setError(String(e)));
  }, [request, month]);

  return (
    <div className="flex flex-col gap-3">
      <label className="flex items-center gap-2 text-sm">
        <span className="label">{t("reports.selectMonth")}</span>
        <input type="month" value={month} onChange={(e) => setMonth(e.target.value)} className="input" />
      </label>
      {error && <ErrorBox message={error} />}
      {!error && !data && <Loading />}
      {data && (
        <>
          <ReportView report={data.report} />
          {data.daily && <DailyTable days={data.daily} currency={data.report.cost.currency} />}
        </>
      )}
    </div>
  );
}

function DailyTable({ days, currency }: { days: DailyEnergy[]; currency: string }) {
  const { t } = useTranslation();
  return (
    <div className="card overflow-x-auto p-0">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-surface-border text-ink-secondary">
            <th className="p-2 text-start">{t("reports.daily")}</th>
            <th className="p-2 text-start">{t("reports.duration")}</th>
            <th className="p-2 text-start">{t("reports.energy")}</th>
            <th className="p-2 text-start">{t("reports.cost")}</th>
          </tr>
        </thead>
        <tbody>
          {days.map((d) => (
            <tr key={d.localDate} className="border-b border-surface-border/50">
              <td className="p-2">
                <Num>{d.localDate}</Num>
              </td>
              <td className="p-2">
                <Num>{formatDuration(d.totals.monitoredSeconds)}</Num>
              </td>
              <td className="p-2">
                <Num>{formatKwh(d.totals.estimatedCompleteJoules)}</Num>
              </td>
              <td className="p-2">
                <Num>
                  {d.cost.estimatedCostRounded != null ? formatMoney(d.cost.estimatedCostRounded, currency) : t("reports.costUnavailable")}
                </Num>
                {d.cost.precision !== "exact" && <span className="ms-1 text-amber-400">*</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Loading() {
  const { t } = useTranslation();
  return <p className="text-sm text-ink-secondary">{t("common.loading")}</p>;
}

function ErrorBox({ message }: { message: string }) {
  return <div className="card border-red-500/30 bg-red-500/10 text-sm text-red-300">{message}</div>;
}
