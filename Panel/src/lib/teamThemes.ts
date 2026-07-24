import type { AppearanceConfig, AppearanceDensity, AppearanceFont, AppearanceLevel } from "./api";

import falconsLogo from "../assets/team-themes/logos/falcons.png";
import vitalityLogo from "../assets/team-themes/logos/vitality.png";
import furiaLogo from "../assets/team-themes/logos/furia.png";
import spiritLogo from "../assets/team-themes/logos/spirit.png";
import naviLogo from "../assets/team-themes/logos/navi.png";
import g2Logo from "../assets/team-themes/logos/g2.png";
import mouzLogo from "../assets/team-themes/logos/mouz.png";
import fazeLogo from "../assets/team-themes/logos/faze.png";
import tylooLogo from "../assets/team-themes/logos/tyloo.png";

import falconsBackground from "../assets/team-themes/backgrounds/falcons.jpg";
import vitalityBackground from "../assets/team-themes/backgrounds/vitality.jpg";
import furiaBackground from "../assets/team-themes/backgrounds/furia.jpg";
import spiritBackground from "../assets/team-themes/backgrounds/spirit.jpg";
import naviBackground from "../assets/team-themes/backgrounds/navi.jpg";
import g2Background from "../assets/team-themes/backgrounds/g2.jpg";
import mouzBackground from "../assets/team-themes/backgrounds/mouz.jpg";
import fazeBackground from "../assets/team-themes/backgrounds/faze.jpg";
import tylooBackground from "../assets/team-themes/backgrounds/tyloo.jpg";

export type TeamThemeDefinition = {
  id: string;
  name: string;
  accent: string;
  colors: readonly [string, string, string];
  logo: string;
  background: string;
  font: AppearanceFont;
  density: AppearanceDensity;
  radius: AppearanceLevel;
  shadow: AppearanceLevel;
  palette: {
    surface: string;
    sunken: string;
    card: string;
    cardWarm: string;
    text: string;
    textSecondary: string;
    textTertiary: string;
    ink: string;
  };
};

