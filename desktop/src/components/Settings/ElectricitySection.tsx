import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@/hooks/useQuery";
import { sidecarRequest } from "@/hooks/useSidecar";
import { Num } from "@/components/Num";
import { ConfirmDialog } from "@/components/ui/ConfirmDialog";
import { Field } from "@/components/ui/Field";
import { Notice } from "@/components/ui/Notice";
import { QueryView } from "@/components/ui/QueryView";
import { ErrorPanel } from "@/components/ui/States";
import { localInputToUtcIso, toLocalInputValue } from "@/lib/dates";
import { formatDuration, formatLocalDateTime, formatPrice } from "@/lib/format";
import { isCurrencyCode, parseSetting, validationKey, validationParams } from "@/lib/validation";
import type { PriceInfo, PricePreview } from "@/lib/types";
import { SettingBlock, SettingsGroup } from "./parts";

/** A price that starts earlier than this long ago is "in the past": it may change what already-recorded energy cost. */
const PAST_TOLERANCE_MS = 5 * 60 * 1000;

export function ElectricitySection() {
  const { t } = useTranslation();
  const query = useQuery<PriceInfo>({ cmd: "get_price_info" });

  return (
    <SettingsGroup id="electricity" title={t("settings.groups.electricity")} description={t("settings.electricity.description")}>
      <QueryView query={query}>{(info) => <PriceSettings info={info} onChanged={() => void query.reload()} />}</QueryView>
    </SettingsGroup>
  );
}

type Mode = "now" | "custom";

