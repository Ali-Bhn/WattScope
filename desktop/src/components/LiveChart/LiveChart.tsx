import { useEffect, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import type { TickMessage } from "@/lib/types";

// Bounded history: old points are dropped as new ones arrive, so memory and
// draw cost never grow with session length (Part E/J). At a 1 s interval
// this is 5 minutes of history; it simply holds less wall-clock time at a
// shorter configured interval.
const HISTORY_CAPACITY = 300;

interface Point {
  cpu: number | null;
  gpu: number | null;
  total: number | null;
}

interface SeriesVisibility {
  cpu: boolean;
  gpu: boolean;
  total: boolean;
}

const COLORS: Record<keyof SeriesVisibility, string> = {
  cpu: "#3ecf8e",
  gpu: "#4c9aff",
  total: "#a78bfa",
};

/**
 * Live power chart. Intentionally does NOT put tick data into React state:
 * it subscribes to the "sidecar-tick" event itself and draws straight to a
 * canvas on a throttled requestAnimationFrame loop, so a new sample never
 * triggers a React re-render of this component (or anything else) --
 * exactly what Part E asks for ("efficient updates", "no unnecessary
 * rerendering"). Only toggling a series' visibility is real React state,
 * since that's a rare, deliberate user action.
 */
export function LiveChart() {
  const { t } = useTranslation();
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const bufferRef = useRef<Point[]>([]);
  const dirtyRef = useRef(true);
  const [visible, setVisible] = useState<SeriesVisibility>({ cpu: true, gpu: true, total: true });
  const visibleRef = useRef(visible);
  visibleRef.current = visible;
  const [hasData, setHasData] = useState(false);

  useEffect(() => {
    let raf = 0;
    let unlisten: (() => void) | undefined;
    let cancelled = false;

    function draw() {
      raf = requestAnimationFrame(draw);
      if (!dirtyRef.current) return;
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

      const points = bufferRef.current;
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

      const padding = 8;
      const plotW = width - padding * 2;
      const plotH = height - padding * 2;
      const n = points.length;

      ctx.strokeStyle = "rgba(255,255,255,0.07)";
      ctx.lineWidth = 1;
      for (let i = 1; i < 4; i++) {
        const y = padding + (plotH / 4) * i;
        ctx.beginPath();
        ctx.moveTo(padding, y);
        ctx.lineTo(width - padding, y);
        ctx.stroke();
      }

      const drawSeries = (pick: (p: Point) => number | null, color: string) => {
        ctx.beginPath();
        let started = false;
        for (let i = 0; i < n; i++) {
          const value = pick(points[i]);
          const x = padding + (n === 1 ? 0 : (i / (n - 1)) * plotW);
          if (value === null) {
            started = false;
            continue;
          }
          const y = padding + plotH - (value / maxValue) * plotH;
          if (!started) {
            ctx.moveTo(x, y);
            started = true;
          } else {
            ctx.lineTo(x, y);
          }
        }
        ctx.strokeStyle = color;
        ctx.lineWidth = 1.5;
        ctx.lineJoin = "round";
        ctx.stroke();
      };

      if (v.total) drawSeries((p) => p.total, COLORS.total);
      if (v.gpu) drawSeries((p) => p.gpu, COLORS.gpu);
      if (v.cpu) drawSeries((p) => p.cpu, COLORS.cpu);

      ctx.restore();
    }

    raf = requestAnimationFrame(draw);

    listen<TickMessage>("sidecar-tick", (event) => {
      if (cancelled) return;
      const tick = event.payload;
      const buffer = bufferRef.current;
      buffer.push({ cpu: tick.cpuWatts, gpu: tick.gpuWatts, total: tick.estimatedTotalWatts });
      if (buffer.length > HISTORY_CAPACITY) buffer.shift();
      dirtyRef.current = true;
      setHasData(true);
    }).then((fn) => {
      if (cancelled) fn();
      else unlisten = fn;
    });

    return () => {
      cancelled = true;
      cancelAnimationFrame(raf);
      unlisten?.();
    };
  }, []);

  // Redraw once immediately after a visibility toggle, without waiting for the next tick.
  useEffect(() => {
    dirtyRef.current = true;
  }, [visible]);

  return (
    <div className="card flex flex-col gap-3">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold">{t("chart.title")}</h2>
        <div className="flex gap-4 text-xs">
          <SeriesToggle id="cpu" label={t("chart.cpu")} checked={visible.cpu} onChange={(c) => setVisible((s) => ({ ...s, cpu: c }))} />
          <SeriesToggle id="gpu" label={t("chart.gpu")} checked={visible.gpu} onChange={(c) => setVisible((s) => ({ ...s, gpu: c }))} />
          <SeriesToggle id="total" label={t("chart.total")} checked={visible.total} onChange={(c) => setVisible((s) => ({ ...s, total: c }))} />
        </div>
      </div>
      <div className="relative h-48 w-full">
        <canvas ref={canvasRef} className="h-full w-full" />
        {!hasData && (
          <div className="absolute inset-0 flex items-center justify-center text-sm text-ink-muted">{t("chart.noData")}</div>
        )}
      </div>
    </div>
  );
}

function SeriesToggle({
  id,
  label,
  checked,
  onChange,
}: {
  id: keyof SeriesVisibility;
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  const color = COLORS[id];
  return (
    <label className="flex cursor-pointer items-center gap-1.5 select-none">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        style={{ accentColor: color }}
      />
      <span style={{ color }}>{label}</span>
    </label>
  );
}
