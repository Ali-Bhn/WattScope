import type { ReactNode } from "react";

export type NoticeTone = "info" | "warn" | "error" | "ok";

const STYLE: Record<NoticeTone, { glyph: string; className: string }> = {
  info: { glyph: "ℹ", className: "border-accent-blue/30 bg-accent-blue/10 text-ink-primary" },
  warn: { glyph: "⚠", className: "border-amber-500/30 bg-amber-500/10 text-amber-200" },
  error: { glyph: "✕", className: "border-red-500/30 bg-red-500/10 text-red-200" },
  ok: { glyph: "✓", className: "border-accent-green/30 bg-accent-green/10 text-accent-green" },
};

/**
 * An inline message with a glyph as well as a colour. Errors are announced to assistive technology
 * (role="alert"); the rest are polite status text.
 */
export function Notice({ tone = "info", title, children, className = "" }: { tone?: NoticeTone; title?: ReactNode; children?: ReactNode; className?: string }) {
  const style = STYLE[tone];
  return (
    <div role={tone === "error" ? "alert" : "status"} className={`flex gap-2 rounded-md border p-3 text-sm ${style.className} ${className}`}>
      <span aria-hidden="true" className="mt-px shrink-0 font-bold">
        {style.glyph}
      </span>
      <div className="flex min-w-0 flex-col gap-1">
        {title && <span className="font-semibold">{title}</span>}
        {children && <div className="text-xs leading-relaxed opacity-95">{children}</div>}
      </div>
    </div>
  );
}