function PriceSettings({ info, onChanged }: { info: PriceInfo; onChanged: () => void }) {
  const { t } = useTranslation();
  const [priceText, setPriceText] = useState("");
  const [currencyText, setCurrencyText] = useState(info.defaultCurrency);
  const [mode, setMode] = useState<Mode>("now");
  const [customLocal, setCustomLocal] = useState(() => toLocalInputValue(new Date()));
  const [priceError, setPriceError] = useState<string | null>(null);
  const [currencyError, setCurrencyError] = useState<string | null>(null);
  const [dateError, setDateError] = useState<string | null>(null);
  const [failure, setFailure] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [savedPrice, setSavedPrice] = useState<string | null>(null);
  const [pending, setPending] = useState<{ preview: PricePreview; iso: string; canonical: string } | null>(null);

  const currencyLocked = info.currency !== null;
  const currency = info.currency ?? currencyText.trim().toUpperCase();

  /** Sends the price. Never edits or removes an earlier entry: the backend only ever appends. */
  const apply = async (canonical: string, iso: string | null, confirmHistorical: boolean) => {
    setBusy(true);
    setFailure(null);
    try {
      await sidecarRequest({
        cmd: "set_price",
        pricePerKwh: canonical, // exact decimal text: money never passes through a JavaScript float
        effectiveFrom: iso ?? undefined,
        currency: currencyLocked ? undefined : currency,
        confirmHistorical: confirmHistorical || undefined,
      });
      setSavedPrice(formatPrice(Number(canonical), currency));
      setPriceText("");
      setMode("now");
      setPending(null);
      onChanged();
    } catch (error) {
      setFailure(error);
      setPending(null);
    } finally {
      setBusy(false);
    }
  };

  const submit = async () => {
    setSavedPrice(null);
    setFailure(null);

    const price = parseSetting(priceText, "price");
    setPriceError(price.ok ? null : t(validationKey(price.error), validationParams("price")));
    const currencyOk = currencyLocked || isCurrencyCode(currency);
    setCurrencyError(currencyOk ? null : t("validation.currency"));
    const iso = mode === "custom" ? localInputToUtcIso(customLocal) : null;
    const dateOk = mode === "now" || iso !== null;
    setDateError(dateOk ? null : t("validation.dateTime"));
    if (!price.ok || !currencyOk || !dateOk) return;

    // A price that starts in the past may re-cost energy that is already recorded: show exactly what, and ask first.
    if (iso !== null && Date.parse(iso) < Date.now() - PAST_TOLERANCE_MS) {
      setBusy(true);
      try {
        const preview = await sidecarRequest<PricePreview>({ cmd: "preview_price", effectiveFrom: iso });
        if (preview.affectsRecordedEnergy) {
          setPending({ preview, iso, canonical: price.canonical });
          return;
        }
      } catch (error) {
        setFailure(error);
        return;
      } finally {
        setBusy(false);
      }
    }
    await apply(price.canonical, iso, false);
  };

  const now = Date.now();
  const entries = [...info.entries].sort((a, b) => b.effectiveFromUtc.localeCompare(a.effectiveFromUtc));

  return (
    <>
      <SettingBlock title={t("settings.electricity.current")}>
        {info.current ? (
          <p className="text-sm">
            <Num className="text-lg font-semibold">{formatPrice(info.current.pricePerKwh, info.current.currency)}</Num>{" "}
            <span className="text-xs text-ink-secondary">
              {t("settings.electricity.since")} <Num>{formatLocalDateTime(info.current.effectiveFromUtc)}</Num>
            </span>
          </p>
        ) : (
          <Notice tone="warn" title={t("settings.electricity.noneTitle")}>
            {t("settings.electricity.none")}
          </Notice>
        )}
      </SettingBlock>

      <SettingBlock title={t("settings.electricity.newPrice")}>
        <div className="flex flex-wrap items-start gap-3">
          <Field label={t("settings.electricity.pricePerKwh")} unit={`${currency || "…"}/kWh`} help={t("settings.electricity.priceHelp")} error={priceError}>
            {(props) => (
              <input
                {...props}
                className="input w-32"
                dir="ltr"
                inputMode="decimal"
                autoComplete="off"
                placeholder="0.30"
                value={priceText}
                onChange={(e) => {
                  setPriceText(e.target.value);
                  setPriceError(null);
                  setSavedPrice(null);
                }}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && !busy) void submit();
                }}
              />
            )}
          </Field>
          {currencyLocked ? (
            <div className="flex flex-col gap-1">
              <span className="label">{t("settings.electricity.currency")}</span>
              <span dir="ltr" className="py-1.5 text-sm">
                {info.currency}
              </span>
              <p className="max-w-[16rem] text-xs text-ink-muted">{t("settings.electricity.currencyLocked")}</p>
            </div>
          ) : (
            <Field label={t("settings.electricity.currency")} help={t("settings.electricity.currencyHelp")} error={currencyError}>
              {(props) => <input {...props} className="input w-20 uppercase" dir="ltr" maxLength={3} value={currencyText} onChange={(e) => setCurrencyText(e.target.value)} />}
            </Field>
          )}
        </div>

        <fieldset className="flex flex-col gap-2">
          <legend className="label mb-1">{t("settings.electricity.effective")}</legend>
          <label className="flex items-center gap-2 text-sm">
            <input type="radio" name="price-effective" checked={mode === "now"} onChange={() => setMode("now")} />
            {t("settings.electricity.fromNow")}
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input type="radio" name="price-effective" checked={mode === "custom"} onChange={() => setMode("custom")} />
            {t("settings.electricity.fromDate")}
          </label>
          {mode === "custom" && (
            <Field label={t("settings.electricity.effectiveAt")} help={t("settings.electricity.pastWarning")} error={dateError}>
              {(props) => <input {...props} type="datetime-local" className="input w-56" value={customLocal} onChange={(e) => setCustomLocal(e.target.value)} />}
            </Field>
          )}
        </fieldset>

        <p className="max-w-prose text-xs text-ink-muted">{t("settings.electricity.appendOnly")}</p>

        <div className="flex flex-wrap items-center gap-3">
          <button type="button" className="btn btn-primary" onClick={() => void submit()} disabled={busy || priceText.trim() === ""}>
            {t("settings.electricity.addPrice")}
          </button>
          {savedPrice && (
            <span role="status" className="text-xs text-accent-green">
              <span aria-hidden="true">✓ </span>
              {t("settings.electricity.saved", { price: savedPrice })}
            </span>
          )}
        </div>
        {failure !== null && <ErrorPanel error={failure} compact />}
      </SettingBlock>

      <SettingBlock title={t("settings.electricity.history")}>
        {entries.length === 0 ? (
          <p className="text-sm text-ink-secondary">{t("settings.electricity.historyEmpty")}</p>
        ) : (
          <div className="overflow-x-auto rounded-md border border-surface-border">
            <table className="table">
              <thead>
                <tr>
                  <th>{t("settings.electricity.from")}</th>
                  <th>{t("settings.electricity.pricePerKwh")}</th>
                  <th>{t("settings.electricity.status")}</th>
                </tr>
              </thead>
              <tbody>
                {entries.map((e) => {
                  const isCurrent = info.current?.effectiveFromUtc === e.effectiveFromUtc;
                  const scheduled = Date.parse(e.effectiveFromUtc) > now;
                  return (
                    <tr key={e.effectiveFromUtc}>
                      <td>
                        <Num>{formatLocalDateTime(e.effectiveFromUtc)}</Num>
                      </td>
                      <td>
                        <Num>{formatPrice(e.pricePerKwh, e.currency)}</Num>
                      </td>
                      <td className="text-xs text-ink-secondary">
                        {isCurrent ? <span className="font-semibold text-accent-green">✓ {t("settings.electricity.statusCurrent")}</span> : scheduled ? t("settings.electricity.statusScheduled") : t("settings.electricity.statusEarlier")}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        <p className="max-w-prose text-xs text-ink-muted">{t("settings.electricity.historyNote")}</p>
      </SettingBlock>

      <ConfirmDialog
        open={pending !== null}
        title={t("settings.electricity.confirmTitle")}
        confirmLabel={t("settings.electricity.confirmApply")}
        busy={busy}
        onCancel={() => setPending(null)}
        onConfirm={() => pending && void apply(pending.canonical, pending.iso, true)}
      >
        {pending && (
          <>
            <p>
              {t("settings.electricity.confirmBody", {
                date: formatLocalDateTime(pending.iso),
                count: pending.preview.affectedSessions,
              })}{" "}
              (<Num>{formatDuration(pending.preview.affectedMonitoredSeconds)}</Num>)
            </p>
            <p>{t("settings.electricity.confirmRecalc")}</p>
            <p>{t("settings.electricity.confirmKept")}</p>
          </>
        )}
      </ConfirmDialog>
    </>
  );
}