export const TEAM_THEMES: readonly TeamThemeDefinition[] = [
  { id: "falcons", name: "Falcons", accent: "#00a978", colors: ["#00bf83", "#082f5f", "#f7f1e4"], logo: falconsLogo, background: falconsBackground, font: "modern", density: "standard", radius: "soft", shadow: "soft", palette: { surface: "#f6faf7", sunken: "#eaf3ee", card: "#fffefa", cardWarm: "#f2f7f4", text: "#13283b", textSecondary: "#344b5b", textTertiary: "#74858d", ink: "#10345a" } },
  { id: "vitality", name: "Vitality", accent: "#c99a00", colors: ["#ffcf00", "#24231f", "#f8f1df"], logo: vitalityLogo, background: vitalityBackground, font: "technical", density: "standard", radius: "subtle", shadow: "subtle", palette: { surface: "#fbfaf3", sunken: "#f2eed9", card: "#fffef8", cardWarm: "#f8f4e4", text: "#25231d", textSecondary: "#47443a", textTertiary: "#858174", ink: "#292720" } },
  { id: "furia", name: "FURIA", accent: "#c93434", colors: ["#cf2f2f", "#1f1d1b", "#f6efe2"], logo: furiaLogo, background: furiaBackground, font: "modern", density: "compact", radius: "subtle", shadow: "soft", palette: { surface: "#faf7f4", sunken: "#f1eae6", card: "#fffdfa", cardWarm: "#f8f0ec", text: "#251f1d", textSecondary: "#4b403d", textTertiary: "#887b76", ink: "#272220" } },
  { id: "spirit", name: "Spirit", accent: "#667eaa", colors: ["#7185aa", "#292731", "#f4efe5"], logo: spiritLogo, background: spiritBackground, font: "classic", density: "standard", radius: "soft", shadow: "soft", palette: { surface: "#f8f7fa", sunken: "#ecebf1", card: "#fffefe", cardWarm: "#f4f2f7", text: "#292833", textSecondary: "#484755", textTertiary: "#817f8d", ink: "#31313c" } },
  { id: "navi", name: "Natus Vincere", accent: "#c79e00", colors: ["#ffd400", "#1f1e1b", "#f8f1df"], logo: naviLogo, background: naviBackground, font: "technical", density: "compact", radius: "subtle", shadow: "subtle", palette: { surface: "#fbfaf2", sunken: "#f1edd7", card: "#fffef7", cardWarm: "#f8f3df", text: "#24231e", textSecondary: "#46443b", textTertiary: "#858174", ink: "#292823" } },
  { id: "g2", name: "G2", accent: "#b93438", colors: ["#bd3437", "#242220", "#f5efe5"], logo: g2Logo, background: g2Background, font: "technical", density: "compact", radius: "subtle", shadow: "strong", palette: { surface: "#faf8f6", sunken: "#eeeae7", card: "#fffefe", cardWarm: "#f6f2ef", text: "#252321", textSecondary: "#484440", textTertiary: "#827d78", ink: "#2b2927" } },
  { id: "mouz", name: "MOUZ", accent: "#d12f36", colors: ["#e52d35", "#242220", "#f5efe5"], logo: mouzLogo, background: mouzBackground, font: "modern", density: "compact", radius: "subtle", shadow: "soft", palette: { surface: "#fbf8f7", sunken: "#f2e9e8", card: "#fffefe", cardWarm: "#f8efee", text: "#272120", textSecondary: "#4a403e", textTertiary: "#877b78", ink: "#2b2524" } },
  { id: "faze", name: "FaZe", accent: "#bd3a3d", colors: ["#c63d3e", "#242220", "#f5efe5"], logo: fazeLogo, background: fazeBackground, font: "technical", density: "standard", radius: "subtle", shadow: "soft", palette: { surface: "#faf8f6", sunken: "#efe9e7", card: "#fffefe", cardWarm: "#f7f1ef", text: "#262220", textSecondary: "#49413e", textTertiary: "#837b77", ink: "#292524" } },
  { id: "tyloo", name: "TYLOO", accent: "#b73534", colors: ["#bd3534", "#26211f", "#c69a52"], logo: tylooLogo, background: tylooBackground, font: "clear", density: "standard", radius: "soft", shadow: "soft", palette: { surface: "#fbf8f4", sunken: "#f1e9e2", card: "#fffefa", cardWarm: "#f8f0e9", text: "#29211e", textSecondary: "#4d403b", textTertiary: "#897a74", ink: "#302522" } },
] as const;

function assetDataUrl(url: string): Promise<string> {
  if (url.startsWith("data:")) return Promise.resolve(url);
  return fetch(url).then((response) => {
    if (!response.ok) throw new Error(`Unable to load theme asset: ${response.status}`);
    return response.blob();
  }).then((blob) => new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => typeof reader.result === "string" ? resolve(reader.result) : reject(new Error("Unable to read theme asset"));
    reader.onerror = () => reject(reader.error ?? new Error("Unable to read theme asset"));
    reader.readAsDataURL(blob);
  }));
}

export async function applyTeamTheme(current: AppearanceConfig, theme: TeamThemeDefinition): Promise<AppearanceConfig> {
  const [logoDataUrl, backgroundDataUrl] = await Promise.all([
    assetDataUrl(theme.logo),
    assetDataUrl(theme.background),
  ]);
  return {
    ...current,
    team_theme: theme.id,
    brand_name: theme.name,
    style: "immersive",
    palette: "custom",
    accent_color: theme.accent,
    font: theme.font,
    density: theme.density,
    radius: theme.radius,
    shadow: theme.shadow,
    logo: { data_url: logoDataUrl, fit: "contain", shape: "square" },
    background: {
      data_url: backgroundDataUrl,
      fit: "cover",
      position_x: 50,
      position_y: 50,
      dim: 0,
      blur: 0,
    },
  };
}
