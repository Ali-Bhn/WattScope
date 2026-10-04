import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@/hooks/useQuery";
import { sidecarRequest, useGamingSessionId, useIsRecording, useMonitoringActions, useTick, useUiVisible } from "@/hooks/useSidecar";
import { Num } from "@/components/Num";
import { ActionButton } from "@/components/ui/ActionButton";
import { Badge, CompletenessBadge } from "@/components/ui/Badge";
import { EmptyState, ErrorPanel, Loading, PageHeader } from "@/components/ui/States";
import { Notice } from "@/components/ui/Notice";
import { StatCard } from "@/components/ui/StatCard";
import { formatDuration, formatEnergy, formatKwh, formatLocalDateTime, formatMoney, formatWatts } from "@/lib/format";
import { COST_STATUS_KEYS, costDisplay, costText, endLabelKey, isRunningSession, SOURCE_KEYS, unmonitoredSeconds, type CostDisplay } from "@/lib/gaming";
import type { GameAggregate, GameMapping, GameMappingKind, GamingLiveState, GamingSessionDetail, GamingSessionSummary, GamingStateResponse, RunningExecutable, TickMessage } from "@/lib/types";

/** The newest N sessions are listed; older ones still count in the per-game totals below. */
const HISTORY_LIMIT = 100;

/** Game names can be in any script (including Persian): isolate them from the surrounding text direction. */
function GameName({ children }: { children: ReactNode }) {
  return <bdi className="font-medium">{children}</bdi>;
}

function Cost({ display }: { display: CostDisplay }) {
  return <Num className={display.kind === "amount" ? undefined : "text-ink-muted"}>{costText(display)}</Num>;
}

/**
 * The Gaming area. Everything here is a *view*: detection, the session state machine and the energy
 * attribution all live in the sidecar (docs/GAMING.md). The live card follows the same tick stream the
 * Dashboard uses (so there is no polling for it at all); history, per-game totals and mappings are plain
 * requests, reloaded when a gaming session starts or ends.
 */
export function Gaming() {
  const { t } = useTranslation();
  const liveSessionId = useGamingSessionId();
  const detection = useQuery<GamingStateResponse>({ cmd: "get_gaming_state" });

  const [sessions, setSessions] = useState<GamingSessionSummary[] | null>(null);
  const [aggregates, setAggregates] = useState<GameAggregate[] | null>(null);
  const [mappings, setMappings] = useState<GameMapping[] | null>(null);
  const [error, setError] = useState<unknown>(null);

  const loadHistory = useCallback(async () => {
    try {
      const [s, a, m] = await Promise.all([
        sidecarRequest<GamingSessionSummary[]>({ cmd: "get_gaming_sessions", limit: HISTORY_LIMIT }),
        sidecarRequest<GameAggregate[]>({ cmd: "get_game_aggregates" }),
        sidecarRequest<GameMapping[]>({ cmd: "get_game_mappings" }),
      ]);
      setSessions(s);
      setAggregates(a);
      setMappings(m);
      setError(null);
    } catch (e) {
      setError(e);
    }
  }, []);

  // Once on open, and again whenever a gaming session starts or ends (the live id changes).
  const previous = useRef<number | null | undefined>(undefined);
  useEffect(() => {
    if (previous.current !== liveSessionId) {
      previous.current = liveSessionId;
      void loadHistory();
    }
  }, [liveSessionId, loadHistory]);

  const detectionOff = detection.state.status === "ready" && !detection.state.data.available;

  return (
    <div className="flex flex-col gap-6">
      <PageHeader title={t("gaming.title")} subtitle={t("gaming.subtitle")} />
      {error !== null && <ErrorPanel error={error} onRetry={() => void loadHistory()} />}
      {detectionOff && (
        <Notice tone="warn" title={t("gaming.detectionOff")}>
          {t("gaming.detectionOffHelp")}
        </Notice>
      )}

      <LiveSection onChanged={loadHistory} />
      <HistorySection sessions={sessions} onChanged={loadHistory} />
      <AggregatesSection aggregates={aggregates} />
      <GamesSection mappings={mappings} onChanged={loadHistory} />
    </div>
  );
}

// ---------------------------------------------------------------- live

