import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { sidecarRequest, useSnapshot } from "@/hooks/useSidecar";
import { SUPPORTED_LANGUAGES, type SupportedLanguage } from "@/i18n";
import { Num } from "@/components/Num";
import { Badge } from "@/components/ui/Badge";
import { ConfirmDialog } from "@/components/ui/ConfirmDialog";
import { Notice } from "@/components/ui/Notice";
import { Loading } from "@/components/ui/States";
import { buildDiagnosticsText } from "@/lib/diagnostics";
import { formatBytes } from "@/lib/format";
import type { Diagnostics, HostInfo } from "@/lib/types";
import { SettingBlock, SettingsGroup } from "./parts";

const LANGUAGE_NAMES: Record<SupportedLanguage, string> = { en: "English", de: "Deutsch", fa: "فارسی" };

export function LanguageSection() {
  const { t, i18n } = useTranslation();
  return (
    <SettingsGroup id="language" title={t("settings.groups.language")} description={t("settings.language.description")}>
      <SettingBlock>
        <fieldset className="flex flex-wrap gap-4">
          <legend className="label mb-1">{t("settings.language.label")}</legend>
          {SUPPORTED_LANGUAGES.map((lng) => (
            <label key={lng} className="flex items-center gap-2 text-sm">
              <input type="radio" name="language" checked={i18n.resolvedLanguage === lng} onChange={() => void i18n.changeLanguage(lng)} />
              {/* Each language is named in itself, so it can always be found. */}
              <span lang={lng} dir={lng === "fa" ? "rtl" : "ltr"}>
                {LANGUAGE_NAMES[lng]}
              </span>
            </label>
          ))}
        </fieldset>
        <p className="max-w-prose text-xs text-ink-muted">{t("settings.language.note")}</p>
      </SettingBlock>
    </SettingsGroup>
  );
}

export function ApplicationSection() {
  const { t } = useTranslation();
  const snapshot = useSnapshot();
  const [confirmExit, setConfirmExit] = useState(false);
  const [exiting, setExiting] = useState(false);

  const exit = async () => {
    setExiting(true);
    try {
      await invoke("exit_app");
    } finally {
      setExiting(false);
      setConfirmExit(false);
    }
  };

  return (
    <SettingsGroup id="application" title={t("settings.groups.application")} description={t("settings.application.description")}>
      <SettingBlock title={t("settings.application.background")}>
        <p className="max-w-prose text-sm text-ink-secondary">{t("settings.application.backgroundHelp")}</p>
        <div>
          <button type="button" className="btn" onClick={() => setConfirmExit(true)}>
            {t("settings.application.exit")}
          </button>
        </div>
        <p className="text-xs text-ink-muted">{t("settings.application.exitHelp")}</p>
      </SettingBlock>

      <SettingBlock title={t("settings.application.administrator")}>
        <p className="max-w-prose text-sm text-ink-secondary">{t("settings.application.administratorHelp")}</p>
        <p className="flex items-center gap-2 text-sm">
          <Badge kind={snapshot?.elevated ? "complete" : "unavailable"}>{snapshot?.elevated ? t("settings.application.elevated") : t("settings.application.notElevated")}</Badge>
        </p>
      </SettingBlock>

      <SettingBlock title={t("settings.application.about")}>
        <DiagnosticsPanel />
      </SettingBlock>

      <ConfirmDialog open={confirmExit} title={t("settings.application.exitTitle")} confirmLabel={t("settings.application.exit")} busy={exiting} onCancel={() => setConfirmExit(false)} onConfirm={() => void exit()}>
        <p>{t("settings.application.exitBody")}</p>
      </ConfirmDialog>
    </SettingsGroup>
  );
}

/** Version, database and sensor facts, with a copy button. Loaded only when opened (no queries for a panel nobody looks at). */
function DiagnosticsPanel() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);

  return (
    <details onToggle={(e) => setOpen((e.currentTarget as HTMLDetailsElement).open)}>
      <summary className="cursor-pointer select-none text-sm font-medium">{t("settings.diagnostics.show")}</summary>
      <div className="mt-3">{open && <DiagnosticsBody />}</div>
    </details>
  );
}

interface DiagnosticsBundle {
  diagnostics: Diagnostics;
  host: HostInfo | null;
}

