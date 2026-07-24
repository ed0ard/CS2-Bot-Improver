import { useRef, useState, type ChangeEvent } from "react";
import { Check, Download, Image, LoaderCircle, RotateCcw, Shield, Type, Upload, X } from "lucide-react";
import Modal from "../../components/Modal";
import Segmented from "../../components/Segmented";
import { useToast } from "../../components/Toast";
import { useT, type I18nKey } from "../../i18n";
import {
  api,
  type AppearanceCustomFont,
  type AppearanceConfig,
  type AppearanceDensity,
  type AppearanceFont,
  type AppearanceLevel,
  type AppearanceMotion,
  type AppearanceStyle,
} from "../../lib/api";
import {
  DEFAULT_APPEARANCE,
  PALETTES,
} from "../../lib/appearance";
import { openDialog, saveDialog } from "../../lib/platform";
import { applyTeamTheme, TEAM_THEMES } from "../../lib/teamThemes";
import { useAppearance } from "../../state/appearance";
import { useStore } from "../../state/store";
import appLogo from "../../assets/app-logo.png";
import "./PersonalizationPage.css";

const STYLES: { value: AppearanceStyle; title: I18nKey; desc: I18nKey }[] = [
  { value: "paper", title: "personal.style.paper", desc: "personal.style.paperDesc" },
  { value: "clean", title: "personal.style.clean", desc: "personal.style.cleanDesc" },
  { value: "compact", title: "personal.style.compact", desc: "personal.style.compactDesc" },
  { value: "immersive", title: "personal.style.immersive", desc: "personal.style.immersiveDesc" },
];

const FONTS: { value: AppearanceFont; title: I18nKey; sample: string }[] = [
  { value: "humanist", title: "personal.font.humanist", sample: "Local Arena 人文" },
  { value: "modern", title: "personal.font.modern", sample: "Local Arena 现代" },
  { value: "clear", title: "personal.font.clear", sample: "Local Arena 清晰" },
  { value: "classic", title: "personal.font.classic", sample: "Local Arena 经典" },
  { value: "technical", title: "personal.font.technical", sample: "LOCAL ARENA 01" },
];

const PALETTE_KEYS: Record<string, I18nKey> = {
  terracotta: "personal.palette.terracotta",
  sky: "personal.palette.sky",
  monochrome: "personal.palette.monochrome",
  grass: "personal.palette.grass",
  mist: "personal.palette.mist",
  berry: "personal.palette.berry",
};

const FONT_FORMATS = {
  ttf: "font/ttf",
  otf: "font/otf",
  woff: "font/woff",
  woff2: "font/woff2",
} as const;

function readDataUrl(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => typeof reader.result === "string" ? resolve(reader.result) : reject(new Error("image-read"));
    reader.onerror = () => reject(reader.error ?? new Error("image-read"));
    reader.readAsDataURL(blob);
  });
}

async function imageData(file: File, kind: "background" | "logo"): Promise<string> {
  if (!(["image/png", "image/jpeg", "image/webp"] as string[]).includes(file.type) || file.size <= 0) {
    throw new Error("unsupported-image");
  }
  const maxBytes = kind === "background" ? 8 * 1024 * 1024 : 2 * 1024 * 1024;
  const maxSide = kind === "background" ? 1920 : 512;
  const bitmap = await createImageBitmap(file).catch(() => null);
  if (!bitmap || bitmap.width <= 0 || bitmap.height <= 0) {
    bitmap?.close();
    throw new Error("unsupported-image");
  }
  const largestSide = Math.max(bitmap.width, bitmap.height);
  if (file.size <= maxBytes && largestSide <= maxSide) {
    bitmap.close();
    return readDataUrl(file);
  }
  const scale = Math.min(1, maxSide / largestSide);
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(bitmap.width * scale));
  canvas.height = Math.max(1, Math.round(bitmap.height * scale));
  const context = canvas.getContext("2d");
  if (!context) {
    bitmap.close();
    throw new Error("unsupported-image");
  }
  context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  let quality = kind === "background" ? 0.85 : 0.92;
  let blob: Blob | null = null;
  for (let attempt = 0; attempt < 3 && !blob; attempt += 1) {
    const encoded = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, "image/webp", quality));
    if (!encoded) throw new Error("unsupported-image");
    if (encoded.size <= maxBytes) blob = encoded;
    else quality *= 0.7;
  }
  if (!blob) throw new Error("unsupported-image");
  return readDataUrl(blob);
}

