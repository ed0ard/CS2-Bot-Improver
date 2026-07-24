import type { AppearanceConfig, AppearanceFont, AppearancePalette } from "./api";
import { TEAM_THEMES } from "./teamThemes";

export type PaletteDefinition = {
  id: Exclude<AppearancePalette, "custom">;
  accent: string;
  surface: string;
  sunken: string;
  card: string;
  cardWarm: string;
  text: string;
  textSecondary: string;
  textTertiary: string;
};

export const PALETTES: PaletteDefinition[] = [
  { id: "terracotta", accent: "#d97757", surface: "#fbfaf6", sunken: "#f4f1ea", card: "#fffefa", cardWarm: "#faf7f0", text: "#1f1e1b", textSecondary: "#3d3a34", textTertiary: "#87867f" },
  { id: "sky", accent: "#5b8fb9", surface: "#f8fafb", sunken: "#eef3f6", card: "#fffefe", cardWarm: "#f5f8fa", text: "#1d282f", textSecondary: "#35454f", textTertiary: "#7b8991" },
  { id: "monochrome", accent: "#23211c", surface: "#f8f8f6", sunken: "#efefec", card: "#ffffff", cardWarm: "#f5f5f2", text: "#171715", textSecondary: "#3c3c38", textTertiary: "#82827c" },
  { id: "grass", accent: "#698b58", surface: "#fafbf7", sunken: "#f0f3eb", card: "#fffefa", cardWarm: "#f6f8f1", text: "#20271d", textSecondary: "#3e4938", textTertiary: "#808a79" },
  { id: "mist", accent: "#667f93", surface: "#f8fafb", sunken: "#edf1f3", card: "#fffefe", cardWarm: "#f4f7f8", text: "#20272c", textSecondary: "#3d474e", textTertiary: "#7f898f" },
  { id: "berry", accent: "#a9636d", surface: "#fbf9f8", sunken: "#f3eeee", card: "#fffefe", cardWarm: "#f8f3f3", text: "#2a2021", textSecondary: "#4b3b3d", textTertiary: "#8b7d7e" },
];

export const DEFAULT_APPEARANCE: AppearanceConfig = {
  schema_version: 1,
  team_theme: null,
  brand_name: "Local Arena",
  style: "paper",
  palette: "terracotta",
  accent_color: "#d97757",
  font: "humanist",
  density: "standard",
  radius: "soft",
  shadow: "soft",
  motion: "full",
  custom_font: null,
  background: null,
  logo: null,
};

export const FONT_STACKS: Record<AppearanceFont, { heading: string; body: string; mono: string }> = {
  humanist: {
    heading: 'Georgia, "Songti SC", "Noto Serif CJK SC", "Source Han Serif SC", serif',
    body: '-apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif',
    mono: 'ui-monospace, "SF Mono", "Cascadia Code", Consolas, monospace',
  },
  modern: {
    heading: '"Inter", "Segoe UI", "Microsoft YaHei", sans-serif',
    body: '"Inter", "Segoe UI", "Microsoft YaHei", sans-serif',
    mono: '"Cascadia Code", Consolas, monospace',
  },
  clear: {
    heading: '"Segoe UI", "Microsoft YaHei", "PingFang SC", sans-serif',
    body: '"Segoe UI", "Microsoft YaHei", "PingFang SC", sans-serif',
    mono: '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
  },
  classic: {
    heading: 'Georgia, "Noto Serif CJK SC", "Songti SC", serif',
    body: 'Georgia, "Microsoft YaHei", "PingFang SC", sans-serif',
    mono: 'Consolas, "Cascadia Code", monospace',
  },
  technical: {
    heading: '"Cascadia Code", "Segoe UI", "Microsoft YaHei", sans-serif',
    body: '"Segoe UI", "Microsoft YaHei", sans-serif',
    mono: '"Cascadia Code", Consolas, monospace',
  },
  custom: {
    heading: '"LocalArenaCustomFont", "Segoe UI", "Microsoft YaHei", sans-serif',
    body: '"LocalArenaCustomFont", "Segoe UI", "Microsoft YaHei", sans-serif',
    mono: 'ui-monospace, "Cascadia Code", Consolas, monospace',
  },
};

