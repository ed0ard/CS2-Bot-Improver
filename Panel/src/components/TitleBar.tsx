import { useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import { ask } from "@tauri-apps/plugin-dialog";
import { openUrl } from "@tauri-apps/plugin-opener";
import { GearIcon, MinusIcon, CloseIcon } from "./icons";
import Modal from "./Modal";
import { useStore } from "../state/store";
import { toAppError } from "../lib/api";
import { useT } from "../i18n";
import "./TitleBar.css";

const RELEASES_URL = "https://github.com/ed0ard/CS2-Bot-Improver/releases/latest";

type Props = {
  title?: string;
  onSettings: () => void;
};

/** Mirrors Rust `UpdateResult` (auto_update.rs). */
type UpdateResult = {
  version: string;
  asset: string;
  backedUp: boolean;
};

type Notice = { ok: boolean; text: string };

export default function TitleBar({ title = "CS2 Bot Improver v1.4.3", onSettings }: Props) {
  const appWindow = getCurrentWindow();
  const t = useT();
  const { csgoPath, ready, refreshAll } = useStore();
  const [isUpdating, setIsUpdating] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);

  const runUpdate = async () => {
    if (isUpdating) return;
    setIsUpdating(true);
    try {
      const confirmed = await ask(t("up.confirm"), {
        title: t("up.title"),
        kind: "info",
      });
      if (!confirmed) return;

      try {
        // May not exist: the original Rust backend was never open-sourced.
        if (!ready || !csgoPath) throw new Error(t("up.noDir"));
        const result = await invoke<UpdateResult>("perform_auto_update", {
          targetDir: csgoPath,
        });
        setNotice({ ok: true, text: t("up.success", { version: result.version }) });
        void refreshAll();
      } catch {
        // Graceful degradation: no native command / no permission for big IO.
        setNotice({ ok: false, text: t("up.fallback") });
        await openUrl(RELEASES_URL);
      }
    } catch (e) {
      // The confirmation dialog itself failed — still surface a concrete error.
      const err = toAppError(e);
      setNotice({ ok: false, text: `${t("up.failed")}: ${err.detail}` });
    } finally {
      setIsUpdating(false);
    }
  };

  return (
    <>
      <header className="titlebar" data-tauri-drag-region>
        <span className="titlebar__title" data-tauri-drag-region>
          {title}
        </span>
        <div className="titlebar__controls">
          <button
            className="tb-update"
            title={t("tb.update")}
            aria-label={t("tb.update")}
            aria-busy={isUpdating}
            disabled={isUpdating}
            onClick={runUpdate}
          >
            {isUpdating ? (
              <>
                <span className="tb-update__spinner" aria-hidden="true" />
                {t("tb.updating")}
              </>
            ) : (
              t("tb.update")
            )}
          </button>
          <button
            className="tl tl--green"
            title={t("tb.settings")}
            aria-label={t("tb.settings")}
            onClick={onSettings}
          >
            <GearIcon size={12} />
          </button>
          <button
            className="tl tl--yellow"
            title={t("tb.minimize")}
            aria-label={t("tb.minimize")}
            onClick={() => appWindow.minimize()}
          >
            <MinusIcon size={12} />
          </button>
          <button
            className="tl tl--red"
            title={t("tb.close")}
            aria-label={t("tb.close")}
            onClick={() => appWindow.close()}
          >
            <CloseIcon size={12} />
          </button>
        </div>
      </header>

      <Modal
        open={!!notice}
        title={t("up.title")}
        onClose={() => setNotice(null)}
        width={420}
        footer={
          <button className="btn-primary" onClick={() => setNotice(null)}>
            {t("common.ok")}
          </button>
        }
      >
        <p className={`up-notice ${notice?.ok ? "up-notice--ok" : "up-notice--err"}`}>
          {notice?.text}
        </p>
      </Modal>
    </>
  );
}
