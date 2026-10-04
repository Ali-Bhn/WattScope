import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@/hooks/useQuery";
import { sidecarRequest } from "@/hooks/useSidecar";
import { Num } from "@/components/Num";
import { ActionButton } from "@/components/ui/ActionButton";
import { ConfirmDialog } from "@/components/ui/ConfirmDialog";
import { Field } from "@/components/ui/Field";
import { Notice } from "@/components/ui/Notice";
import { QueryView } from "@/components/ui/QueryView";
import { ErrorPanel } from "@/components/ui/States";
import { formatBytes, formatLocalDateTime, formatNumber } from "@/lib/format";
import { parseSetting, validationKey, validationParams } from "@/lib/validation";
import type { HistoryDeletionPreview, MaintenanceResult, StorageStatistics } from "@/lib/types";
import { NumberSettingField, SettingBlock, SettingsGroup } from "./parts";
import { useNumberSetting } from "./useNumberSetting";

export function DataSection() {
  const { t } = useTranslation();
  const retention = useQuery<{ days: number }>({ cmd: "get_retention" });
  const stats = useQuery<StorageStatistics>({ cmd: "get_db_info" });

  return (
    <SettingsGroup id="data" title={t("settings.groups.data")} description={t("settings.data.description")}>
      <QueryView query={retention}>{(r) => <RetentionSetting initial={String(r.days)} onChanged={() => void stats.reload()} />}</QueryView>

      <SettingBlock title={t("settings.data.storage")}>
        <QueryView query={stats}>{(s) => <StorageSummary stats={s} />}</QueryView>
      </SettingBlock>

      <Maintenance onDone={() => void stats.reload()} />
      <DeleteHistory onDone={() => void stats.reload()} />
    </SettingsGroup>
  );
}

function RetentionSetting({ initial, onChanged }: { initial: string; onChanged: () => void }) {
  const { t } = useTranslation();
  const setting = useNumberSetting("retentionDays", initial, async (parsed) => {
    await sidecarRequest({ cmd: "set_retention", retentionDays: parsed.value });
    onChanged();
  });
  return (
    <SettingBlock title={t("settings.data.retention")}>
      <NumberSettingField
        setting={setting}
        spec="retentionDays"
        label={t("settings.data.retentionDays")}
        unit={t("settings.data.days")}
        inputMode="numeric"
        help={t("settings.data.retentionHelp")}
      />
    </SettingBlock>
  );
}

function StorageSummary({ stats }: { stats: StorageStatistics }) {
  const { t } = useTranslation();
  const rows: [string, string][] = [
    [t("settings.data.dbSize"), formatBytes(stats.databaseBytes + stats.walBytes)],
    [t("terms.monitoringSessions"), formatNumber(stats.sessions, 0)],
    [t("settings.data.gamingSessions"), formatNumber(stats.gamingSessions, 0)],
    [t("settings.data.buckets"), formatNumber(stats.energyBuckets, 0)],
    [t("settings.data.rawSamples"), formatNumber(stats.powerSamples, 0)],
    [t("settings.data.prices"), formatNumber(stats.prices, 0)],
  ];
  return (
    <>
      <dl className="grid grid-cols-[repeat(auto-fit,minmax(9rem,1fr))] gap-x-6 gap-y-2 text-sm">
        {rows.map(([label, value]) => (
          <div key={label} className="min-w-0">
            <dt className="text-xs text-ink-muted">{label}</dt>
            <dd>
              <Num>{value}</Num>
            </dd>
          </div>
        ))}
      </dl>
      {stats.oldestSampleUtc && stats.newestSampleUtc && (
        <p className="text-xs text-ink-muted">
          {t("settings.data.rawRange")}: <Num>{formatLocalDateTime(stats.oldestSampleUtc)}</Num> – <Num>{formatLocalDateTime(stats.newestSampleUtc)}</Num>
        </p>
      )}
      <p className="max-w-prose text-xs text-ink-muted">{t("settings.data.storageNote")}</p>
    </>
  );
}

/** Routine, non-destructive housekeeping: prune raw samples past retention, return free space, checkpoint, check structure. */
function Maintenance({ onDone }: { onDone: () => void }) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState<"maintenance" | "integrity" | null>(null);
  const [maintenance, setMaintenance] = useState<MaintenanceResult | null>(null);
  const [integrity, setIntegrity] = useState<string | null>(null);
  const [failure, setFailure] = useState<unknown>(null);

  const run = async (kind: "maintenance" | "integrity") => {
    setBusy(kind);
    setFailure(null);
    try {
      if (kind === "maintenance") {
        setMaintenance(await sidecarRequest<MaintenanceResult>({ cmd: "run_maintenance" }));
        onDone();
      } else {
        setIntegrity((await sidecarRequest<{ result: string }>({ cmd: "check_integrity" })).result);
      }
    } catch (e) {
      setFailure(e);
    } finally {
      setBusy(null);
    }
  };

  return (
    <SettingBlock title={t("settings.data.maintenance")}>
      <p className="max-w-prose text-sm text-ink-secondary">{t("settings.data.maintenanceHelp")}</p>
      <div className="flex flex-wrap gap-2">
        <ActionButton className="btn" onClick={() => void run("maintenance")} inactive={busy !== null}>
          {busy === "maintenance" ? t("common.working") : t("settings.data.optimize")}
        </ActionButton>
        <ActionButton className="btn" onClick={() => void run("integrity")} inactive={busy !== null}>
          {busy === "integrity" ? t("common.working") : t("settings.data.checkIntegrity")}
        </ActionButton>
      </div>
      {maintenance && (
        <Notice tone="ok">
          {t("settings.data.maintenanceDone", { count: maintenance.rawSamplesPruned })}{" "}
          <Num>
            {formatBytes(maintenance.bytesBefore)} → {formatBytes(maintenance.bytesAfter)}
          </Num>
        </Notice>
      )}
      {integrity !== null && (integrity === "ok" ? <Notice tone="ok">{t("settings.data.integrityOk")}</Notice> : <Notice tone="error" title={t("settings.data.integrityBad")}>{integrity}</Notice>)}
      {failure !== null && <ErrorPanel error={failure} compact />}
    </SettingBlock>
  );
}