const HEX_PATTERN = /^#[0-9a-f]{6}$/i;
const FONT_DATA_PATTERN = /^data:font\/(ttf|otf|woff|woff2);base64,[a-z0-9+/=]+$/i;
const IMAGE_DATA_PATTERN = /^data:image\/(png|jpeg|webp);base64,[a-z0-9+/=]+$/i;

const DATA_URL_CACHE_LIMIT = 6;
const imageDataUrlCache = new Map<string, boolean>();
const fontDataUrlCache = new Map<string, boolean>();

function cachedDataUrlTest(cache: Map<string, boolean>, pattern: RegExp, dataUrl: string): boolean {
  const cached = cache.get(dataUrl);
  if (cached !== undefined) return cached;
  const valid = pattern.test(dataUrl);
  if (cache.size >= DATA_URL_CACHE_LIMIT) cache.clear();
  cache.set(dataUrl, valid);
  return valid;
}

function clampNumber(value: unknown, min: number, max: number, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value)
    ? Math.min(max, Math.max(min, value))
    : fallback;
}

export function normalizeAppearance(value: unknown): AppearanceConfig {
  if (!value || typeof value !== "object") return structuredClone(DEFAULT_APPEARANCE);
  const candidate = value as Partial<AppearanceConfig>;
  const palette = candidate.palette === "custom" || PALETTES.some((entry) => entry.id === candidate.palette)
    ? candidate.palette!
    : "terracotta";
  const basePalette = palette === "custom" ? PALETTES[0] : PALETTES.find((entry) => entry.id === palette)!;
  const brandName = typeof candidate.brand_name === "string" ? candidate.brand_name.trim().slice(0, 32) : "";
  const rawFont = candidate.custom_font;
  const customFont = rawFont
    && typeof rawFont.data_url === "string"
    && cachedDataUrlTest(fontDataUrlCache, FONT_DATA_PATTERN, rawFont.data_url)
    && typeof rawFont.file_name === "string"
    && rawFont.file_name.trim().length > 0
    && ["ttf", "otf", "woff", "woff2"].includes(rawFont.format)
    ? { ...rawFont, file_name: rawFont.file_name.trim().slice(0, 128) }
    : null;
  const validFonts: AppearanceFont[] = ["humanist", "modern", "clear", "classic", "technical", "custom"];
  const requestedFont = validFonts.includes(candidate.font as AppearanceFont) ? candidate.font as AppearanceFont : "humanist";
  const teamTheme = typeof candidate.team_theme === "string" && TEAM_THEMES.some((entry) => entry.id === candidate.team_theme)
    ? candidate.team_theme
    : null;
  const rawBackground = candidate.background;
  const background = rawBackground
    && typeof rawBackground.data_url === "string"
    && cachedDataUrlTest(imageDataUrlCache, IMAGE_DATA_PATTERN, rawBackground.data_url)
    ? {
      data_url: rawBackground.data_url,
      fit: rawBackground.fit === "contain" ? "contain" as const : "cover" as const,
      position_x: clampNumber(rawBackground.position_x, 0, 100, 50),
      position_y: clampNumber(rawBackground.position_y, 0, 100, 50),
      dim: clampNumber(rawBackground.dim, 0, 85, 18),
      blur: clampNumber(rawBackground.blur, 0, 12, 0),
    }
    : null;
  const rawLogo = candidate.logo;
  const logo = rawLogo
    && typeof rawLogo.data_url === "string"
    && cachedDataUrlTest(imageDataUrlCache, IMAGE_DATA_PATTERN, rawLogo.data_url)
    ? {
      data_url: rawLogo.data_url,
      fit: rawLogo.fit === "cover" ? "cover" as const : "contain" as const,
      shape: (["rounded", "square", "circle"] as const).includes(rawLogo.shape as "rounded" | "square" | "circle")
        ? rawLogo.shape as "rounded" | "square" | "circle"
        : "rounded" as const,
    }
    : null;
  return {
    ...structuredClone(DEFAULT_APPEARANCE),
    ...candidate,
    schema_version: 1,
    team_theme: teamTheme,
    brand_name: brandName || DEFAULT_APPEARANCE.brand_name,
    palette,
    accent_color: typeof candidate.accent_color === "string" && HEX_PATTERN.test(candidate.accent_color)
      ? candidate.accent_color.toLowerCase()
      : basePalette.accent,
    font: requestedFont === "custom" && !customFont ? "humanist" : requestedFont,
    custom_font: customFont,
    background,
    logo,
  };
}

