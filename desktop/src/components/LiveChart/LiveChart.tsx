import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { monitoringStore } from "@/hooks/useSidecar";
import { formatNumber } from "@/lib/format";
import { HISTORY_CAPACITY, type HistoryPoint } from "@/lib/store";

interface SeriesVisibility {
  cpu: boolean;
  gpu: boolean;
  total: boolean;
}

// Colour is never the only cue: each series also has its own line pattern (and the legend shows it).
const SERIES: Record<keyof SeriesVisibility, { color: string; dash: number[]; width: number }> = {
  total: { color: "#a78bfa", dash: [], width: 2 },
  gpu: { color: "#4c9aff", dash: [6, 3], width: 1.5 },
  cpu: { color: "#3ecf8e", dash: [2, 3], width: 1.5 },
};

/**
 * Live power chart. It deliberately keeps tick data out of React state: it
 * subscribes to the store's tick callback and draws straight to a canvas on a
 * requestAnimationFrame that is requested only when something changed, so a new
 * sample never re-renders React. The bounded history itself lives in the store
 * (not here), so it survives navigating to another page and back, and is
 * capped at HISTORY_CAPACITY points. A hidden window draws nothing.
 */
export function LiveChart() {
  const { t } = useTranslation();
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const dirtyRef = useRef(true);
  const scheduleRef = useRef<(() => void) | null>(null);
  const [visible, setVisible] = useState<SeriesVisibility>({ cpu: true, gpu: true, total: true });
  const visibleRef = useRef(visible);
  visibleRef.current = visible;
  const [hasData, setHasData] = useState(() => monitoringStore.getHistory().length > 0);
  const hasDataRef = useRef(hasData);

  useEffect(() => {
    let raf = 0;
    let pending = false;
    let cancelled = false;

    const schedule = () => {
      if (pending || cancelled) return;
      pending = true;
      raf = requestAnimationFrame(draw);
    };
    scheduleRef.current = schedule;

    function draw() {
      pending = false;
      // Nothing to paint for a hidden window; stay dirty so the first visible frame repaints.
      if (document.hidden || !monitoringStore.getState().uiVisible || !dirtyRef.current) return;
      dirtyRef.current = false;

      const canvas = canvasRef.current;
      const ctx = canvas?.getContext("2d");
      if (!canvas || !ctx) return;

      const dpr = window.devicePixelRatio || 1;
      const width = canvas.clientWidth;
      const height = canvas.clientHeight;
      if (width === 0 || height === 0) return;
      if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) {
        canvas.width = Math.round(width * dpr);
        canvas.height = Math.round(height * dpr);
      }

      ctx.save();
      ctx.scale(dpr, dpr);
      ctx.clearRect(0, 0, width, height);

      const points: readonly HistoryPoint[] = monitoringStore.getHistory();
      if (points.length < 2) {
        ctx.restore();
        return;
      }

      const v = visibleRef.current;
      let maxValue = 10;
      for (const p of points) {
        if (v.cpu && p.cpu !== null) maxValue = Math.max(maxValue, p.cpu);
        if (v.gpu && p.gpu !== null) maxValue = Math.max(maxValue, p.gpu);
        if (v.total && p.total !== null) maxValue = Math.max(maxValue, p.total);
      }
      maxValue = Math.ceil(maxValue / 10) * 10; // a stable, round top of the scale

      const padX = 8;
      const padTop = 16;
      const padBottom = 8;
      const plotW = width - padX * 2;
      const plotH = height - padTop - padBottom;
      const n = points.length;
      // The line fills the width once the buffer is full; before that it grows from the start edge.
      const stepX = plotW / (HISTORY_CAPACITY - 1);

      ctx.strokeStyle = "rgba(255,255,255,0.07)";
      ctx.lineWidth = 1;
      ctx.setLineDash([]);
      for (let i = 0; i <= 4; i++) {
        const y = padTop + (plotH / 4) * i;
        ctx.beginPath();
        ctx.moveTo(padX, y);
        ctx.lineTo(width - padX, y);
        ctx.stroke();
      }

      ctx.fillStyle = "#8b94a3";
      ctx.font = "11px Segoe UI, sans-serif";
      ctx.textBaseline = "top";
      ctx.fillText(`${formatNumber(maxValue, 0)} W`, padX, 2);

      // Time always runs left to right (older -> newer), also in right-to-left layouts: a chart is a technical figure, like a number.
      const xOf = (i: number) => padX + i * stepX;
      const drawSeries = (pick: (p: HistoryPoint) => number | null, style: (typeof SERIES)[keyof SeriesVisibility]) => {
        ctx.beginPath();
        let started = false;
        for (let i = 0; i < n; i++) {
          const value = pick(points[i]);
          if (value === null) {
            started = false; // a missing reading or a deliberate break: the line stops, it is never drawn as zero
            continue;
          }
          const y = padTop + plotH - (value / maxValue) * plotH;
          if (!started) {
            ctx.moveTo(xOf(i), y);
            started = true;
          } else {
            ctx.lineTo(xOf(i), y);
          }
        }
        ctx.strokeStyle = style.color;
        ctx.lineWidth = style.width;
        ctx.lineJoin = "round";
        ctx.setLineDash(style.dash);
        ctx.stroke();
      };

      if (v.total) drawSeries((p) => p.total, SERIES.total);
      if (v.gpu) drawSeries((p) => p.gpu, SERIES.gpu);
      if (v.cpu) drawSeries((p) => p.cpu, SERIES.cpu);

      ctx.restore();
    }

    const markDirty = () => {
      dirtyRef.current = true;
      schedule();
    };

    markDirty();

    const offTicks = monitoringStore.subscribeTicks(() => {
      if (!hasDataRef.current) {
        hasDataRef.current = true;
        setHasData(true);
      }
      markDirty();
    });

    // Repaint when the window is shown again, and when the history is reset (a new run).
    let wasVisible = monitoringStore.getState().uiVisible;
    const offState = monitoringStore.subscribe(() => {
      const nowVisible = monitoringStore.getState().uiVisible;
      if (nowVisible && !wasVisible) markDirty();
      wasVisible = nowVisible;
      const has = monitoringStore.getHistory().length > 0;
      if (has !== hasDataRef.current) {
        hasDataRef.current = has;
        setHasData(has);
        markDirty();
      }
    });

    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(markDirty);
    if (canvasRef.current) observer?.observe(canvasRef.current);

    return () => {
      cancelled = true;
      cancelAnimationFrame(raf);
      scheduleRef.current = null;
      offTicks();
      offState();
      observer?.disconnect();
    };
  }, []);

  // Redraw once immediately after a series toggle, without waiting for the next tick.
  useEffect(() => {
    dirtyRef.current = true;
    scheduleRef.current?.();
  }, [visible]);

  return (
    <section className="card flex flex-col gap-3" aria-labelledby="live-chart-title">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="live-chart-title" className="text-sm font-semibold">
          {t("chart.title")}
        </h2>
        <div className="flex flex-wrap gap-4 text-xs">
          <SeriesToggle id="cpu" label={t("chart.cpu")} checked={visible.cpu} onChange={(c) => setVisible((s) => ({ ...s, cpu: c }))} />
          <SeriesToggle id="gpu" label={t("chart.gpu")} checked={visible.gpu} onChange={(c) => setVisible((s) => ({ ...s, gpu: c }))} />
          <SeriesToggle id="total" label={t("chart.total")} checked={visible.total} onChange={(c) => setVisible((s) => ({ ...s, total: c }))} />
        </div>
      </div>
      <div className="relative h-48 w-full min-w-0">
        <canvas ref={canvasRef} className="h-full w-full" role="img" aria-label={t("chart.ariaLabel")} />
        {!hasData && <div className="absolute inset-0 flex items-center justify-center text-sm text-ink-muted">{t("chart.noData")}</div>}
      </div>
      <p className="text-xs text-ink-muted">{t("chart.note")}</p>
    </section>
  );
}

function SeriesToggle({ id, label, checked, onChange }: { id: keyof SeriesVisibility; label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  const style = SERIES[id];
  return (
    <label className="flex cursor-pointer select-none items-center gap-1.5">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} style={{ accentColor: style.color }} />
      <svg width="22" height="8" aria-hidden="true">
        <line x1="0" y1="4" x2="22" y2="4" stroke={style.color} strokeWidth={style.width} strokeDasharray={style.dash.join(" ")} />
      </svg>
      <span className="text-ink-secondary">{label}</span>
    </label>
  );
}
