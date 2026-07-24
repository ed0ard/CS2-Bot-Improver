import type { AppConfig, CosmeticsTeam, KnifeCustomizerConfig, KnifePreset, StickerPreset } from "./api";

export const STICKER_SLOT_COUNT = 5;
export const STICKER_RELEASE_ENABLED = false;

export function stickerFeatureEnabled(config: AppConfig | null | undefined): boolean {
  return STICKER_RELEASE_ENABLED
    && !!config?.experimental_features_enabled
    && !!config?.experimental_stickers_enabled;
}

export function clampStickerValue(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, Number.isFinite(value) ? value : min));
}

export function normalizeStickerSlots(stickers: StickerPreset[]): StickerPreset[] {
  return [...stickers].sort((left, right) => left.slot - right.slot);
}

export function replaceSticker(stickers: StickerPreset[], sticker: StickerPreset): StickerPreset[] {
  return normalizeStickerSlots([...stickers.filter((entry) => entry.slot !== sticker.slot), sticker]);
}

export function removeSticker(stickers: StickerPreset[], slot: number): StickerPreset[] {
  return normalizeStickerSlots(stickers.filter((entry) => entry.slot !== slot));
}

export function swapStickerSlots(stickers: StickerPreset[], slot: number, target: number): StickerPreset[] {
  if (slot < 0 || slot >= STICKER_SLOT_COUNT || target < 0 || target >= STICKER_SLOT_COUNT)
    return normalizeStickerSlots(stickers);
  return normalizeStickerSlots(stickers.map((entry) => entry.slot === slot
    ? { ...entry, slot: target }
    : entry.slot === target ? { ...entry, slot } : entry));
}

export function filterStickerCatalog<T extends { id: number }>(
  entries: T[],
  query: string,
  displayName: (entry: T) => string,
): T[] {
  const value = query.trim().toLocaleLowerCase();
  if (!value) return entries;
  return entries.filter((entry) => `${entry.id} ${displayName(entry)}`.toLocaleLowerCase().includes(value));
}

export function paginateStickerCatalog<T>(entries: T[], requestedPage: number, pageSize: number) {
  const pageCount = Math.max(1, Math.ceil(entries.length / pageSize));
  const page = Math.min(pageCount - 1, Math.max(0, requestedPage));
  return { page, pageCount, entries: entries.slice(page * pageSize, (page + 1) * pageSize) };
}

export function updateGunPresetStickers(
  config: KnifeCustomizerConfig,
  team: CosmeticsTeam,
  weaponId: number,
  availability: CosmeticsTeam | "shared",
  preset: KnifePreset,
  stickers: StickerPreset[],
): KnifeCustomizerConfig {
  const key = String(weaponId);
  const nextPreset: KnifePreset = { ...preset, stickers: normalizeStickerSlots(stickers) };
  const otherTeam: CosmeticsTeam = team === "ct" ? "t" : "ct";
  const linked = availability === "shared" && (config.shared_weapon_links[key] ?? true);
  return {
    ...config,
    loadouts: {
      ...config.loadouts,
      [team]: {
        ...config.loadouts[team],
        gun_presets: { ...config.loadouts[team].gun_presets, [key]: nextPreset },
      },
      [otherTeam]: linked ? {
        ...config.loadouts[otherTeam],
        gun_presets: { ...config.loadouts[otherTeam].gun_presets, [key]: { ...nextPreset, stickers: [...nextPreset.stickers ?? []] } },
      } : config.loadouts[otherTeam],
    },
  };
}
