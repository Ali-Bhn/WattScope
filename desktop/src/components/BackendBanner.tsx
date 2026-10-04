import { useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { useBackend } from "@/hooks/useSidecar";
import { ActionButton } from "@/components/ui/ActionButton";
import { Loading } from "@/components/ui/States";
import { Notice } from "@/components/ui/Notice";

/**
 * Where the background monitor (the sidecar process) stands, shown above every page whenever it is not
 * simply running: starting, restarting (a bounded automatic restart is pending), or failed (the host gave up).
 * After a give-up the only way forward is the user's Retry; it asks the host, which refuses to act unless the
 * monitor is really down, so a double click can never create a second monitor or session.
 */
export function BackendBanner() {
  const { t } = useTranslation();
  const backend = useBackend();
  const [busy, setBusy] = useState(false);
  const [retryError, setRetryError] = useState(false);

  if (backend.state === "running") return null;

  if (backend.state === "starting") {
    return <Loading label={t("backend.starting")} />;
  }

  if (backend.state === "restarting") {
    return (
      <Notice tone="warn" title={t("backend.restarting.title")}>
        {t("backend.restarting.hint")}
      </Notice>
    );
  }

  const reason = backend.reason ?? "tooManyRestarts";
  const retry = async () => {
    setBusy(true);
    setRetryError(false);
    try {
      await invoke("retry_sidecar");
    } catch {
      setRetryError(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Notice tone="error" title={t("backend.failed.title")}>
      <p>{t(`backend.reason.${reason}`)}</p>
      <div className="mt-1 flex flex-wrap items-center gap-3">
        <ActionButton className="btn" onClick={() => void retry()} inactive={busy}>
          {busy ? t("backend.retrying") : t("common.retry")}
        </ActionButton>
        {retryError && <span>{t("backend.retryFailed")}</span>}
        {backend.message && (
          <details className="text-ink-secondary">
            <summary className="cursor-pointer select-none">{t("errors.details")}</summary>
            <p dir="ltr" className="mt-1 break-words font-mono text-[11px]">
              {backend.message}
            </p>
          </details>
        )}
      </div>
    </Notice>
  );
}