async function loadDiagnostics(): Promise<DiagnosticsBundle> {
  const [diagnostics, host] = await Promise.all([sidecarRequest<Diagnostics>({ cmd: "get_diagnostics" }), invoke<HostInfo>("get_host_info").catch(() => null)]);
  return { diagnostics, host };
}

function DiagnosticsBody() {
  const { t } = useTranslation();
  const [bundle, setBundle] = useState<DiagnosticsBundle | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [copied, setCopied] = useState<"ok" | "failed" | null>(null);

  // Diagnostics need the sidecar *and* the host, so they load through their own function rather than `useQuery`.
  useEffect(() => {
    let cancelled = false;
    loadDiagnostics().then(
      (b) => !cancelled && setBundle(b),
      (e) => !cancelled && setError(e),
    );
    return () => {
      cancelled = true;
    };
  }, []);

  if (error) return <Notice tone="error" title={t("settings.diagnostics.failed")}>{t("errors.backendUnavailable.hint")}</Notice>;
  if (!bundle) return <Loading />;

  const { diagnostics: d, host } = bundle;
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(buildDiagnosticsText(d, host));
      setCopied("ok");
    } catch {
      setCopied("failed");
    }
  };

  const rows: [string, string][] = [
    [t("settings.diagnostics.version"), host?.appVersion ?? d.appVersion ?? "n/a"],
    [t("settings.diagnostics.schema"), String(d.schemaVersion)],
    [t("settings.diagnostics.dbSize"), formatBytes(d.databaseBytes)],
    [t("settings.diagnostics.monitoring"), `${d.monitoringState}${d.completeness ? ` / ${d.completeness}` : ""}`],
    [t("settings.diagnostics.backend"), host ? host.backend.state : "n/a"],
    [t("settings.diagnostics.runtime"), d.runtime],
    [t("settings.diagnostics.os"), `${d.os ?? "n/a"} ${d.architecture ?? ""}`.trim()],
    [t("settings.diagnostics.webview"), host?.webviewVersion ?? "n/a"],
  ];

  return (
    <div className="flex flex-col gap-3">
      <dl className="grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-x-6 gap-y-2 text-sm">
        {rows.map(([label, value]) => (
          <div key={label} className="min-w-0">
            <dt className="text-xs text-ink-muted">{label}</dt>
            <dd className="break-words">
              <Num>{value}</Num>
            </dd>
          </div>
        ))}
      </dl>
      <div className="min-w-0">
        <p className="text-xs text-ink-muted">{t("settings.diagnostics.location")}</p>
        <p dir="ltr" className="break-all font-mono text-xs">
          {d.databasePath}
        </p>
      </div>
      <div className="min-w-0">
        <p className="text-xs text-ink-muted">{t("settings.diagnostics.sensors")}</p>
        {d.sensors && d.sensors.length > 0 ? (
          <ul className="text-xs">
            {d.sensors.map((s) => (
              <li key={`${s.channel}-${s.sensor}`} dir="ltr">
                {s.channel}: {s.hardware} / {s.sensor}
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-xs">{t("settings.diagnostics.noSensors")}</p>
        )}
      </div>
      <div className="min-w-0">
        <p className="text-xs text-ink-muted">{t("settings.diagnostics.providers")}</p>
        <ul className="text-xs">
          {(d.providers ?? []).map((p) => (
            <li key={p.id} dir="ltr">
              {p.id}: {p.available ? t("settings.diagnostics.available") : t("settings.diagnostics.unavailable")}
              {p.requiresElevation ? ` (${t("settings.diagnostics.needsAdmin")})` : ""}
            </li>
          ))}
        </ul>
      </div>
      <div className="flex flex-wrap items-center gap-3">
        <button type="button" className="btn" onClick={() => void copy()}>
          {t("settings.diagnostics.copy")}
        </button>
        {copied === "ok" && (
          <span role="status" className="text-xs text-accent-green">
            ✓ {t("settings.diagnostics.copied")}
          </span>
        )}
        {copied === "failed" && (
          <span role="alert" className="text-xs text-red-300">
            ✕ {t("settings.diagnostics.copyFailed")}
          </span>
        )}
      </div>
      <p className="max-w-prose text-xs text-ink-muted">{t("settings.diagnostics.privacy")}</p>
    </div>
  );
}
