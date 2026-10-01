import { useEffect, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { useSidecarRequest, useSidecarStatus } from "@/hooks/useSidecar";
import { SUPPORTED_LANGUAGES, type SupportedLanguage } from "@/i18n";
import { Num } from "@/components/Num";
import { formatLocalDateTime } from "@/lib/format";
import type { ElectricityPrice, EstimationSettings, StorageStatistics } from "@/lib/types";

const LANGUAGE_NAMES: Record<SupportedLanguage, string> = {
  en: "English",
  de: "Deutsch",
  fa: "فارسی",
};

export function Settings() {
  const { i18n } = useTranslation();
  const request = useSidecarRequest();
  const status = useSidecarStatus();

  return (
    <div className="flex flex-col gap-4">
      <LanguageCard currentLanguage={i18n.language} onChange={(lng) => i18n.changeLanguage(lng)} />
      <PriceCard request={request} />
      <EstimationCard request={request} />
      <IntervalCard />
      <RetentionCard request={request} />
      <ElevationCard elevated={status?.elevated ?? false} />
      <DbInfoCard request={request} />
    </div>
  );
}

function SectionCard({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="card flex flex-col gap-3">
      <h2 className="text-sm font-semibold">{title}</h2>
      {children}
    </section>
  );
}

function LanguageCard({ currentLanguage, onChange }: { currentLanguage: string; onChange: (lng: string) => void }) {
  const { t } = useTranslation();
  return (
    <SectionCard title={t("settings.language")}>
      <div className="flex gap-2">
        {SUPPORTED_LANGUAGES.map((lng) => (
          <button
            key={lng}
            type="button"
            onClick={() => onChange(lng)}
            className={`btn ${currentLanguage === lng ? "btn-primary" : ""}`}
          >
            {LANGUAGE_NAMES[lng]}
          </button>
        ))}
      </div>
    </SectionCard>
  );
}

function PriceCard({ request }: { request: ReturnType<typeof useSidecarRequest> }) {
  const { t } = useTranslation();
  const [prices, setPrices] = useState<ElectricityPrice[] | null>(null);
  const [value, setValue] = useState("");
  const [effectiveFrom, setEffectiveFrom] = useState("");
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = () => request<ElectricityPrice[]>({ cmd: "get_prices" }).then(setPrices);
  useEffect(() => {
    reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const save = async () => {
    setError(null);
    const price = Number(value.replace(",", "."));
    if (!Number.isFinite(price) || price < 0) {
      setError(t("settings.invalidPrice"));
      return;
    }
    try {
      await request({
        cmd: "set_price",
        pricePerKwh: price,
        effectiveFrom: effectiveFrom ? new Date(effectiveFrom).toISOString() : undefined,
      });
      setStatus(t("settings.saved"));
      setValue("");
      reload();
    } catch (e) {
      setError(String(e));
    }
  };

  return (
    <SectionCard title={t("settings.electricityPrice")}>
      <div className="flex flex-wrap items-end gap-3">
        <Field label={t("settings.pricePerKwh")}>
          <input className="input w-32" inputMode="decimal" value={value} onChange={(e) => setValue(e.target.value)} placeholder="0.30" />
        </Field>
        <Field label={t("settings.effectiveFrom")}>
          <input type="datetime-local" className="input" value={effectiveFrom} onChange={(e) => setEffectiveFrom(e.target.value)} />
          <span className="ms-2 text-xs text-ink-muted">({t("settings.effectiveNow")})</span>
        </Field>
        <button type="button" className="btn btn-primary" onClick={save}>
          {t("settings.save")}
        </button>
      </div>
      {error && <p className="text-xs text-red-400">{error}</p>}
      {status && !error && <p className="text-xs text-accent-green">{status}</p>}

      {prices && prices.length > 0 && (
        <div className="mt-1 flex flex-col gap-1 text-xs text-ink-secondary">
          <span className="label">{t("settings.priceHistory")}</span>
          {prices.map((p) => (
            <div key={p.effectiveFromUtc}>
              <Num>{formatLocalDateTime(p.effectiveFromUtc)}</Num> -- <Num>{p.pricePerKwh}</Num> {p.currency}/kWh
            </div>
          ))}
        </div>
      )}
    </SectionCard>
  );
}

function EstimationCard({ request }: { request: ReturnType<typeof useSidecarRequest> }) {
  const { t } = useTranslation();
  const [settings, setSettings] = useState<EstimationSettings | null>(null);
  const [otherWatts, setOtherWatts] = useState("");
  const [efficiency, setEfficiency] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    request<EstimationSettings>({ cmd: "get_estimation" }).then((s) => {
      setSettings(s);
      setOtherWatts(String(s.otherComponentsWatts));
      setEfficiency(String(Math.round(s.psuEfficiency * 100)));
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const save = async () => {
    setError(null);
    const watts = Number(otherWatts);
    const pct = Number(efficiency);
    if (!Number.isFinite(watts) || watts < 0) {
      setError(t("settings.invalidPrice"));
      return;
    }
    if (!Number.isFinite(pct) || pct <= 0 || pct > 100) {
      setError(t("settings.invalidEfficiency"));
      return;
    }
    try {
      const updated = await request<EstimationSettings>({
        cmd: "set_estimation",
        otherWatts: watts,
        psuEfficiency: pct / 100,
      });
      setSettings(updated);
    } catch (e) {
      setError(String(e));
    }
  };

  if (!settings) return null;

  return (
    <SectionCard title={t("dashboard.otherPower")}>
      <p className="text-xs text-ink-muted">{t("settings.assumption")}</p>
      <div className="flex flex-wrap items-end gap-3">
        <Field label={t("settings.otherComponents") + " (W)"}>
          <input className="input w-28" inputMode="decimal" value={otherWatts} onChange={(e) => setOtherWatts(e.target.value)} />
        </Field>
        <Field label={t("settings.psuEfficiency") + " (%)"}>
          <input className="input w-24" inputMode="decimal" value={efficiency} onChange={(e) => setEfficiency(e.target.value)} />
        </Field>
        <button type="button" className="btn btn-primary" onClick={save}>
          {t("settings.save")}
        </button>
      </div>
      {error && <p className="text-xs text-red-400">{error}</p>}
      <p className="text-xs text-ink-muted">
        {t("settings.measured")}: CPU {settings.includeCpuPackage ? "✓" : "✕"} / GPU {settings.includeGpuBoard ? "✓" : "✕"}
      </p>
    </SectionCard>
  );
}

function IntervalCard() {
  const { t } = useTranslation();
  const [intervalMs, setIntervalMs] = useState("1000");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<number | null>(null);

  const save = async () => {
    setError(null);
    const value = Number(intervalMs);
    if (!Number.isFinite(value) || value < 250) {
      setError(t("settings.invalidInterval"));
      return;
    }
    setBusy(true);
    try {
      await invoke("restart_sidecar", { intervalMs: Math.round(value) });
      setSavedAt(Date.now());
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <SectionCard title={t("settings.monitoringInterval")}>
      <p className="text-xs text-ink-muted">
        Changing this restarts the background monitor (a short gap is recorded; no energy is invented across it).
      </p>
      <div className="flex items-end gap-3">
        <Field label="ms">
          <input className="input w-28" inputMode="numeric" value={intervalMs} onChange={(e) => setIntervalMs(e.target.value)} />
        </Field>
        <button type="button" className="btn btn-primary" onClick={save} disabled={busy}>
          {t("settings.save")}
        </button>
      </div>
      {error && <p className="text-xs text-red-400">{error}</p>}
      {savedAt && !error && <p className="text-xs text-accent-green">{t("settings.saved")}</p>}
    </SectionCard>
  );
}

function RetentionCard({ request }: { request: ReturnType<typeof useSidecarRequest> }) {
  const { t } = useTranslation();
  const [days, setDays] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<number | null>(null);

  useEffect(() => {
    request<{ days: number }>({ cmd: "get_retention" }).then((r) => setDays(String(r.days)));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const save = async () => {
    setError(null);
    const value = Number(days);
    if (!Number.isInteger(value) || value < 0 || value > 3650) {
      setError(t("settings.invalidPrice"));
      return;
    }
    try {
      await request({ cmd: "set_retention", retentionDays: value });
      setSavedAt(Date.now());
    } catch (e) {
      setError(String(e));
    }
  };

  return (
    <SectionCard title={t("settings.retention")}>
      <p className="text-xs text-ink-muted">{t("settings.retentionHelp")}</p>
      <div className="flex items-end gap-3">
        <Field label={t("settings.retentionDays")}>
          <input className="input w-24" inputMode="numeric" value={days} onChange={(e) => setDays(e.target.value)} />
        </Field>
        <button type="button" className="btn btn-primary" onClick={save}>
          {t("settings.save")}
        </button>
      </div>
      {error && <p className="text-xs text-red-400">{error}</p>}
      {savedAt && !error && <p className="text-xs text-accent-green">{t("settings.saved")}</p>}
    </SectionCard>
  );
}

function ElevationCard({ elevated }: { elevated: boolean }) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [requested, setRequested] = useState(false);

  const restartElevated = async () => {
    setBusy(true);
    setError(null);
    try {
      await invoke("restart_sidecar_elevated");
      setRequested(true);
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <SectionCard title={t("settings.elevation")}>
      <p className="text-xs text-ink-muted">{t("settings.elevationDescription")}</p>
      <p className="text-xs">
        {t("dashboard.monitoringStatus")}: <strong>{elevated ? t("settings.measured") : t("dashboard.notElevated")}</strong>
      </p>
      {!elevated && (
        <button type="button" className="btn" onClick={restartElevated} disabled={busy}>
          {t("settings.restartElevated")}
        </button>
      )}
      {requested && (
        <p className="text-xs text-amber-400">
          A Windows administrator prompt was requested. Live monitoring in this window pauses until you restart
          WattScope, which will then connect to the elevated session's data.
        </p>
      )}
      {error && <p className="text-xs text-red-400">{error}</p>}
    </SectionCard>
  );
}

function DbInfoCard({ request }: { request: ReturnType<typeof useSidecarRequest> }) {
  const [info, setInfo] = useState<StorageStatistics | null>(null);

  useEffect(() => {
    request<StorageStatistics>({ cmd: "get_db_info" }).then(setInfo);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (!info) return null;

  return (
    <SectionCard title="Database">
      <dl className="grid grid-cols-2 gap-x-6 gap-y-1 text-xs text-ink-secondary sm:grid-cols-4">
        <Dt label="Schema" value={info.schemaVersion} />
        <Dt label="Sessions" value={info.sessions} />
        <Dt label="Buckets" value={info.energyBuckets} />
        <Dt label="Price splits" value={info.priceSplitRows} />
        <Dt label="Raw samples" value={info.powerSamples} />
        <Dt label="Gaps" value={info.gaps} />
        <Dt label="Prices" value={info.prices} />
        <Dt label="Size" value={`${((info.databaseBytes + info.walBytes) / 1024).toFixed(1)} KiB`} />
      </dl>
    </SectionCard>
  );
}

function Dt({ label, value }: { label: string; value: string | number }) {
  return (
    <div>
      <dt className="text-ink-muted">{label}</dt>
      <dd>
        <Num>{value}</Num>
      </dd>
    </div>
  );
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="flex flex-col gap-1 text-sm">
      <span className="label">{label}</span>
      {children}
    </label>
  );
}
