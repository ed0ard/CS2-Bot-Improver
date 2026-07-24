import { useEffect, useState } from "react";
import { BadgeInfo, Download, FileCheck2, FlaskConical, FolderOpen, Languages, Palette, type LucideIcon } from "lucide-react";
import { BackIcon, ChevronRight } from "../../components/icons";
import AboutPage, { type AboutTarget } from "./AboutPage";
import AboutDetailPage from "./AboutDetailPage";
import LanguagesPage from "./LanguagesPage";
import DirectoryPage from "./DirectoryPage";
import InstallationPage from "./InstallationPage";
import OnlineUpdatePage from "./OnlineUpdatePage";
import ExperimentalPage from "./ExperimentalPage";
import PersonalizationPage from "./PersonalizationPage";
import { api, type OnlineUpdateSnapshot } from "../../lib/api";
import { useStore } from "../../state/store";
import { LANGUAGES } from "../../data/languages";
import { useT, type I18nKey } from "../../i18n";
import "./settings.css";

type SettingsEntry = "about" | "languages" | "personalization" | "directory" | "installation" | "updates" | "experimental";
type Page = "root" | SettingsEntry | AboutTarget;

const TITLE_KEYS: Record<Page, I18nKey> = {
  root: "set.title",
  about: "set.about",
  aboutThirdParty: "set.thirdParty",
  aboutAgreement: "set.userAgreement",
  aboutPrivacy: "set.privacyPolicy",
  languages: "set.languages",
  personalization: "personal.title",
  directory: "set.directory",
  installation: "set.installation",
  updates: "set.updates",
  experimental: "experimental.title",
};

const DESC_KEYS: Record<SettingsEntry, I18nKey> = {
  updates: "set.updatesDesc",
  installation: "set.installationDesc",
  directory: "set.directoryDesc",
  languages: "set.languagesDesc",
  personalization: "personal.settingsDesc",
  about: "set.aboutDesc",
  experimental: "experimental.settingsDesc",
};

const ICONS: Record<SettingsEntry, LucideIcon> = {
  updates: Download,
  installation: FileCheck2,
  directory: FolderOpen,
  languages: Languages,
  personalization: Palette,
  about: BadgeInfo,
  experimental: FlaskConical,
};

type Tone = "green" | "yellow" | "blue" | "neutral";

export default function SettingsView({ onClose }: { onClose?: () => void }) {
  const [page, setPage] = useState<Page>("root");
  const { config, directory, installation } = useStore();
  const [updates, setUpdates] = useState<OnlineUpdateSnapshot | null>(null);
  const t = useT();
  const back = () => {
    if (page === "root") onClose?.();
    else if (page === "aboutThirdParty" || page === "aboutAgreement" || page === "aboutPrivacy") setPage("about");
    else setPage("root");
  };

  useEffect(() => {
    if (page !== "root") return;
    void api.getUpdateSnapshot().then(setUpdates).catch(() => {});
  }, [page]);

  const updateAvailable = !!updates && (updates.panel.update_available || updates.plugin.update_available);
  const language = LANGUAGES.find((entry) => entry.code === config?.language);

  const STATUS: Record<SettingsEntry, { text: string; tone: Tone } | null> = {
    updates: updates
      ? { text: t(updateAvailable ? "update.available" : "update.current"), tone: updateAvailable ? "yellow" : "green" }
      : null,
    installation: installation?.installed
      ? { text: `v${installation.package_version}`, tone: installation.missing.length + installation.corrupt.length > 0 ? "yellow" : "green" }
      : { text: t("install.notInstalled"), tone: "neutral" },
    directory: directory?.valid && directory.selected
      ? { text: directory.selected, tone: "blue" }
      : { text: t("set.noCsgo"), tone: "yellow" },
    languages: language ? { text: language.native, tone: "blue" } : null,
    personalization: { text: t("personal.live"), tone: "blue" },
    about: null,
    experimental: config?.experimental_features_enabled
      ? { text: t("cosmetics.enabled"), tone: "yellow" }
      : { text: t("cosmetics.disabled"), tone: "neutral" },
  };

  return (
    <div className="settings">
      <div className="settings__head">
        {(page !== "root" || onClose) && (
          <button className="settings__back" onClick={back} aria-label="Back">
            <BackIcon size={20} />
          </button>
        )}
        <span className="settings__title" key={page}>{t(TITLE_KEYS[page])}</span>
      </div>

      <div className="settings__body">
        <div className="settings__page" key={page}>
          {page === "root" && (
            <>
              <div className="settings-list">
                {(["updates", "installation", "directory", "languages", "personalization", "experimental"] as SettingsEntry[]).map((p) => {
                  const Icon = ICONS[p];
                  const status = STATUS[p];
                  return (
                    <button key={p} className="set-card" onClick={() => setPage(p)}>
                      <span className={`set-card__icon set-card__icon--${p}`} aria-hidden="true">
                        <Icon size={20} strokeWidth={1.9} />
                      </span>
                      <span className="set-card__body">
                        <strong>{t(TITLE_KEYS[p])}</strong>
                        <small>{t(DESC_KEYS[p])}</small>
                        {status && <em className={`set-card__status is-${status.tone}`}>{status.text}</em>}
                      </span>
                      <ChevronRight size={18} className="set-card__chevron" />
                    </button>
                  );
                })}
              </div>
              <button className="set-card set-card--about" onClick={() => setPage("about")}>
                <span className="set-card__icon set-card__icon--about" aria-hidden="true"><BadgeInfo size={20} strokeWidth={1.9} /></span>
                <span className="set-card__body"><strong>{t("set.about")}</strong><small>{t("set.aboutDesc")}</small></span>
                <ChevronRight size={18} className="set-card__chevron" />
              </button>
            </>
          )}
          {page === "about" && <AboutPage onOpen={setPage} onOpenUpdates={() => setPage("updates")} />}
          {(page === "aboutThirdParty" || page === "aboutAgreement" || page === "aboutPrivacy") && (
            <AboutDetailPage kind={page} />
          )}
          {page === "languages" && <LanguagesPage />}
          {page === "personalization" && <PersonalizationPage />}
          {page === "directory" && <DirectoryPage />}
          {page === "installation" && <InstallationPage />}
          {page === "updates" && <OnlineUpdatePage />}
          {page === "experimental" && <ExperimentalPage />}
        </div>
      </div>
    </div>
  );
}
