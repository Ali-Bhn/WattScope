import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

/**
 * The short first-run introduction: five plain statements and one button, no account, no cloud, no steps.
 * It is a modal <dialog> (focus is trapped, Escape closes it for this session) and is shown only until the
 * user continues once -- the sidecar persists that, so it survives restarts and never depends on browser storage.
 */
export function FirstRun({ onContinue, onSetPrice, onDismiss }: { onContinue: () => void; onSetPrice: () => void; onDismiss: () => void }) {
  const { t } = useTranslation();
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    // showModal() focuses the first control; the primary action is the one to land on.
    dialog?.querySelector<HTMLElement>("[data-autofocus]")?.focus();
  }, []);

  const points = ["local", "measured", "estimated", "cpu", "price"] as const;

  return (
    <dialog
      ref={ref}
      className="modal"
      aria-labelledby="first-run-title"
      onCancel={(e) => {
        e.preventDefault();
        onDismiss(); // Escape: hide for now; it will be offered again until completed
      }}
    >
      <div className="flex flex-col gap-4">
        <div>
          <h2 id="first-run-title" className="text-lg font-semibold">
            {t("firstRun.title")}
          </h2>
          <p className="mt-1 text-sm text-ink-secondary">{t("firstRun.intro")}</p>
        </div>
        <ul className="flex flex-col gap-3 text-sm">
          {points.map((p) => (
            <li key={p} className="flex gap-2">
              <span aria-hidden="true" className="mt-px text-accent-green">
                ✓
              </span>
              <span>
                <strong className="font-semibold">{t(`firstRun.points.${p}.title`)}</strong> {t(`firstRun.points.${p}.body`)}
              </span>
            </li>
          ))}
        </ul>
        <p className="text-xs text-ink-muted">{t("firstRun.footer")}</p>
        <div className="flex flex-wrap justify-end gap-2">
          <button type="button" className="btn" onClick={onSetPrice}>
            {t("firstRun.setPrice")}
          </button>
          <button type="button" className="btn btn-primary" data-autofocus onClick={onContinue}>
            {t("firstRun.start")}
          </button>
        </div>
      </div>
    </dialog>
  );
}
