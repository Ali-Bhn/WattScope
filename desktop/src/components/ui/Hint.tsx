import { useEffect, useId, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

/**
 * A technical explanation next to a figure. A real button, so it is reachable and operable by keyboard; the
 * text is linked with aria-describedby, shows on hover, focus and click, and Escape dismisses it
 * (WCAG 1.4.13: dismissible, hoverable, persistent). Details stay out of the way until asked for.
 */
export function Hint({ children, label }: { children: ReactNode; label?: string }) {
  const { t } = useTranslation();
  const id = useId();
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [open]);

  return (
    <span className="relative inline-flex" onMouseEnter={() => setOpen(true)} onMouseLeave={() => setOpen(false)}>
      <button
        type="button"
        className="inline-flex h-4 w-4 items-center justify-center rounded-full border border-surface-border text-[10px] font-bold text-ink-secondary hover:text-ink-primary"
        aria-label={label ?? t("common.moreInfo")}
        aria-describedby={open ? id : undefined}
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        onFocus={() => setOpen(true)}
        onBlur={() => setOpen(false)}
      >
        i
      </button>
      {open && (
        <span
          id={id}
          role="tooltip"
          className="absolute start-0 top-full z-20 mt-1 w-64 rounded-md border border-surface-border bg-surface-3 p-2 text-xs font-normal normal-case leading-relaxed text-ink-primary shadow-lg"
        >
          {children}
        </span>
      )}
    </span>
  );
}