function LiveSection({ onChanged }: { onChanged: () => void }) {
  const { t } = useTranslation();
  const tick = useTick();
  const monitoring = useIsRecording();
  const { start } = useMonitoringActions();
  const live: GamingLiveState | null = tick?.gaming ?? null;

  if (!live || !isRunningSession(live)) {
    return (
      <div className="flex flex-col gap-3">
        <div className="card flex flex-col items-start gap-2">
          <h2 className="text-base font-semibold">{t("gaming.noGame")}</h2>
          {live?.state === "candidate" ? (
            <p className="text-sm text-ink-secondary">
              {t("gaming.detecting")} <GameName>{live.gameName}</GameName>
            </p>
          ) : monitoring ? (
            <p className="max-w-prose text-sm text-ink-secondary">{t("gaming.noGameHelp")}</p>
          ) : (
            <>
              <p className="max-w-prose text-sm text-ink-secondary">{t("gaming.needsMonitoring")}</p>
              <button type="button" className="btn btn-primary" onClick={() => void start().catch(() => undefined)}>
                {t("dashboard.startMonitoring")}
              </button>
            </>
          )}
        </div>
        <ManualControl live={live} monitoring={monitoring} onChanged={onChanged} />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      <LiveSession live={live} tick={tick} />
      <ManualControl live={live} monitoring={monitoring} onChanged={onChanged} />
    </div>
  );
}

function LiveSession({ live, tick }: { live: GamingLiveState; tick: TickMessage | null }) {
  const { t } = useTranslation();
  const cost = costDisplay(live.costStatus, live.cost, live.currency);
  const partial = live.completeness !== "complete";
  const cpuOk = !!tick && tick.cpuState === "valid";
  const gpuOk = !!tick && tick.gpuState === "valid";
  const unmonitored = unmonitoredSeconds(live.durationSeconds, live.monitoredSeconds);

  return (
    <section className="card flex flex-col gap-3" aria-labelledby="live-game">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex min-w-0 flex-col">
          <span className="label">{t("terms.gamingSession")}</span>
          <h2 id="live-game" className="text-xl">
            <GameName>{live.gameName}</GameName>
          </h2>
        </div>
        <div className="flex items-center gap-2 text-[11px] font-semibold">
          <span className="rounded bg-surface-3 px-2 py-0.5 text-ink-secondary">{live.isManual ? t("gaming.manualBadge") : t("gaming.autoBadge")}</span>
          <span className={`rounded px-2 py-0.5 ${live.state === "ending" ? "bg-amber-500/15 text-amber-300" : "bg-accent-green/15 text-accent-green"}`}>
            <span aria-hidden="true">{live.state === "ending" ? "◔ " : "● "}</span>
            {live.state === "ending" ? t("gaming.ending") : t("gaming.live")}
          </span>
        </div>
      </div>

      {partial && <Notice tone="warn">{t("gaming.partialBanner")}</Notice>}

      <div className="grid grid-cols-[repeat(auto-fit,minmax(12rem,1fr))] gap-3">
        <StatCard
          label={t("gaming.duration")}
          value={<Num>{formatDuration(live.durationSeconds)}</Num>}
          sub={unmonitored > 1 ? `${t("gaming.monitored")}: ${formatDuration(live.monitoredSeconds)}` : undefined}
        />
        <StatCard
          label={t("terms.gpuPower")}
          badge={gpuOk ? "measured" : "unavailable"}
          value={<Num>{gpuOk ? formatWatts(tick?.gpuWatts ?? null, 0) : "--"}</Num>}
        />
        <StatCard
          label={t("terms.cpuPower")}
          badge={cpuOk ? "measured" : "unavailable"}
          value={<Num>{cpuOk ? formatWatts(tick?.cpuWatts ?? null, 0) : "--"}</Num>}
          sub={!cpuOk && tick ? t("gaming.cpuUnavailable") : undefined}
        />
        <StatCard
          label={t("terms.systemPower")}
          badge={tick?.estimatedTotalWatts == null ? "unavailable" : tick.estimateCompleteness === "partial" ? "partial" : "estimated"}
          value={<Num>{tick?.estimatedTotalWatts == null ? "--" : `${tick.estimateCompleteness === "partial" ? "≥ " : ""}${formatWatts(tick.estimatedTotalWatts, 0)}`}</Num>}
          sub={tick ? t(`completeness.${tick.estimateCompleteness}`) : undefined}
        />
        <StatCard
          label={t("terms.energy")}
          badge={live.completeness === "none" ? "unavailable" : partial ? "partial" : "estimated"}
          value={<Num>{live.completeness === "none" ? "--" : `${live.systemIsLowerBound ? "≥ " : ""}${formatEnergy(live.systemKwh * 3_600_000)}`}</Num>}
          sub={live.systemIsLowerBound ? t("gaming.lowerBound") : undefined}
        />
        <StatCard
          label={t("terms.cost")}
          badge={cost.kind === "amount" ? "estimated" : "unavailable"}
          value={<Cost display={cost} />}
          sub={cost.kind === "amount" ? t("dashboard.approxCost") : live.costStatus === "priced" ? undefined : t(COST_STATUS_KEYS[live.costStatus as keyof typeof COST_STATUS_KEYS])}
        />
      </div>
    </section>
  );
}

/** Fallback for when automatic detection misses a game: start/stop a session by hand. Obeys the same one-active-session rule (the sidecar enforces it). */
function ManualControl({ live, monitoring, onChanged }: { live: GamingLiveState | null; monitoring: boolean; onChanged: () => void }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [exe, setExe] = useState("");
  const [remember, setRemember] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const manualRunning = !!live && live.isManual && live.state !== "candidate";
  const gameRunning = !!live && live.state !== "candidate";

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await fn();
      onChanged();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  if (manualRunning) {
    return (
      <div className="flex flex-col items-start gap-2">
        <ActionButton className="btn" inactive={busy} onClick={() => void run(() => sidecarRequest({ cmd: "stop_manual_gaming" }))}>
          {t("gaming.manual.stop")}
        </ActionButton>
        {error !== null && <ErrorPanel error={error} compact />}
      </div>
    );
  }
  if (gameRunning) return null; // one active gaming session at a time

  return (
    <div className="flex flex-col gap-2">
      {!open ? (
        <div className="flex flex-col items-start gap-1">
          <button type="button" className="btn" disabled={!monitoring} onClick={() => setOpen(true)} aria-describedby="manual-hint">
            {t("gaming.manual.start")}
          </button>
          <span id="manual-hint" className="max-w-prose text-xs text-ink-muted">
            {monitoring ? t("gaming.manual.hint") : t("gaming.manual.requiresMonitoring")}
          </span>
        </div>
      ) : (
        <form
          className="card flex flex-col gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            if (!name.trim() || busy) return;
            void run(async () => {
              await sidecarRequest({ cmd: "start_manual_gaming", displayName: name.trim(), exeName: exe.trim() || undefined, rememberAsGame: remember });
              setOpen(false);
              setName("");
              setExe("");
            });
          }}
        >
          <p className="max-w-prose text-sm text-ink-secondary">{t("gaming.manual.help")}</p>
          <div className="flex flex-wrap items-end gap-3">
            <label className="flex flex-col gap-1 text-sm">
              <span className="label">{t("gaming.manual.name")}</span>
              <input className="input" value={name} maxLength={80} onChange={(e) => setName(e.target.value)} required autoFocus />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="label">{t("gaming.manual.exe")}</span>
              <input className="input" dir="ltr" value={exe} placeholder="game.exe" onChange={(e) => setExe(e.target.value)} />
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={remember} disabled={!exe.trim()} onChange={(e) => setRemember(e.target.checked)} />
              <span className={exe.trim() ? "" : "text-ink-muted"}>{t("gaming.manual.remember")}</span>
            </label>
          </div>
          <div className="flex gap-2">
            <ActionButton type="submit" className="btn btn-primary" inactive={busy} disabled={!name.trim()}>
              {t("gaming.manual.begin")}
            </ActionButton>
            <button type="button" className="btn" onClick={() => setOpen(false)}>
              {t("common.cancel")}
            </button>
          </div>
          {error !== null && <ErrorPanel error={error} compact />}
        </form>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- history

function HistorySection({ sessions, onChanged }: { sessions: GamingSessionSummary[] | null; onChanged: () => void }) {
  const { t } = useTranslation();
  const [detail, setDetail] = useState<GamingSessionDetail | null>(null);
  const [error, setError] = useState<unknown>(null);

  const open = (id: number) =>
    sidecarRequest<GamingSessionDetail>({ cmd: "get_gaming_session", gamingSessionId: id }).then(
      (d) => {
        setDetail(d);
        setError(null);
      },
      (e) => setError(e),
    );

  return (
    <section className="flex flex-col gap-3" aria-labelledby="gaming-history">
      <h2 id="gaming-history" className="text-sm font-semibold">
        {t("gaming.history.title")}
      </h2>
      {error !== null && <ErrorPanel error={error} compact />}
      {!sessions ? (
        <Loading />
      ) : sessions.length === 0 ? (
        <EmptyState title={t("gaming.history.none")}>{t("gaming.history.noneHelp")}</EmptyState>
      ) : (
        <>
          <div className="card overflow-x-auto p-0">
            <table className="table">
              <thead>
                <tr>
                  <th>{t("gaming.history.game")}</th>
                  <th>{t("gaming.history.start")}</th>
                  <th>{t("gaming.history.duration")}</th>
                  <th>{t("gaming.history.monitored")}</th>
                  <th>{t("gaming.history.energy")}</th>
                  <th>{t("gaming.history.cost")}</th>
                  <th>{t("gaming.history.completeness")}</th>
                </tr>
              </thead>
              <tbody>
                {sessions.map((s) => (
                  <tr key={s.id} className={detail?.summary.id === s.id ? "bg-surface-2" : "hover:bg-surface-2/60"}>
                    <td>
                      <button type="button" className="text-start text-accent-blue underline-offset-2 hover:underline" onClick={() => void open(s.id)} aria-label={t("gaming.history.openSession", { name: s.displayName })}>
                        <GameName>{s.displayName}</GameName>
                      </button>
                      {s.isManual && <span className="ms-2 rounded bg-surface-3 px-1.5 py-0.5 text-[10px] text-ink-secondary">{t("gaming.manualBadge")}</span>}
                    </td>
                    <td>
                      <Num>{formatLocalDateTime(s.startedUtc)}</Num>
                    </td>
                    <td>
                      <Num>{formatDuration(s.durationSeconds)}</Num>
                    </td>
                    <td>
                      <Num>{formatDuration(s.monitoredSeconds)}</Num>
                    </td>
                    <td>
                      <Num>
                        {s.completeness === "none" ? "--" : `${s.systemIsLowerBound && s.systemJoules > 0 ? "≥ " : ""}${formatKwh(s.systemJoules)}`}
                      </Num>
                    </td>
                    <td>
                      <Cost display={costDisplay(s.costStatus, s.cost, s.currency)} />
                      {s.costPrecision !== "exact" && s.costStatus === "priced" && (
                        <span className="ms-1 text-amber-300" aria-label={t("gaming.history.approximate")} title={t("gaming.history.approximate")}>
                          *
                        </span>
                      )}
                    </td>
                    <td>
                      <CompletenessBadge completeness={s.completeness} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {sessions.length >= HISTORY_LIMIT && <p className="text-xs text-ink-muted">{t("gaming.history.limit", { count: HISTORY_LIMIT })}</p>}
        </>
      )}
      {detail && (
        <SessionDetail
          detail={detail}
          onClose={() => setDetail(null)}
          onChanged={() => {
            onChanged();
            setDetail(null);
          }}
        />
      )}
    </section>
  );
}

function DetailRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-4 border-b border-surface-border/40 py-1 text-sm">
      <dt className="text-ink-secondary">{label}</dt>
      <dd className="min-w-0 break-words text-end">{children}</dd>
    </div>
  );
}

function SessionDetail({ detail, onClose, onChanged }: { detail: GamingSessionDetail; onClose: () => void; onChanged: () => void }) {
  const { t } = useTranslation();
  const s = detail.summary;
  const unmonitored = unmonitoredSeconds(s.durationSeconds, s.monitoredSeconds);
  const lb = s.systemIsLowerBound;
  const lbMark = lb ? "≥ " : "";
  const cost = costDisplay(s.costStatus, s.cost, s.currency);
  const watts = (value: number | null, lower = false) => (value === null ? "--" : `${lower ? lbMark : ""}${formatWatts(value)}`);

  return (
    <section className="card flex flex-col gap-3" aria-labelledby="gaming-detail">
      <div className="flex items-center justify-between gap-2">
        <h3 id="gaming-detail" className="text-base">
          <GameName>{s.displayName}</GameName> <Num className="text-ink-muted">#{s.id}</Num>
        </h3>
        <button type="button" className="btn" onClick={onClose}>
          {t("common.close")}
        </button>
      </div>

      {lb && <Notice tone="warn">{t("gaming.history.lowerBoundNote")}</Notice>}

      <div className="grid gap-x-8 md:grid-cols-2">
        <dl>
          <DetailRow label={t("gaming.history.start")}>
            <Num>{formatLocalDateTime(s.startedUtc)}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.end")}>
            <Num>{s.endedUtc ? formatLocalDateTime(s.endedUtc) : "--"}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.duration")}>
            <Num>{formatDuration(s.durationSeconds)}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.monitored")}>
            <Num>{formatDuration(s.monitoredSeconds)}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.unmonitored")}>
            <Num>{formatDuration(unmonitored)}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.gaps")}>
            <Num>{detail.gaps.count}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.endReason")}>{t(endLabelKey(s))}</DetailRow>
          <DetailRow label={t("gaming.history.source")}>{t(SOURCE_KEYS[s.source])}</DetailRow>
          <DetailRow label={t("gaming.history.executable")}>
            <Num>{detail.exePath ?? s.exeName}</Num>
          </DetailRow>
        </dl>
        <dl>
          <DetailRow label={t("gaming.history.energy")}>
            <Num>{s.completeness === "none" ? "--" : `${lbMark}${formatKwh(s.systemJoules)}`}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.cost")}>
            <Cost display={cost} />
          </DetailRow>
          {s.costStatus === "withheldPartial" && s.lowerBoundCost !== null && (
            <DetailRow label={t("gaming.history.lowerBoundCost")}>
              <Num>≥ {formatMoney(s.lowerBoundCost, s.currency ?? "EUR", 2)}</Num>
            </DetailRow>
          )}
          {s.costStatus !== "priced" && <DetailRow label={t("gaming.history.costStatus")}>{t(COST_STATUS_KEYS[s.costStatus as keyof typeof COST_STATUS_KEYS])}</DetailRow>}
          <DetailRow label={t("gaming.history.avgCpu")}>
            <Num>{watts(s.avgCpuWatts)}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.avgGpu")}>
            <Num>{watts(s.avgGpuWatts)}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.avgSystem")}>
            <Num>{watts(s.avgSystemWatts, true)}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.peakCpu")}>
            <Num>{watts(s.peakCpuWatts)}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.peakGpu")}>
            <Num>{watts(s.peakGpuWatts)}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.peakSystem")}>
            <Num>{watts(s.peakSystemWatts, true)}</Num>
          </DetailRow>
          <DetailRow label={t("gaming.history.completeness")}>
            <CompletenessBadge completeness={s.completeness} />
          </DetailRow>
        </dl>
      </div>
      {s.costPrecision !== "exact" && <p className="text-xs text-amber-300">{t("gaming.history.approxPrice")}</p>}
      <GameActions summary={s} onChanged={onChanged} />
    </section>
  );
}

/** Manual correction from a session: rename the game everywhere, or never detect this executable again. */
function GameActions({ summary, onChanged }: { summary: GamingSessionSummary; onChanged: () => void }) {
  const { t } = useTranslation();
  const [name, setName] = useState(summary.displayName);
  const [error, setError] = useState<unknown>(null);
  const hasExecutable = !summary.gameKey.startsWith("manual:");

  const save = (kind: GameMappingKind) =>
    sidecarRequest({ cmd: "set_game_mapping", exeName: summary.exeName, mappingKind: kind, displayName: kind === "game" ? name.trim() : undefined }).then(onChanged, (e) => setError(e));

  if (!hasExecutable) return null;
  return (
    <div className="flex flex-col gap-2 border-t border-surface-border pt-3">
      <div className="flex flex-wrap items-end gap-2">
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
      </div>
      <p className="max-w-prose text-xs text-ink-muted">{t("gaming.games.actionsNote")}</p>
      {error !== null && <ErrorPanel error={error} compact />}
    </div>
  );
}

// ---------------------------------------------------------------- aggregates

function AggregatesSection({ aggregates }: { aggregates: GameAggregate[] | null }) {
  const { t } = useTranslation();
  if (!aggregates || aggregates.length === 0) return null;

  return (
    <section className="flex flex-col gap-3" aria-labelledby="gaming-aggregates">
      <h2 id="gaming-aggregates" className="text-sm font-semibold">
        {t("gaming.aggregates.title")}
      </h2>
      <p className="max-w-prose text-xs text-ink-secondary">{t("gaming.aggregates.help")}</p>
      <div className="card overflow-x-auto p-0">
        <table className="table">
          <thead>
            <tr>
              <th>{t("gaming.history.game")}</th>
              <th>{t("gaming.aggregates.sessions")}</th>
              <th>{t("gaming.aggregates.playTime")}</th>
              <th>{t("gaming.aggregates.monitored")}</th>
              <th>{t("gaming.aggregates.avgSession")}</th>
              <th>{t("gaming.aggregates.complete")}</th>
              <th>{t("gaming.aggregates.partial")}</th>
            </tr>
          </thead>
          <tbody>
            {aggregates.map((g) => (
              <tr key={g.gameKey}>
                <td>
                  <GameName>{g.displayName}</GameName>
                </td>
                <td>
                  <Num>{g.sessionCount}</Num>
                </td>
                <td>
                  <Num>{formatDuration(g.totalDurationSeconds)}</Num>
                </td>
                <td>
                  <Num>{formatDuration(g.totalMonitoredSeconds)}</Num>
                </td>
                <td>
                  <Num>{formatDuration(g.averageSessionDurationSeconds)}</Num>
                </td>
                <td>
                  {g.complete.sessions === 0 ? (
                    <span className="text-ink-muted">--</span>
                  ) : (
                    <div className="flex flex-col">
                      <Num>{formatKwh(g.complete.systemJoules)}</Num>
                      <Num className="text-xs text-ink-secondary">{g.complete.avgSystemWatts === null ? "--" : formatWatts(g.complete.avgSystemWatts, 0)}</Num>
                      <span className="text-xs text-ink-secondary">
                        {g.complete.cost !== null ? <Num>{formatMoney(g.complete.cost, g.currency ?? "EUR", 2)}</Num> : "--"}
                        {g.complete.unpricedSessions > 0 && ` (${t("gaming.aggregates.unpriced")}: ${g.complete.unpricedSessions})`}
                      </span>
                    </div>
                  )}
                </td>
                <td>
                  {g.partial.sessions === 0 ? (
                    <span className="text-ink-muted">--</span>
                  ) : (
                    <div className="flex flex-col gap-0.5">
                      <Badge kind="partial" />
                      <Num>≥ {formatKwh(g.partial.systemJoules)}</Num>
                      <span className="text-xs text-ink-secondary">{t("gaming.aggregates.partialSessions", { count: g.partial.sessions })}</span>
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

function GamesSection({ mappings, onChanged }: { mappings: GameMapping[] | null; onChanged: () => void }) {
  const { t } = useTranslation();
  const visible = useUiVisible();
  const [exe, setExe] = useState("");
  const [name, setName] = useState("");
  const [kind, setKind] = useState<GameMappingKind>("game");
  const [editing, setEditing] = useState<GameMapping | null>(null);
  const [running, setRunning] = useState<RunningExecutable[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const formRef = useRef<HTMLDivElement>(null);

  const reset = () => {
    setExe("");
    setName("");
    setKind("game");
    setEditing(null);
  };

  const pick = async () => {
    try {
      setRunning(await sidecarRequest<RunningExecutable[]>({ cmd: "get_running_executables" }));
      setError(null);
    } catch (e) {
      setError(e);
    }
  };

  const save = async () => {
    try {
      await sidecarRequest({ cmd: "set_game_mapping", exeName: exe.trim(), mappingKind: kind, displayName: kind === "game" ? name.trim() || undefined : undefined });
      reset();
      setError(null);
      setSaved(true);
      onChanged();
    } catch (e) {
      setError(e);
    }
  };

  const remove = (m: GameMapping) =>
    sidecarRequest({ cmd: "remove_game_mapping", exeName: m.exeName, exePath: m.exePath || undefined }).then(
      () => {
        if (editing?.id === m.id) reset();
        setSaved(false);
        onChanged();
      },
      (e) => setError(e),
    );

  const edit = (m: GameMapping) => {
    setEditing(m);
    setExe(m.exePath || m.exeName); // a path-specific mapping keeps its path so saving updates *that* entry
    setName(m.displayName ?? "");
    setKind(m.kind);
    setSaved(false);
    formRef.current?.scrollIntoView({ block: "nearest" });
  };

  // Only one window of the "pick" list is meaningful; drop it when the window is hidden so it never goes stale.
  useEffect(() => {
    if (!visible) setRunning(null);
  }, [visible]);

  return (
    <section className="flex flex-col gap-3" aria-labelledby="gaming-games">
      <h2 id="gaming-games" className="text-sm font-semibold">
        {t("gaming.games.title")}
      </h2>
      <p className="max-w-prose text-xs text-ink-secondary">{t("gaming.games.help")}</p>

      <div ref={formRef} className="card flex flex-col gap-3">
        {editing && (
          <p className="text-xs text-accent-blue">
            {t("gaming.games.editing")} <Num>{editing.exeName}</Num>
          </p>
        )}
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
          <button type="button" className="btn btn-primary" disabled={!exe.trim()} onClick={() => void save()}>
            {editing ? t("common.save") : t("gaming.games.add")}
          </button>
          {editing && (
            <button type="button" className="btn" onClick={reset}>
              {t("common.cancel")}
            </button>
          )}
          <button type="button" className="btn" onClick={() => void pick()}>
            {t("gaming.games.pick")}
          </button>
        </div>
        {running && (
          <div className="flex flex-wrap gap-2" role="group" aria-label={t("gaming.games.pick")}>
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
        {saved && (
          <p role="status" className="text-xs text-accent-green">
            ✓ {t("common.saved")}
          </p>
        )}
        {error !== null && <ErrorPanel error={error} compact />}
      </div>

      {mappings === null ? (
        <Loading />
      ) : mappings.length === 0 ? (
        <p className="text-sm text-ink-secondary">{t("gaming.games.empty")}</p>
      ) : (
        <div className="card overflow-x-auto p-0">
          <table className="table">
            <thead>
              <tr>
                <th>{t("gaming.games.exe")}</th>
                <th>{t("gaming.games.kind")}</th>
                <th>{t("gaming.games.name")}</th>
                <th>
                  <span className="sr-only">{t("gaming.games.actions")}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {mappings.map((m) => (
                <tr key={m.id} className={editing?.id === m.id ? "bg-surface-2" : undefined}>
                  <td>
                    <Num>{m.exeName}</Num>
                    {m.exePath && (
                      <div dir="ltr" className="max-w-[18rem] truncate text-xs text-ink-muted" title={m.exePath}>
                        {m.exePath}
                      </div>
                    )}
                  </td>
                  <td>{m.kind === "game" ? t("gaming.games.kindGame") : t("gaming.games.kindExcluded")}</td>
                  <td>{m.displayName ? <GameName>{m.displayName}</GameName> : <span className="text-ink-muted">--</span>}</td>
                  <td className="whitespace-nowrap text-end">
                    <button type="button" className="btn me-2" onClick={() => edit(m)} aria-label={t("gaming.games.editFor", { name: m.exeName })}>
                      {t("gaming.games.edit")}
                    </button>
                    <button type="button" className="btn" onClick={() => void remove(m)} aria-label={t("gaming.games.removeFor", { name: m.exeName })}>
                      {t("gaming.games.remove")}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="max-w-prose text-xs text-ink-muted">{t("gaming.games.privacyNote")}</p>
    </section>
  );
}
