import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useMonitoring, useSidecarRequest } from "@/hooks/useSidecar";
import { Num } from "@/components/Num";
import { StatCard } from "@/components/Dashboard/StatCard";
import { formatDuration, formatKwh, formatLocalDateTime, formatMoney, formatWatts } from "@/lib/format";
import { COST_STATUS_KEYS, costDisplay, endLabelKey, isRunningSession, SOURCE_KEYS, unmonitoredSeconds, type CostDisplay } from "@/lib/gaming";
import type {
  GameAggregate,
  GameMapping,
  GameMappingKind,
  GamingLiveState,
  GamingSessionDetail,
  GamingSessionSummary,
  GamingStateResponse,
  RunningExecutable,
} from "@/lib/types";

/** Game names can be in any script (including Persian): isolate them from the surrounding text direction. */
function GameName({ children }: { children: ReactNode }) {
  return <bdi className="font-medium">{children}</bdi>;
}

function Cost({ display, t }: { display: CostDisplay; t: (key: string) => string }) {
  if (display.kind === "amount") return <Num>{formatMoney(display.amount, display.currency, 2)}</Num>;
  return <span className="text-ink-muted">{t("common.na")}</span>;
}

function ErrorBox({ message }: { message: string }) {
  return <div className="card border-red-500/30 bg-red-500/10 text-sm text-red-300">{message}</div>;
}

/**
 * The Gaming area. Everything here is a *view*: detection, the session state machine and the energy
 * attribution all live in the sidecar (docs/GAMING.md). The live card follows the same tick stream the
 * dashboard uses; history, per-game totals and mappings are plain requests.
 */
export function Gaming() {
  const { t } = useTranslation();
  const request = useSidecarRequest();
  const { tick } = useMonitoring();
  const [polled, setPolled] = useState<GamingStateResponse | null>(null);
  const [sessions, setSessions] = useState<GamingSessionSummary[] | null>(null);
  const [aggregates, setAggregates] = useState<GameAggregate[] | null>(null);
  const [mappings, setMappings] = useState<GameMapping[]>([]);
  const [error, setError] = useState<string | null>(null);

  // The tick stream is authoritative while monitoring runs; the polled state covers a quiet moment (and a candidate).
  const live: GamingLiveState | null = tick ? tick.gaming : (polled?.live ?? null);
  const liveSessionId = live?.sessionId ?? null;

  const loadHistory = useCallback(async () => {
    try {
      const [s, a, m] = await Promise.all([
        request<GamingSessionSummary[]>({ cmd: "get_gaming_sessions", limit: 100 }),
        request<GameAggregate[]>({ cmd: "get_game_aggregates" }),
        request<GameMapping[]>({ cmd: "get_game_mappings" }),
      ]);
      setSessions(s);
      setAggregates(a);
      setMappings(m);
      setError(null);
    } catch (e) {
      setError(String(e));
    }
  }, [request]);

  // Slow poll (5 s) only for what ticks do not carry; history reloads when a session starts or ends.
  useEffect(() => {
    let cancelled = false;
    const poll = () =>
      request<GamingStateResponse>({ cmd: "get_gaming_state" })
        .then((s) => !cancelled && setPolled(s))
        .catch(() => undefined);
    void poll();
    const id = setInterval(poll, 5000);
    return () => {
      cancelled = true;
      clearInterval(id);
    };
  }, [request]);

  const previousSession = useRef<number | null>(null);
  useEffect(() => {
    if (previousSession.current !== liveSessionId) {
      previousSession.current = liveSessionId;
      void loadHistory();
    }
  }, [liveSessionId, loadHistory]);
  useEffect(() => {
    void loadHistory();
  }, [loadHistory]);

  return (
    <div className="flex flex-col gap-5">
      <h1 className="text-lg font-semibold">{t("gaming.title")}</h1>
      {error && <ErrorBox message={error} />}

      <LiveSection live={live} tick={tick} monitoring={polled?.monitoring ?? !!tick} onChanged={loadHistory} />
      <HistorySection sessions={sessions} onChanged={loadHistory} />
      <AggregatesSection aggregates={aggregates} />
      <GamesSection mappings={mappings} onChanged={loadHistory} />
    </div>
  );
}