/**
 * The only destructive control in the app. It states what will go and what stays, shows the exact scope
 * measured by the backend (a preview, nothing deleted yet), needs a deliberate confirmation of that scope,
 * only ever touches finished sessions older than the chosen age, and never the running session, prices,
 * settings, game list or the database file itself.
 */
function DeleteHistory({ onDone }: { onDone: () => void }) {
  const { t } = useTranslation();
  const [days, setDays] = useState("365");
  const [preview, setPreview] = useState<HistoryDeletionPreview | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<HistoryDeletionPreview | null>(null);
  const [failure, setFailure] = useState<unknown>(null);

  const parsed = parseSetting(days, "historyDays");
  const error = !parsed.ok && days !== "" ? t(validationKey(parsed.error), validationParams("historyDays")) : null;

  const changeDays = (value: string) => {
    setDays(value);
    setPreview(null); // a confirmation only ever applies to the scope that was shown
    setDone(null);
    setFailure(null);
  };

  const showPreview = async () => {
    if (!parsed.ok) return;
    setBusy(true);
    setFailure(null);
    setDone(null);
    try {
      setPreview(await sidecarRequest<HistoryDeletionPreview>({ cmd: "preview_history_deletion", olderThanDays: parsed.value }));
    } catch (e) {
      setFailure(e);
    } finally {
      setBusy(false);
    }
  };

  const confirmDelete = async () => {
    if (!parsed.ok || !preview) return;
    setBusy(true);
    try {
      const result = await sidecarRequest<HistoryDeletionPreview>({ cmd: "delete_history", olderThanDays: parsed.value, confirmSessions: preview.sessions });
      setDone(result);
      setPreview(null);
      onDone();
    } catch (e) {
      setFailure(e);
    } finally {
      setBusy(false);
      setConfirming(false);
    }
  };

  return (
    <SettingBlock title={t("settings.data.deleteTitle")}>
      <p className="max-w-prose text-sm text-ink-secondary">{t("settings.data.deleteHelp")}</p>
      <div className="flex flex-wrap items-end gap-3">
        <Field label={t("settings.data.deleteOlderThan")} unit={t("settings.data.days")} help={t("settings.data.deleteMin")} error={error}>
          {(props) => <input {...props} className="input w-24" dir="ltr" inputMode="numeric" value={days} onChange={(e) => changeDays(e.target.value)} />}
        </Field>
        <ActionButton className="btn" onClick={() => void showPreview()} inactive={busy} disabled={!parsed.ok}>
          {t("settings.data.deletePreview")}
        </ActionButton>
      </div>

      {preview &&
        (preview.sessions === 0 ? (
          <Notice tone="info">{t("settings.data.deleteNothing")}</Notice>
        ) : (
          <Notice tone="warn" title={t("settings.data.deleteScope", { count: preview.sessions })}>
            <ul className="list-inside list-disc">
              <li>
                <Num>{formatNumber(preview.energyBuckets, 0)}</Num> {t("settings.data.buckets")}
              </li>
              <li>
                <Num>{formatNumber(preview.gamingSessions, 0)}</Num> {t("settings.data.gamingSessions")}
              </li>
              <li>
                <Num>{formatNumber(preview.powerSamples, 0)}</Num> {t("settings.data.rawSamples")}
              </li>
            </ul>
            <div className="mt-2">
              <ActionButton className="btn btn-danger" onClick={() => setConfirming(true)} inactive={busy}>
                {t("settings.data.deleteButton")}
              </ActionButton>
            </div>
          </Notice>
        ))}
      {done && <Notice tone="ok">{t("settings.data.deleteDone", { count: done.sessions })}</Notice>}
      {failure !== null && <ErrorPanel error={failure} compact />}

      <ConfirmDialog open={confirming} title={t("settings.data.confirmTitle")} confirmLabel={t("settings.data.confirmDelete")} destructive busy={busy} onCancel={() => setConfirming(false)} onConfirm={() => void confirmDelete()}>
        {preview && (
          <>
            <p>{t("settings.data.confirmBody", { count: preview.sessions })}</p>
            <ul className="list-inside list-disc">
              <li>
                <Num>{formatNumber(preview.energyBuckets, 0)}</Num> {t("settings.data.buckets")}
              </li>
              <li>
                <Num>{formatNumber(preview.gamingSessions, 0)}</Num> {t("settings.data.gamingSessions")}
              </li>
              <li>
                <Num>{formatNumber(preview.powerSamples, 0)}</Num> {t("settings.data.rawSamples")}
              </li>
            </ul>
            {preview.oldestStartedUtc && preview.newestEndedUtc && (
              <p>
                <Num>{formatLocalDateTime(preview.oldestStartedUtc)}</Num> – <Num>{formatLocalDateTime(preview.newestEndedUtc)}</Num>
              </p>
            )}
            <p>{t("settings.data.confirmReports")}</p>
            <p>{t("settings.data.confirmKept")}</p>
            <p className="font-semibold text-red-200">{t("settings.data.confirmIrreversible")}</p>
          </>
        )}
      </ConfirmDialog>
    </SettingBlock>
  );
}
