import Section from "../components/Section";
import Segmented from "../components/Segmented";
import SubPage from "../components/SubPage";
import Toggle, { type Tone } from "../components/Toggle";
import { useStore } from "../state/store";
import { useT, type I18nKey } from "../i18n";
import type { AimValue, BotItemKey, NadesValue } from "../lib/api";
import type { Status } from "../components/StatusDot";
import "./PresetsPanel.css";
import "./BotItemsPanel.css";

const AIM: { value: AimValue; labelKey: I18nKey; descriptionKey: I18nKey }[] = [
  { value: "head", labelKey: "pre.aimHead", descriptionKey: "pre.aimHeadDesc" },
  { value: "mixed", labelKey: "pre.aimMixed", descriptionKey: "pre.aimMixedDesc" },
  { value: "body", labelKey: "pre.aimBody", descriptionKey: "pre.aimBodyDesc" },
];
const NADES: { value: NadesValue; labelKey: I18nKey; descriptionKey: I18nKey }[] = [
  { value: "max", labelKey: "pre.nadesMax", descriptionKey: "pre.nadesMaxDesc" },
  { value: "more", labelKey: "pre.nadesMore", descriptionKey: "pre.nadesMoreDesc" },
  { value: "normal", labelKey: "pre.nadesNormal", descriptionKey: "pre.nadesNormalDesc" },
  { value: "off", labelKey: "pre.nadesOff", descriptionKey: "pre.nadesOffDesc" },
];

const ITEMS: { key: BotItemKey; labelKey: I18nKey }[] = [
  { key: "skins", labelKey: "bi.skins" },
  { key: "profiles", labelKey: "bi.profiles" },
  { key: "agents", labelKey: "bi.agents" },
  { key: "music", labelKey: "bi.music" },
];

export default function PresetsPanel({ onBack }: { onBack?: () => void }) {
  const {
    presets,
    config,
    botItems,
    csgoPath,
    applyAim,
    applyNades,
    applyBotItem,
    aimPending,
    nadesPending,
    botItemsPending,
  } = useStore();
  const t = useT();

  const cfgPresent = presets?.cfg_present ?? false;
  const running = presets?.cs2_running ?? false;
  const disabled = !csgoPath || !cfgPresent;
  const aimSupported = presets?.aim_supported ?? false;
  const aimRuntimeActive = presets?.aim_active;

  // aimPending / nadesPending live in the global store, so each section's
  // pending-restart flag survives leaving and returning to this panel. A section
  // turns yellow only when *that* setting was changed while CS2 is running, so
  // the Aim and Nades lights stay independent.

  // Per-section light: green when writable, yellow only if this section has a
  // change pending a restart, off/red when no path/cfg.
  const statusFor = (pending: boolean): Status =>
    !csgoPath ? "off" : !cfgPresent ? "red" : running && pending ? "yellow" : "green";
  const aim: AimValue = presets?.aim ?? ((config?.aim as AimValue | null) ?? "mixed");
  const nades: NadesValue =
    presets?.nades ?? ((config?.nades as NadesValue | null) ?? "normal");
  const aimOption = AIM.find(({ value }) => value === aim) ?? AIM[1];
  const aimRuntimeDetail = !aimSupported
    ? t("pre.aimUnavailable")
    : aimRuntimeActive === true
    ? t("pre.aimRuntimeActive", {
        count: presets?.aim_override_count ?? 0,
        errors: presets?.aim_error_count ?? 0,
      })
    : aimRuntimeActive === false
    ? t("pre.aimRuntimeInactive")
    : t("pre.aimRuntimeUnknown");
  const nadesOption = NADES.find(({ value }) => value === nades) ?? NADES[2];
  const botItemsCfgPresent = botItems?.cfg_present ?? false;
  const botItemsRunning = botItems?.cs2_running ?? false;
  const itemPending = (key: BotItemKey) => botItemsRunning && botItemsPending[key];
  const itemsStatus: Status = !csgoPath
    ? "off"
    : !botItemsCfgPresent
    ? "red"
    : ITEMS.some(({ key }) => itemPending(key))
    ? "yellow"
    : "green";

  return (
    <SubPage title={t("pre.title")} onBack={onBack}>
      <div className="presets__controls">
        <Section
          title={t("pre.aim")}
          status={!aimSupported ? "off" : aimRuntimeActive === false ? "red" : statusFor(aimPending)}
        >
          <Segmented
            ariaLabel={t("pre.aim")}
            value={aim}
            onChange={(v) => applyAim(v)}
            disabled={disabled || !aimSupported}
            options={AIM.map(({ value, labelKey }) => ({ value, label: t(labelKey) }))}
          />
          <p className="selection-detail" aria-live="polite">
            {aimSupported ? t(aimOption.descriptionKey) : aimRuntimeDetail}
          </p>
          <p className="selection-detail" aria-live="polite">
            {aimRuntimeDetail}
          </p>
          <p className="selection-detail">{t("pre.appliesNextLaunch")}</p>
        </Section>

        <Section title={t("pre.nades")} status={statusFor(nadesPending)}>
          <Segmented
            ariaLabel={t("pre.nades")}
            value={nades}
            onChange={(v) => applyNades(v)}
            disabled={disabled}
            options={NADES.map(({ value, labelKey }) => ({ value, label: t(labelKey) }))}
          />
          <p className="selection-detail" aria-live="polite">
            {t(nadesOption.descriptionKey)}
          </p>
        </Section>

        <Section title={t("bi.title")} status={itemsStatus}>
          <div className="botitems-grid">
            {ITEMS.map(({ key, labelKey }) => {
              const on = (botItems?.[key] as boolean | undefined) ?? false;
              const tone: Tone = !botItemsCfgPresent
                ? "red"
                : itemPending(key)
                ? "yellow"
                : "green";
              return (
                <div className="botitem" key={key}>
                  <span className="botitem__label">{t(labelKey)}</span>
                  <Toggle
                    ariaLabel={t(labelKey)}
                    checked={on}
                    tone={tone}
                    disabled={!csgoPath || !botItemsCfgPresent}
                    onChange={(next) => applyBotItem(key, next)}
                  />
                </div>
              );
            })}
          </div>
        </Section>
      </div>
    </SubPage>
  );
}