function mix(hex: string, target: string, amount: number) {
  const channel = (value: string, index: number) => Number.parseInt(value.slice(index, index + 2), 16);
  const result = [1, 3, 5].map((index) => Math.round(channel(hex, index) * (1 - amount) + channel(target, index) * amount));
  return `#${result.map((value) => value.toString(16).padStart(2, "0")).join("")}`;
}

function rgba(hex: string, alpha: number) {
  const channels = [1, 3, 5].map((index) => Number.parseInt(hex.slice(index, index + 2), 16));
  return `rgba(${channels.join(", ")}, ${alpha})`;
}

let appliedBackgroundImage: string | null = null;

export function applyAppearance(raw: AppearanceConfig) {
  const appearance = normalizeAppearance(raw);
  const root = document.documentElement;
  const teamTheme = TEAM_THEMES.find((entry) => entry.id === appearance.team_theme);
  const palette = teamTheme?.palette ?? PALETTES.find((entry) => entry.id === appearance.palette) ?? PALETTES[0];
  const accent = appearance.accent_color;
  const fonts = FONT_STACKS[appearance.font] ?? FONT_STACKS.humanist;
  const immersive = appearance.style === "immersive" && !!appearance.background;

  root.dataset.appearanceStyle = appearance.style;
  root.dataset.appearanceDensity = appearance.density;
  root.dataset.appearanceMotion = appearance.motion;
  if (teamTheme) root.dataset.teamTheme = teamTheme.id;
  else delete root.dataset.teamTheme;
  root.style.setProperty("--app-surface", palette.surface);
  root.style.setProperty("--paper-sunken", palette.sunken);
  root.style.setProperty("--card", immersive ? rgba(palette.card, 0.96) : palette.card);
  root.style.setProperty("--card-warm", immersive ? rgba(palette.cardWarm, 0.95) : palette.cardWarm);
  root.style.setProperty("--text-primary", palette.text);
  root.style.setProperty("--text-secondary", palette.textSecondary);
  root.style.setProperty("--text-tertiary", mix(palette.textTertiary, palette.text, 0.18));
  root.style.setProperty("--text-quaternary", mix(palette.textTertiary, palette.text, 0.1));
  root.style.setProperty("--c-accent", accent);
  root.style.setProperty("--c-accent-hover", mix(accent, "#000000", 0.15));
  root.style.setProperty("--c-accent-soft", rgba(accent, 0.1));
  root.style.setProperty("--c-accent-line", rgba(accent, 0.3));
  root.style.setProperty("--c-ink", teamTheme?.palette.ink ?? "#23211c");
  root.style.setProperty("--c-ink-hover", mix(teamTheme?.palette.ink ?? "#23211c", "#ffffff", 0.1));
  root.style.setProperty("--line", rgba(palette.text, 0.06));
  root.style.setProperty("--line-strong", rgba(palette.text, 0.12));
  root.style.setProperty("--edge", rgba(palette.text, 0.04));
  root.style.setProperty("--track", rgba(palette.text, 0.045));
  root.style.setProperty("--sidebar-bg", palette.sunken);
  root.style.setProperty("--panel-muted", palette.cardWarm);
  root.style.setProperty("--serif", fonts.heading);
  root.style.setProperty("--sans", fonts.body);
  root.style.setProperty("--mono", fonts.mono);

  const customFontStyleId = "local-arena-custom-font";
  document.getElementById(customFontStyleId)?.remove();
  if (appearance.custom_font) {
    const style = document.createElement("style");
    style.id = customFontStyleId;
    const cssFormat = appearance.custom_font.format === "ttf" ? "truetype"
      : appearance.custom_font.format === "otf" ? "opentype"
        : appearance.custom_font.format;
    style.textContent = `@font-face{font-family:"LocalArenaCustomFont";src:url("${appearance.custom_font.data_url}") format("${cssFormat}");font-style:normal;font-weight:100 900;font-display:swap;}`;
    document.head.append(style);
  }

  const radius = {
    none: [3, 5, 7], subtle: [6, 9, 12], soft: [9, 12, 18], strong: [11, 15, 20],
  }[appearance.radius] ?? [9, 12, 18];
  root.style.setProperty("--r-sm", `${radius[0]}px`);
  root.style.setProperty("--r-md", `${radius[1]}px`);
  root.style.setProperty("--r-lg", `${radius[2]}px`);
  root.style.setProperty("--r-xl", `${radius[2]}px`);

  const shadow = {
    none: "none",
    subtle: "0 1px 2px rgba(31, 30, 27, 0.05)",
    soft: "0 1px 2px rgba(31, 30, 27, 0.04), 0 12px 32px -16px rgba(31, 30, 27, 0.1)",
    strong: "0 2px 5px rgba(31, 30, 27, 0.06), 0 18px 44px -18px rgba(31, 30, 27, 0.2)",
  }[appearance.shadow];
  root.style.setProperty("--sh-card", shadow);
  root.style.setProperty("--sh-soft", shadow);

  const density = {
    compact: { page: "20px 28px 30px", card: "14px 16px", gap: "11px", nav: "34px" },
    standard: { page: "28px 36px 40px", card: "18px 20px", gap: "16px", nav: "38px" },
    relaxed: { page: "34px 42px 48px", card: "22px 24px", gap: "20px", nav: "42px" },
  }[appearance.density];
  root.style.setProperty("--pad-page", density.page);
  root.style.setProperty("--pad-card", density.card);
  root.style.setProperty("--gap-card", density.gap);
  root.style.setProperty("--nav-row", density.nav);

  root.style.setProperty("--dur", appearance.motion === "off" ? "0ms" : appearance.motion === "reduced" ? "100ms" : "180ms");
  if (appearance.background) {
    const washAlpha = immersive ? 0.78 : teamTheme ? 0.72 : 0.66;
    const sidebarWashAlpha = immersive ? 0.85 : 0.82;
    if (appliedBackgroundImage !== appearance.background.data_url) {
      root.style.setProperty("--personal-bg-image", `url("${appearance.background.data_url}")`);
      appliedBackgroundImage = appearance.background.data_url;
    }
    root.style.setProperty("--personal-bg-size", appearance.background.fit);
    root.style.setProperty("--personal-bg-position", `${appearance.background.position_x}% ${appearance.background.position_y}%`);
    root.style.setProperty("--personal-bg-dim", String(appearance.background.dim / 100));
    root.style.setProperty("--personal-bg-blur", `${appearance.background.blur}px`);
    root.style.setProperty("--personal-bg-wash", rgba(palette.surface, washAlpha));
    root.style.setProperty("--personal-sidebar-wash", rgba(palette.sunken, sidebarWashAlpha));
  } else {
    if (appliedBackgroundImage !== null) {
      root.style.setProperty("--personal-bg-image", "none");
      appliedBackgroundImage = null;
    }
    root.style.setProperty("--personal-bg-dim", "0");
    root.style.setProperty("--personal-bg-blur", "0px");
    root.style.setProperty("--personal-bg-wash", "transparent");
    root.style.setProperty("--personal-sidebar-wash", palette.sunken);
  }
}

export type AppearanceBundle = {
  schema_version: 1;
  kind: "local-arena-theme";
  exported_at_unix: number;
  appearance: AppearanceConfig;
};

export function appearanceBundle(config: AppearanceConfig): AppearanceBundle {
  return {
    schema_version: 1,
    kind: "local-arena-theme",
    exported_at_unix: Math.floor(Date.now() / 1000),
    appearance: normalizeAppearance(config),
  };
}

export function parseAppearanceBundle(value: unknown): AppearanceConfig {
  if (!value || typeof value !== "object") throw new Error("Invalid Local Arena theme");
  const bundle = value as Partial<AppearanceBundle>;
  if (bundle.schema_version !== 1 || bundle.kind !== "local-arena-theme" || !bundle.appearance) {
    throw new Error("Unsupported Local Arena theme");
  }
  return normalizeAppearance(bundle.appearance);
}