// ---------------------------------------------------------------- live

function LiveSection({
  live,
  tick,
  monitoring,
  onChanged,
}: {
  live: GamingLiveState | null;
  tick: ReturnType<typeof useMonitoring>["tick"];
  monitoring: boolean;
  onChanged: () => void;
}) {
  const { t } = useTranslation();

  if (!live || !isRunningSession(live)) {
    return (
      <div className="flex flex-col gap-3">
        <div className="card flex flex-col gap-1">
          <span className="text-base font-semibold">{t("gaming.noGame")}</span>
          {live?.state === "candidate" ? (
            <span className="text-sm text-ink-secondary">
              {t("gaming.detecting")} <GameName>{live.gameName}</GameName>
            </span>
          ) : (
            <span className="text-sm text-ink-secondary">{monitoring ? t("gaming.noGameHelp") : t("gaming.needsMonitoring")}</span>
          )}
        </div>
        <ManualControl live={live} monitoring={monitoring} onChanged={onChanged} />
      </div>
    );
  }

  const cost = costDisplay(live.costStatus, live.cost, live.currency);
  const partial = live.completeness !== "complete";
  const cpuMissing = !tick || tick.cpuState !== "valid";
  const unmonitored = unmonitoredSeconds(live.durationSeconds, live.monitoredSeconds);

  return (
    <div className="flex flex-col gap-3">
      <div className="card flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex flex-col">
            <span className="label">{t("gaming.sessionTitle")}</span>
            <span className="text-xl">
              <GameName>{live.gameName}</GameName>
            </span>
          </div>
          <div className="flex items-center gap-2 text-[11px] font-semibold">
            <span className="rounded bg-surface-3 px-2 py-0.5 text-ink-secondary">{live.isManual ? t("gaming.manualBadge") : t("gaming.autoBadge")}</span>
            <span className={`rounded px-2 py-0.5 ${live.state === "ending" ? "bg-amber-500/15 text-amber-400" : "bg-accent-green/15 text-accent-green"}`}>
              {live.state === "ending" ? t("gaming.ending") : t("gaming.live")}
            </span>
          </div>
        </div>

        {partial && <div className="rounded-md border border-amber-500/30 bg-amber-500/10 p-2 text-xs text-amber-300">{t("gaming.partialBanner")}</div>}

        <div className="grid grid-cols-2 gap-3 md:grid-cols-3">
          <StatCard
            label={t("gaming.duration")}
            value={<Num>{formatDuration(live.durationSeconds)}</Num>}
            sub={unmonitored > 1 ? `${t("gaming.monitored")}: ${formatDuration(live.monitoredSeconds)}` : undefined}
          />
          <StatCard
            label={t("gaming.gpuPower")}
            value={<Num>{tick && tick.gpuState !== "valid" ? "--" : formatWatts(tick?.gpuWatts ?? null, 0)}</Num>}
            badge={{ text: t("dashboard.measuredBadge"), tone: "measured" }}
          />
          <StatCard
            label={t("gaming.cpuPower")}
            value={<Num>{cpuMissing ? "--" : formatWatts(tick?.cpuWatts ?? null, 0)}</Num>}
            badge={{ text: t("dashboard.measuredBadge"), tone: "measured" }}
            sub={cpuMissing && tick ? t("gaming.cpuUnavailable") : undefined}
          />
          <StatCard
            label={t("gaming.systemPower")}
            value={<Num>{formatWatts(tick?.estimatedTotalWatts ?? null, 0)}</Num>}
            badge={{ text: t("dashboard.estimatedBadge"), tone: "estimated" }}
            sub={tick ? t(`dashboard.completeness.${tick.estimateCompleteness}`) : undefined}
          />
          <StatCard
            label={t("gaming.energy")}
            value={
              <Num>
                {live.systemIsLowerBound ? "≥ " : ""}
                {formatKwh(live.systemKwh * 3_600_000)}
              </Num>
            }
            badge={{ text: t("dashboard.estimatedBadge"), tone: "estimated" }}
            sub={live.systemIsLowerBound ? t("gaming.lowerBound") : undefined}
          />
          <StatCard
            label={t("gaming.cost")}
            value={<Cost display={cost} t={t} />}
            sub={cost.kind === "amount" ? t("dashboard.approxCost") : live.costStatus === "priced" ? undefined : t(COST_STATUS_KEYS[live.costStatus as keyof typeof COST_STATUS_KEYS])}
          />
        </div>
      </div>
      <ManualControl live={live} monitoring={monitoring} onChanged={onChanged} />
    </div>
  );
}