async function fontData(file: File): Promise<AppearanceCustomFont> {
  const format = file.name.split(".").pop()?.toLowerCase() as keyof typeof FONT_FORMATS | undefined;
  if (!format || !(format in FONT_FORMATS) || file.size <= 0 || file.size > 24 * 1024 * 1024) {
    throw new Error("unsupported-font");
  }
  const bytes = new Uint8Array(await file.arrayBuffer());
  const magic = String.fromCharCode(...bytes.slice(0, 4));
  const valid = format === "ttf"
    ? (bytes[0] === 0 && bytes[1] === 1 && bytes[2] === 0 && bytes[3] === 0) || magic === "true"
    : format === "otf" ? magic === "OTTO"
      : format === "woff" ? magic === "wOFF"
        : magic === "wOF2";
  if (!valid) throw new Error("font-signature");
  const dataUrl = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => typeof reader.result === "string" ? resolve(reader.result) : reject(new Error("font-read"));
    reader.onerror = () => reject(reader.error ?? new Error("font-read"));
    reader.readAsDataURL(new Blob([bytes], { type: FONT_FORMATS[format] }));
  });
  return { data_url: dataUrl, file_name: file.name.slice(0, 128), format };
}

export default function PersonalizationPage() {
  const t = useT();
  const toast = useToast();
  const { reportError } = useStore();
  const { appearance, updateAppearance, replaceAppearance, resetAppearance } = useAppearance();
  const [busy, setBusy] = useState<"import" | "export" | null>(null);
  const [teamBusy, setTeamBusy] = useState<string | null>(null);
  const [pendingTheme, setPendingTheme] = useState<string | null>(null);
  const teamBusyRef = useRef(false);

  const update = <K extends keyof AppearanceConfig>(key: K, value: AppearanceConfig[K]) => {
    updateAppearance((current) => ({ ...current, [key]: value }));
  };

  const chooseImage = async (event: ChangeEvent<HTMLInputElement>, kind: "background" | "logo") => {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) return;
    try {
      const dataUrl = await imageData(file, kind);
      if (kind === "background") {
        updateAppearance((current) => ({
          ...current,
          style: "immersive",
          background: { data_url: dataUrl, fit: "cover", position_x: 50, position_y: 50, dim: 18, blur: 0 },
        }));
      } else {
        update("logo", { data_url: dataUrl, fit: "contain", shape: "rounded" });
      }
      toast.show(t(kind === "background" ? "personal.backgroundAdded" : "personal.logoAdded"), "green");
    } catch {
      toast.show(t(kind === "background" ? "personal.backgroundInvalid" : "personal.logoInvalid"), "red");
    }
  };

  const chooseFont = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) return;
    try {
      const customFont = await fontData(file);
      updateAppearance((current) => ({ ...current, font: "custom", custom_font: customFont }));
      toast.show(t("personal.customFontAdded"), "green");
    } catch {
      toast.show(t("personal.customFontInvalid"), "red");
    }
  };

  const removeCustomFont = () => {
    updateAppearance((current) => ({
      ...current,
      font: current.font === "custom" ? "humanist" : current.font,
      custom_font: null,
    }));
  };

  const exportTheme = async () => {
    if (busy) return;
    setBusy("export");
    try {
      const destination = await saveDialog({ defaultPath: "local-arena-theme.latheme", filters: [{ name: "Local Arena Theme", extensions: ["latheme"] }] });
      if (!destination) return;
      await api.exportAppearance(destination);
      toast.show(t("personal.exported"), "green");
    } catch (error) {
      reportError(error);
    } finally {
      setBusy(null);
    }
  };

  const importTheme = async () => {
    if (busy) return;
    const source = await openDialog({ multiple: false, directory: false, filters: [{ name: "Local Arena Theme", extensions: ["latheme"] }] });
    if (!source || Array.isArray(source)) return;
    setBusy("import");
    try {
      replaceAppearance(await api.importAppearance(source));
      toast.show(t("personal.imported"), "green");
    } catch (error) {
      reportError(error);
    } finally {
      setBusy(null);
    }
  };

  const reset = () => {
    resetAppearance();
    toast.show(t("personal.resetDone"), "green");
  };

  const applyTheme = async (themeId: string) => {
    if (teamBusyRef.current) return;
    const theme = TEAM_THEMES.find((entry) => entry.id === themeId);
    if (!theme) return;
    teamBusyRef.current = true;
    setTeamBusy(theme.id);
    try {
      replaceAppearance(await applyTeamTheme(appearance, theme));
      toast.show(`${t("personal.teamApplied")} ${theme.name}`, "green");
    } catch (error) {
      reportError(error);
      toast.show(t("personal.teamApplyFailed"), "red");
    } finally {
      teamBusyRef.current = false;
      setTeamBusy(null);
    }
  };

  const chooseTeamTheme = (themeId: string) => {
    if (teamBusyRef.current) return;
    if (themeId !== appearance.team_theme && (appearance.logo || appearance.background)) {
      setPendingTheme(themeId);
      return;
    }
    void applyTheme(themeId);
  };

  const previewStyle = appearance.background ? {
    backgroundImage: `linear-gradient(rgba(0, 0, 0, ${appearance.background.dim / 100}), rgba(0, 0, 0, ${appearance.background.dim / 100})), url("${appearance.background.data_url}")`,
    backgroundSize: appearance.background.fit,
    backgroundPosition: `${appearance.background.position_x}% ${appearance.background.position_y}%`,
  } : undefined;

  return <div className="personal-page">
    <section className="personal-preview" aria-label={t("personal.preview")} style={previewStyle}>
      <aside className="personal-preview__rail">
        <span className="personal-preview__brand">
          <img src={appearance.logo?.data_url || appLogo} alt="" style={{ objectFit: appearance.logo?.fit ?? "cover", borderRadius: appearance.logo?.shape === "circle" ? "50%" : appearance.logo?.shape === "square" ? 0 : undefined }} />
          <strong>{appearance.brand_name}</strong>
        </span>
        <i className="is-active" /><i /><i /><i />
      </aside>
      <div className="personal-preview__workspace">
        <small>{t("personal.previewEyebrow")}</small>
        <strong>{t("personal.previewTitle")}</strong>
        <span className="personal-preview__status"><i /><b /><b /><b /></span>
        <div className="personal-preview__cards"><i /><i /></div>
      </div>
      <em>{t("personal.livePreview")}</em>
    </section>

    <section className="personal-section">
      <header><span><Shield size={17} /><strong>{t("personal.teamThemes")}</strong></span><small>{t("personal.teamThemesDesc")}</small></header>
      <div className="personal-team-grid">
        {TEAM_THEMES.map((theme) => {
          const active = appearance.team_theme === theme.id;
          const loading = teamBusy === theme.id;
          return <button
            key={theme.id}
            className={active ? "personal-team-card is-active" : "personal-team-card"}
            disabled={!!teamBusy}
            onClick={() => void chooseTeamTheme(theme.id)}
            aria-pressed={active}
          >
            <span className="personal-team-card__visual"><img src={theme.background} alt="" loading="lazy" /></span>
            <span className="personal-team-card__meta">
              <strong>{theme.name}</strong>
              <span className="personal-team-card__swatches" aria-hidden="true">
                {theme.colors.map((color) => <i key={color} style={{ background: color }} />)}
              </span>
              <small>{loading ? <LoaderCircle className="is-spinning" size={14} /> : active ? <Check size={14} /> : null}{loading ? t("personal.teamApplying") : active ? t("personal.teamActive") : t("personal.teamApply")}</small>
            </span>
          </button>;
        })}
      </div>
    </section>

    <section className="personal-section">
      <header><span><Type size={17} /><strong>{t("personal.identity")}</strong></span><small>{t("personal.identityDesc")}</small></header>
      <div className="personal-field personal-field--wide">
        <label htmlFor="personal-brand-name">{t("personal.brandName")}</label>
        <input id="personal-brand-name" value={appearance.brand_name} maxLength={32} onChange={(event) => update("brand_name", event.target.value || DEFAULT_APPEARANCE.brand_name)} />
        <small>{t("personal.brandNameDesc")}</small>
      </div>
      <div className="personal-asset-row">
        <span className="personal-asset-preview"><img src={appearance.logo?.data_url || appLogo} alt="" /></span>
        <span><strong>{t("personal.logo")}</strong><small>{t("personal.logoDesc")}</small></span>
        <label className="personal-button"><Upload size={15} />{t("personal.chooseImage")}<input type="file" accept="image/png,image/jpeg,image/webp" onChange={(event) => void chooseImage(event, "logo")} /></label>
        {appearance.logo && <button className="personal-icon-button" onClick={() => update("logo", null)} title={t("personal.removeLogo")}><X size={16} /></button>}
      </div>
      {appearance.logo && <Segmented value={appearance.logo.shape} ariaLabel={t("personal.logoShape")} onChange={(shape) => update("logo", { ...appearance.logo!, shape })} options={[
        { value: "rounded", label: t("personal.shapeRounded") }, { value: "square", label: t("personal.shapeSquare") }, { value: "circle", label: t("personal.shapeCircle") },
      ]} />}
    </section>

    <section className="personal-section">
      <header><span><strong>{t("personal.style")}</strong></span><small>{t("personal.styleDesc")}</small></header>
      <div className="personal-style-grid">
        {STYLES.map((entry) => <button key={entry.value} className={appearance.style === entry.value ? "is-active" : ""} onClick={() => update("style", entry.value)}>
          <i className={`personal-style-sample is-${entry.value}`}><b /><b /><b /></i>
          <span><strong>{t(entry.title)}</strong><small>{t(entry.desc)}</small></span>
        </button>)}
      </div>
    </section>

    <section className="personal-section">
      <header><span><strong>{t("personal.palette")}</strong></span><small>{t("personal.paletteDesc")}</small></header>
      <div className="personal-palette-grid">
        {PALETTES.map((entry) => <button key={entry.id} className={!appearance.team_theme && appearance.palette === entry.id ? "is-active" : ""} onClick={() => updateAppearance((current) => ({ ...current, team_theme: null, palette: entry.id, accent_color: entry.accent }))}>
          <i style={{ background: `linear-gradient(135deg, ${entry.accent} 0 38%, ${entry.sunken} 38% 68%, ${entry.card} 68%)` }} />
          <span>{t(PALETTE_KEYS[entry.id])}</span>
        </button>)}
        <label className={`personal-custom-color ${!appearance.team_theme && appearance.palette === "custom" ? "is-active" : ""}`}>
          <input type="color" value={appearance.accent_color} onChange={(event) => updateAppearance((current) => ({ ...current, team_theme: null, palette: "custom", accent_color: event.target.value }))} />
          <span><strong>{t("personal.palette.custom")}</strong><code>{appearance.accent_color.toUpperCase()}</code></span>
        </label>
      </div>
    </section>

    <section className="personal-section">
      <header><span><strong>{t("personal.font")}</strong></span><small>{t("personal.fontDesc")}</small></header>
      <div className="personal-font-list">
        {FONTS.map((entry) => <button key={entry.value} className={`is-${entry.value} ${appearance.font === entry.value ? "is-active" : ""}`} onClick={() => update("font", entry.value)}>
          <span><strong>{t(entry.title)}</strong><small>{entry.sample}</small></span><i />
        </button>)}
      </div>
      <div className={`personal-font-upload ${appearance.font === "custom" ? "is-active" : ""}`}>
        <span className="personal-font-upload__sample">Aa</span>
        <span>
          <strong>{appearance.custom_font ? appearance.custom_font.file_name : t("personal.customFont")}</strong>
          <small>{appearance.custom_font ? t("personal.customFontReady") : t("personal.customFontDesc")}</small>
        </span>
        {appearance.custom_font && <button className="personal-button" onClick={() => update("font", "custom")}>{t("personal.useCustomFont")}</button>}
        <label className="personal-button"><Upload size={15} />{appearance.custom_font ? t("personal.replaceFont") : t("personal.uploadFont")}<input type="file" accept=".ttf,.otf,.woff,.woff2,font/ttf,font/otf,font/woff,font/woff2" onChange={(event) => void chooseFont(event)} /></label>
        {appearance.custom_font && <button className="personal-icon-button" onClick={removeCustomFont} title={t("personal.removeFont")}><X size={16} /></button>}
      </div>
    </section>

    <section className="personal-section">
      <header><span><strong>{t("personal.layout")}</strong></span><small>{t("personal.layoutDesc")}</small></header>
      <div className="personal-control-grid">
        <label><span>{t("personal.density")}</span><Segmented<AppearanceDensity> value={appearance.density} onChange={(value) => update("density", value)} options={[
          { value: "compact", label: t("personal.compact") }, { value: "standard", label: t("personal.standard") }, { value: "relaxed", label: t("personal.relaxed") },
        ]} /></label>
        <label><span>{t("personal.radius")}</span><Segmented<AppearanceLevel> value={appearance.radius} onChange={(value) => update("radius", value)} options={[
          { value: "none", label: t("personal.none") }, { value: "subtle", label: t("personal.subtle") }, { value: "soft", label: t("personal.soft") }, { value: "strong", label: t("personal.strong") },
        ]} /></label>
        <label><span>{t("personal.shadow")}</span><Segmented<AppearanceLevel> value={appearance.shadow} onChange={(value) => update("shadow", value)} options={[
          { value: "none", label: t("personal.none") }, { value: "subtle", label: t("personal.subtle") }, { value: "soft", label: t("personal.soft") }, { value: "strong", label: t("personal.strong") },
        ]} /></label>
        <label><span>{t("personal.motion")}</span><Segmented<AppearanceMotion> value={appearance.motion} onChange={(value) => update("motion", value)} options={[
          { value: "off", label: t("personal.off") }, { value: "reduced", label: t("personal.reduced") }, { value: "full", label: t("personal.full") },
        ]} /></label>
      </div>
    </section>

    <section className="personal-section">
      <header><span><Image size={17} /><strong>{t("personal.background")}</strong></span><small>{t("personal.backgroundDesc")}</small></header>
      <div className="personal-asset-row">
        <span className="personal-asset-preview is-background">{appearance.background ? <img src={appearance.background.data_url} alt="" /> : <Image size={18} />}</span>
        <span><strong>{appearance.background ? t("personal.backgroundActive") : t("personal.backgroundNone")}</strong><small>{t("personal.backgroundLimit")}</small></span>
        <label className="personal-button"><Upload size={15} />{t("personal.chooseImage")}<input type="file" accept="image/png,image/jpeg,image/webp" onChange={(event) => void chooseImage(event, "background")} /></label>
        {appearance.background && <button className="personal-icon-button" onClick={() => update("background", null)} title={t("personal.removeBackground")}><X size={16} /></button>}
      </div>
      {appearance.background && <div className="personal-range-grid">
        <label><span>{t("personal.positionX")}<code>{appearance.background.position_x}%</code></span><input type="range" min="0" max="100" value={appearance.background.position_x} onChange={(event) => update("background", { ...appearance.background!, position_x: Number(event.target.value) })} /></label>
        <label><span>{t("personal.positionY")}<code>{appearance.background.position_y}%</code></span><input type="range" min="0" max="100" value={appearance.background.position_y} onChange={(event) => update("background", { ...appearance.background!, position_y: Number(event.target.value) })} /></label>
        <label><span>{t("personal.dim")}<code>{appearance.background.dim}%</code></span><input type="range" min="0" max="85" value={appearance.background.dim} onChange={(event) => update("background", { ...appearance.background!, dim: Number(event.target.value) })} /></label>
        <label><span>{t("personal.blur")}<code>{appearance.background.blur}px</code></span><input type="range" min="0" max="12" value={appearance.background.blur} onChange={(event) => update("background", { ...appearance.background!, blur: Number(event.target.value) })} /></label>
        <label className="personal-range-grid__fit"><span>{t("personal.fit")}</span><Segmented value={appearance.background.fit} onChange={(fit) => update("background", { ...appearance.background!, fit })} options={[{ value: "cover", label: t("personal.cover") }, { value: "contain", label: t("personal.contain") }]} /></label>
      </div>}
    </section>

    <footer className="personal-actions">
      <span><strong>{t("personal.transfer")}</strong><small>{t("personal.transferDesc")}</small></span>
      <button onClick={reset}><RotateCcw size={15} />{t("personal.reset")}</button>
      <button onClick={() => void importTheme()} disabled={!!busy}><Upload size={15} />{busy === "import" ? t("personal.importing") : t("personal.import")}</button>
      <button className="is-primary" onClick={() => void exportTheme()} disabled={!!busy}><Download size={15} />{busy === "export" ? t("personal.exporting") : t("personal.export")}</button>
    </footer>

    <Modal
      open={!!pendingTheme}
      title={TEAM_THEMES.find((entry) => entry.id === pendingTheme)?.name ?? t("personal.teamThemes")}
      onClose={() => setPendingTheme(null)}
      footer={<>
        <button className="btn-secondary" onClick={() => setPendingTheme(null)}>{t("common.cancel")}</button>
        <button className="btn-primary" onClick={() => { const themeId = pendingTheme; setPendingTheme(null); if (themeId) void applyTheme(themeId); }}>{t("personal.teamApply")}</button>
      </>}
    >
      <p className="personal-confirm-copy">{t("personal.teamOverwriteConfirm")}</p>
    </Modal>
  </div>;
}
