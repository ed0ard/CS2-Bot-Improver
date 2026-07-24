import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, ArrowRight, Download, ExternalLink, PackageCheck, RefreshCw, X } from "lucide-react";
import { api, toAppError, type OnlineUpdateSnapshot, type UpdateProgress } from "../../lib/api";
import { useStore } from "../../state/store";
import { useT } from "../../i18n";
import { listenAppEvent, openExternalUrl } from "../../lib/platform";

function formatBytes(value: number) {
  if (!value) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  return `${(value / 1024 ** index).toFixed(index ? 1 : 0)} ${units[index]}`;
}

function formatTime(value: number | null) {
  return value ? new Date(value * 1000).toLocaleString() : "--";
}

export default function OnlineUpdatePage() {
  const { csgoPath, process } = useStore();
  const t = useT();
  const [snapshot, setSnapshot] = useState<OnlineUpdateSnapshot | null>(null);
  const [localError, setLocalError] = useState<string | null>(null);
  const [working, setWorking] = useState<string | null>(null);

  const refreshSnapshot = useCallback(async () => {
    try { setSnapshot(await api.getUpdateSnapshot()); } catch { /* startup checks stay silent */ }
  }, []);

  useEffect(() => {
    void refreshSnapshot();
    const unlisten = listenAppEvent<UpdateProgress>("update-progress", () => void refreshSnapshot());
    return () => { void unlisten.then((dispose) => dispose()); };
  }, [refreshSnapshot]);

  const check = async () => {
    setWorking("check");
    setLocalError(null);
    try { setSnapshot(await api.checkOnlineUpdates(true)); }
    catch (error) { setLocalError(toAppError(error).detail); await refreshSnapshot(); }
    finally { setWorking(null); }
  };

  const installAll = async () => {
    setWorking("all");
    setLocalError(null);
    try {
      await api.installAllUpdates(snapshot?.plugin.update_available ? csgoPath : null);
      await refreshSnapshot();
    } catch (error) {
      setLocalError(toAppError(error).detail);
      await refreshSnapshot();
    } finally { setWorking(null); }
  };

  const cancel = async () => {
    await api.cancelUpdate();
    setWorking(null);
  };

  const blocked = !!process?.running && (process.matches_selected || !process.path_accessible);
  const panelAvailable = !!snapshot?.panel.update_available;
  const pluginAvailable = !!snapshot?.plugin.update_available;
  const hasUpdates = panelAvailable || pluginAvailable;
  const pluginNeedsDirectory = pluginAvailable && !csgoPath;
  const pluginIncompatible = pluginAvailable && snapshot?.plugin.compatible === false;
  const panelIncompatible = panelAvailable && snapshot?.panel.compatible === false;
  const canInstallAll = hasUpdates && !snapshot?.busy && !working
    && !pluginNeedsDirectory && !(pluginAvailable && blocked)
    && !pluginIncompatible && !panelIncompatible;
  const blockingNote = pluginNeedsDirectory ? t("update.selectDirectory")
    : pluginAvailable && blocked ? t("update.closeCs2")
      : pluginIncompatible || panelIncompatible ? t("update.incompatible")
        : null;
  const componentSection = (component: "panel" | "plugin") => {
    const state = snapshot?.[component];
    const progress = state?.total_bytes
      ? Math.min(100, Math.round((state.downloaded_bytes / state.total_bytes) * 100)) : 0;
    const installing = !!snapshot?.busy && ["downloading", "extracting"].includes(state?.status ?? "");
    const available = !!state?.update_available;
    return (
      <section className="upd-card" key={component}>
        <div className="upd-card__head">
          <span className={`upd-card__icon upd-card__icon--${component}`}>
            {component === "panel" ? <Download size={20} /> : <PackageCheck size={20} />}
          </span>
          <div>
            <strong>{component === "panel" ? t("update.panel") : t("update.plugin")}</strong>
            <small>{component === "panel" ? t("update.panelDesc") : t("update.pluginDesc")}</small>
          </div>
          <span className={`update-status update-status--${state?.status ?? "idle"}`}>
            {available ? t("update.available") : t("update.current")}
          </span>
        </div>

        <div className="upd-card__versions">
          <span className="upd-ver">
            <small>{t("update.currentVersion")}</small>
            <strong>{state?.current_version ?? "--"}</strong>
          </span>
          <ArrowRight size={18} className={`upd-arrow ${available ? "is-hot" : ""}`} aria-hidden="true" />
          <span className="upd-ver">
            <small>{t("update.latestVersion")}</small>
            <strong className={available ? "is-new" : ""}>{state?.latest_version ?? "--"}</strong>
          </span>
          <span className="upd-ver upd-ver--size">
            <small>{t("update.size")}</small>
            <strong>{formatBytes(state?.total_bytes ?? 0)}</strong>
          </span>
        </div>

        {(installing || !!state?.downloaded_bytes) && (
          <div className="update-progress">
            <div><span style={{ width: `${progress}%` }} /></div>
            <small>{state?.status === "extracting" ? t("update.extracting") : t("update.downloading", { n: progress })}</small>
          </div>
        )}

        {component === "plugin" && blocked && (
          <p className="upd-note"><AlertTriangle size={14} aria-hidden="true" />{t("update.closeCs2")}</p>
        )}
        {state && !state.compatible && (
          <p className="upd-note"><AlertTriangle size={14} aria-hidden="true" />{t("update.panelRequired")}</p>
        )}

      </section>
    );
  };

  return (
    <div className="online-update-page">
      <div className="update-toolbar">
        <span><small>{t("update.lastChecked")}</small><strong>{formatTime(snapshot?.checked_at ?? null)}</strong></span>
        <div>
          {snapshot?.release_notes_url && (
            <button title={t("update.releaseNotes")} onClick={() => openExternalUrl(snapshot.release_notes_url!)}>
              <ExternalLink size={16} />{t("update.releaseNotes")}
            </button>
          )}
          <button disabled={working === "check" || snapshot?.busy} onClick={check}>
            <RefreshCw size={16} />{working === "check" ? t("update.checking") : t("update.check")}
          </button>
        </div>
      </div>
      {(localError || snapshot?.error) && <div className="update-error">{localError ?? snapshot?.error}</div>}
      <section className="update-primary-action">
        <span>
          <strong>{t("update.allTitle")}</strong>
          <small>{t("update.allDesc")}</small>
        </span>
        <div>
          <button className="is-primary" disabled={!canInstallAll} onClick={installAll}>
            <Download size={16} />
            {working === "all" ? t("update.updatingAll") : hasUpdates ? t("update.installAll") : t("update.current")}
          </button>
          {working === "all" && <button onClick={cancel}><X size={16} />{t("update.cancel")}</button>}
        </div>
      </section>
      {blockingNote && <p className="upd-note update-primary-note"><AlertTriangle size={14} aria-hidden="true" />{blockingNote}</p>}
      <div className="upd-grid">
        {componentSection("panel")}
        {componentSection("plugin")}
      </div>
    </div>
  );
}