/** Fallback for when automatic detection misses a game: start/stop a session by hand. Obeys the same one-active-session rule (the sidecar enforces it). */
function ManualControl({ live, monitoring, onChanged }: { live: GamingLiveState | null; monitoring: boolean; onChanged: () => void }) {
  const { t } = useTranslation();
  const request = useSidecarRequest();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [exe, setExe] = useState("");
  const [remember, setRemember] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const manualRunning = !!live && live.isManual && live.state !== "candidate";
  const gameRunning = !!live && live.state !== "candidate";

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await fn();
      onChanged();
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  };

  if (manualRunning) {
    return (
      <div className="flex items-center gap-3">
        <button type="button" className="btn" disabled={busy} onClick={() => run(() => request({ cmd: "stop_manual_gaming" }))}>
          {t("gaming.manual.stop")}
        </button>
        {error && <span className="text-xs text-red-300">{error}</span>}
      </div>
    );
  }
  if (gameRunning) return null; // one active gaming session at a time

  return (
    <div className="flex flex-col gap-2">
      {!open ? (
        <div>
          <button type="button" className="btn" disabled={!monitoring} onClick={() => setOpen(true)} title={monitoring ? undefined : t("gaming.manual.requiresMonitoring")}>
            {t("gaming.manual.start")}
          </button>
        </div>
      ) : (
        <div className="card flex flex-col gap-3">
          <p className="text-xs text-ink-secondary">{t("gaming.manual.help")}</p>
          <div className="flex flex-wrap items-end gap-3">
            <label className="flex flex-col gap-1 text-sm">
              <span className="label">{t("gaming.manual.name")}</span>
              <input className="input" value={name} maxLength={80} onChange={(e) => setName(e.target.value)} />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="label">{t("gaming.manual.exe")}</span>
              <input className="input" dir="ltr" value={exe} placeholder="game.exe" onChange={(e) => setExe(e.target.value)} />
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={remember} disabled={!exe.trim()} onChange={(e) => setRemember(e.target.checked)} />
              <span>{t("gaming.manual.remember")}</span>
            </label>
          </div>
          <div className="flex gap-2">
            <button
              type="button"
              className="btn btn-primary"
              disabled={busy || !name.trim()}
              onClick={() =>
                run(async () => {
                  await request({ cmd: "start_manual_gaming", displayName: name.trim(), exeName: exe.trim() || undefined, rememberAsGame: remember });
                  setOpen(false);
                  setName("");
                  setExe("");
                })
              }
            >
              {t("gaming.manual.begin")}
            </button>
            <button type="button" className="btn" onClick={() => setOpen(false)}>
              {t("common.close")}
            </button>
          </div>
          {error && <span className="text-xs text-red-300">{error}</span>}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- history

function HistorySection({ sessions, onChanged }: { sessions: GamingSessionSummary[] | null; onChanged: () => void }) {
  const { t } = useTranslation();
  const request = useSidecarRequest();
  const [detail, setDetail] = useState<GamingSessionDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  const open = (id: number) =>
    request<GamingSessionDetail>({ cmd: "get_gaming_session", gamingSessionId: id })
      .then((d) => {
        setDetail(d);
        setError(null);
      })
      .catch((e) => setError(String(e)));

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-sm font-semibold">{t("gaming.history.title")}</h2>
      {error && <ErrorBox message={error} />}
      {!sessions ? (
        <p className="text-sm text-ink-secondary">{t("common.loading")}</p>
      ) : sessions.length === 0 ? (
        <p className="text-sm text-ink-secondary">{t("gaming.history.none")}</p>
      ) : (
        <div className="card overflow-x-auto p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-surface-border text-ink-secondary">
                <th className="p-2 text-start">{t("gaming.history.game")}</th>
                <th className="p-2 text-start">{t("gaming.history.start")}</th>
                <th className="p-2 text-start">{t("gaming.history.duration")}</th>
                <th className="p-2 text-start">{t("gaming.history.monitored")}</th>
                <th className="p-2 text-start">{t("gaming.history.energy")}</th>
                <th className="p-2 text-start">{t("gaming.history.cost")}</th>
                <th className="p-2 text-start">{t("gaming.history.completeness")}</th>
              </tr>
            </thead>
            <tbody>
              {sessions.map((s) => (
                <tr key={s.id} className="cursor-pointer border-b border-surface-border/50 hover:bg-surface-2" onClick={() => void open(s.id)}>
                  <td className="p-2">
                    <GameName>{s.displayName}</GameName>
                    {s.isManual && <span className="ms-2 rounded bg-surface-3 px-1.5 py-0.5 text-[10px] text-ink-secondary">{t("gaming.manualBadge")}</span>}
                  </td>
                  <td className="p-2">
                    <Num>{formatLocalDateTime(s.startedUtc)}</Num>
                  </td>
                  <td className="p-2">
                    <Num>{formatDuration(s.durationSeconds)}</Num>
                  </td>
                  <td className="p-2">
                    <Num>{formatDuration(s.monitoredSeconds)}</Num>
                  </td>
                  <td className="p-2">
                    <Num>
                      {s.systemIsLowerBound && s.systemJoules > 0 ? "≥ " : ""}
                      {s.completeness === "none" ? "--" : formatKwh(s.systemJoules)}
                    </Num>
                  </td>
                  <td className="p-2">
                    <Cost display={costDisplay(s.costStatus, s.cost, s.currency)} t={t} />
                    {s.costPrecision !== "exact" && s.costStatus === "priced" && <span className="ms-1 text-amber-400">*</span>}
                  </td>
                  <td className="p-2">{t(`dashboard.completeness.${s.completeness}`)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {detail && <SessionDetail detail={detail} onClose={() => setDetail(null)} onChanged={onChanged} />}
    </section>
  );
}

function SessionDetail({ detail, onClose, onChanged }: { detail: GamingSessionDetail; onClose: () => void; onChanged: () => void }) {
  const { t } = useTranslation();
  const s = detail.summary;
  const unmonitored = unmonitoredSeconds(s.durationSeconds, s.monitoredSeconds);
  const lb = s.systemIsLowerBound;
  const cost = costDisplay(s.costStatus, s.cost, s.currency);

  const Row = ({ label, children }: { label: string; children: ReactNode }) => (
    <div className="flex items-baseline justify-between gap-4 border-b border-surface-border/40 py-1 text-sm">
      <span className="text-ink-secondary">{label}</span>
      <span className="text-end">{children}</span>
    </div>
  );

  return (
    <div className="card flex flex-col gap-3">
      <div className="flex items-center justify-between">
        <span className="text-base">
          <GameName>{s.displayName}</GameName> <Num className="text-ink-muted">#{s.id}</Num>
        </span>
        <button type="button" className="btn" onClick={onClose}>
          {t("common.close")}
        </button>
      </div>

      {lb && <div className="rounded-md border border-amber-500/30 bg-amber-500/10 p-2 text-xs text-amber-300">{t("gaming.history.lowerBoundNote")}</div>}

      <div className="grid gap-x-8 md:grid-cols-2">
        <div>
          <Row label={t("gaming.history.start")}>
            <Num>{formatLocalDateTime(s.startedUtc)}</Num>
          </Row>
          <Row label={t("gaming.history.end")}>
            <Num>{s.endedUtc ? formatLocalDateTime(s.endedUtc) : "--"}</Num>
          </Row>
          <Row label={t("gaming.history.duration")}>
            <Num>{formatDuration(s.durationSeconds)}</Num>
          </Row>
          <Row label={t("gaming.history.monitored")}>
            <Num>{formatDuration(s.monitoredSeconds)}</Num>
          </Row>
          <Row label={t("gaming.history.unmonitored")}>
            <Num>{formatDuration(unmonitored)}</Num>
          </Row>
          <Row label={t("gaming.history.gaps")}>
            <Num>{detail.gaps.count}</Num>
          </Row>
          <Row label={t("gaming.history.endReason")}>{t(endLabelKey(s))}</Row>
          <Row label={t("gaming.history.source")}>{t(SOURCE_KEYS[s.source])}</Row>
          <Row label={t("gaming.history.executable")}>
            <Num>{detail.exePath ?? s.exeName}</Num>
          </Row>
        </div>
        <div>
          <Row label={t("gaming.history.energy")}>
            <Num>{s.completeness === "none" ? "--" : `${lb ? "≥ " : ""}${formatKwh(s.systemJoules)}`}</Num>
          </Row>
          <Row label={t("gaming.history.cost")}>
            <Cost display={cost} t={t} />
          </Row>
          {s.costStatus === "withheldPartial" && s.lowerBoundCost !== null && (
            <Row label={t("gaming.history.lowerBoundCost")}>
              <Num>≥ {formatMoney(s.lowerBoundCost, s.currency ?? "EUR", 2)}</Num>
            </Row>
          )}
          {s.costStatus !== "priced" && <Row label={t("gaming.history.costStatus")}>{t(COST_STATUS_KEYS[s.costStatus as keyof typeof COST_STATUS_KEYS])}</Row>}
          <Row label={t("gaming.history.avgCpu")}>
            <Num>{s.avgCpuWatts === null ? "--" : formatWatts(s.avgCpuWatts)}</Num>
          </Row>
          <Row label={t("gaming.history.avgGpu")}>
            <Num>{s.avgGpuWatts === null ? "--" : formatWatts(s.avgGpuWatts)}</Num>
          </Row>
          <Row label={t("gaming.history.avgSystem")}>
            <Num>{s.avgSystemWatts === null ? "--" : `${lb ? "≥ " : ""}${formatWatts(s.avgSystemWatts)}`}</Num>
          </Row>
          <Row label={t("gaming.history.peakCpu")}>
            <Num>{s.peakCpuWatts === null ? "--" : formatWatts(s.peakCpuWatts)}</Num>
          </Row>
          <Row label={t("gaming.history.peakGpu")}>
            <Num>{s.peakGpuWatts === null ? "--" : formatWatts(s.peakGpuWatts)}</Num>
          </Row>
          <Row label={t("gaming.history.peakSystem")}>
            <Num>{s.peakSystemWatts === null ? "--" : `${lb ? "≥ " : ""}${formatWatts(s.peakSystemWatts)}`}</Num>
          </Row>
          <Row label={t("gaming.history.completeness")}>{t(`dashboard.completeness.${s.completeness}`)}</Row>
        </div>
      </div>
      {s.costPrecision !== "exact" && <p className="text-xs text-amber-300">{t("gaming.history.approxPrice")}</p>}
      <GameActions summary={s} onChanged={() => { onChanged(); onClose(); }} />
    </div>
  );
}

/** Manual correction from a session: rename the game everywhere, or never detect this executable again. */
function GameActions({ summary, onChanged }: { summary: GamingSessionSummary; onChanged: () => void }) {
  const { t } = useTranslation();
  const request = useSidecarRequest();
  const [name, setName] = useState(summary.displayName);
  const [error, setError] = useState<string | null>(null);
  const hasExecutable = !summary.gameKey.startsWith("manual:");

  const save = (kind: GameMappingKind) =>
    request({ cmd: "set_game_mapping", exeName: summary.exeName, mappingKind: kind, displayName: kind === "game" ? name.trim() : undefined })
      .then(onChanged)
      .catch((e) => setError(String(e)));

  if (!hasExecutable) return null;
  return (
    <div className="flex flex-wrap items-end gap-2 border-t border-surface-border pt-3">
      <label className="flex flex-col gap-1 text-sm">
        <span className="label">{t("gaming.games.name")}</span>
        <input className="input" value={name} maxLength={80} onChange={(e) => setName(e.target.value)} />
      </label>
      <button type="button" className="btn" disabled={!name.trim()} onClick={() => void save("game")}>
        {t("gaming.games.rename")}
      </button>
      <button type="button" className="btn" onClick={() => void save("excluded")}>
        {t("gaming.games.exclude")}
      </button>
      {error && <span className="text-xs text-red-300">{error}</span>}
    </div>
  );
}

// ---------------------------------------------------------------- aggregates

function AggregatesSection({ aggregates }: { aggregates: GameAggregate[] | null }) {
  const { t } = useTranslation();
  if (!aggregates || aggregates.length === 0) return null;

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-sm font-semibold">{t("gaming.aggregates.title")}</h2>
      <p className="text-xs text-ink-secondary">{t("gaming.aggregates.help")}</p>
      <div className="card overflow-x-auto p-0">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-surface-border text-ink-secondary">
              <th className="p-2 text-start">{t("gaming.history.game")}</th>
              <th className="p-2 text-start">{t("gaming.aggregates.sessions")}</th>
              <th className="p-2 text-start">{t("gaming.aggregates.playTime")}</th>
              <th className="p-2 text-start">{t("gaming.aggregates.monitored")}</th>
              <th className="p-2 text-start">{t("gaming.aggregates.avgSession")}</th>
              <th className="p-2 text-start">{t("gaming.aggregates.complete")}</th>
              <th className="p-2 text-start">{t("gaming.aggregates.partial")}</th>
            </tr>
          </thead>
          <tbody>
            {aggregates.map((g) => (
              <tr key={g.gameKey} className="border-b border-surface-border/50 align-top">
                <td className="p-2">
                  <GameName>{g.displayName}</GameName>
                </td>
                <td className="p-2">
                  <Num>{g.sessionCount}</Num>
                </td>
                <td className="p-2">
                  <Num>{formatDuration(g.totalDurationSeconds)}</Num>
                </td>
                <td className="p-2">
                  <Num>{formatDuration(g.totalMonitoredSeconds)}</Num>
                </td>
                <td className="p-2">
                  <Num>{formatDuration(g.averageSessionDurationSeconds)}</Num>
                </td>
                <td className="p-2">
                  {g.complete.sessions === 0 ? (
                    <span className="text-ink-muted">--</span>
                  ) : (
                    <div className="flex flex-col">
                      <Num>{formatKwh(g.complete.systemJoules)}</Num>
                      <Num className="text-xs text-ink-secondary">{g.complete.avgSystemWatts === null ? "--" : formatWatts(g.complete.avgSystemWatts, 0)}</Num>
                      <span className="text-xs text-ink-secondary">
                        {g.complete.cost !== null ? <Num>{formatMoney(g.complete.cost, g.currency ?? "EUR", 2)}</Num> : t("common.na")}
                        {g.complete.unpricedSessions > 0 && ` (${t("gaming.aggregates.unpriced")}: ${g.complete.unpricedSessions})`}
                      </span>
                    </div>
                  )}
                </td>
                <td className="p-2">
                  {g.partial.sessions === 0 ? (
                    <span className="text-ink-muted">--</span>
                  ) : (
                    <div className="flex flex-col">
                      <Num>≥ {formatKwh(g.partial.systemJoules)}</Num>
                      <span className="text-xs text-ink-secondary">
                        {g.partial.sessions} {t("gaming.aggregates.sessionsShort")}, {t("gaming.aggregates.noCost")}
                      </span>
                    </div>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

// ---------------------------------------------------------------- games / mappings

function GamesSection({ mappings, onChanged }: { mappings: GameMapping[]; onChanged: () => void }) {
  const { t } = useTranslation();
  const request = useSidecarRequest();
  const [exe, setExe] = useState("");
  const [name, setName] = useState("");
  const [kind, setKind] = useState<GameMappingKind>("game");
  const [running, setRunning] = useState<RunningExecutable[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const pick = async () => {
    try {
      setRunning(await request<RunningExecutable[]>({ cmd: "get_running_executables" }));
    } catch (e) {
      setError(String(e));
    }
  };

  const add = async () => {
    try {
      await request({ cmd: "set_game_mapping", exeName: exe.trim(), mappingKind: kind, displayName: kind === "game" ? name.trim() || undefined : undefined });
      setExe("");
      setName("");
      setError(null);
      onChanged();
    } catch (e) {
      setError(String(e));
    }
  };

  const remove = (m: GameMapping) =>
    request({ cmd: "remove_game_mapping", exeName: m.exeName, exePath: m.exePath || undefined })
      .then(onChanged)
      .catch((e) => setError(String(e)));

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-sm font-semibold">{t("gaming.games.title")}</h2>
      <p className="text-xs text-ink-secondary">{t("gaming.games.help")}</p>

      <div className="card flex flex-col gap-3">
        <div className="flex flex-wrap items-end gap-3">
          <label className="flex flex-col gap-1 text-sm">
            <span className="label">{t("gaming.games.exe")}</span>
            <input className="input" dir="ltr" value={exe} placeholder="game.exe" onChange={(e) => setExe(e.target.value)} />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            <span className="label">{t("gaming.games.name")}</span>
            <input className="input" value={name} maxLength={80} disabled={kind === "excluded"} onChange={(e) => setName(e.target.value)} />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            <span className="label">{t("gaming.games.kind")}</span>
            <select className="input" value={kind} onChange={(e) => setKind(e.target.value as GameMappingKind)}>
              <option value="game">{t("gaming.games.kindGame")}</option>
              <option value="excluded">{t("gaming.games.kindExcluded")}</option>
            </select>
          </label>
          <button type="button" className="btn btn-primary" disabled={!exe.trim()} onClick={() => void add()}>
            {t("gaming.games.add")}
          </button>
          <button type="button" className="btn" onClick={() => void pick()}>
            {t("gaming.games.pick")}
          </button>
        </div>
        {running && (
          <div className="flex flex-wrap gap-2">
            {running.length === 0 && <span className="text-xs text-ink-secondary">{t("gaming.games.pickNone")}</span>}
            {running.map((r) => (
              <button
                key={r.exeName}
                type="button"
                className="rounded bg-surface-2 px-2 py-1 text-xs hover:bg-surface-3"
                dir="ltr"
                title={r.exePath ?? undefined}
                onClick={() => {
                  setExe(r.exeName);
                  setRunning(null);
                }}
              >
                {r.exeName}
              </button>
            ))}
          </div>
        )}
        {error && <span className="text-xs text-red-300">{error}</span>}
      </div>

      {mappings.length === 0 ? (
        <p className="text-sm text-ink-secondary">{t("gaming.games.empty")}</p>
      ) : (
        <div className="card overflow-x-auto p-0">
          <table className="w-full text-sm">
            <tbody>
              {mappings.map((m) => (
                <tr key={m.id} className="border-b border-surface-border/50">
                  <td className="p-2">
                    <Num>{m.exeName}</Num>
                  </td>
                  <td className="p-2">{m.kind === "game" ? t("gaming.games.kindGame") : t("gaming.games.kindExcluded")}</td>
                  <td className="p-2">{m.displayName ? <GameName>{m.displayName}</GameName> : <span className="text-ink-muted">--</span>}</td>
                  <td className="p-2 text-end">
                    <button type="button" className="btn" onClick={() => void remove(m)}>
                      {t("gaming.games.remove")}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="text-xs text-ink-muted">{t("gaming.games.privacyNote")}</p>
    </section>
  );
}
