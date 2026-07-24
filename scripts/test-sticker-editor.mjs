import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import ts from "../Panel/node_modules/typescript/lib/typescript.js";

const sourcePath = new URL("../Panel/src/lib/stickerEditor.ts", import.meta.url);
const source = await readFile(sourcePath, "utf8");
const compiled = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.ES2022, target: ts.ScriptTarget.ES2022 },
  fileName: "stickerEditor.ts",
});
const editor = await import(`data:text/javascript;base64,${Buffer.from(compiled.outputText).toString("base64")}`);

const sticker = (slot, id, patch = {}) => ({
  slot, id, wear: 0, scale: 1, rotation: 0,
  offset_x: 0, offset_y: 0, custom_position: false,
  ...patch,
});
const preset = { paint: 661, seed: 0, wear: 0.01, name_tag: "", stattrak_enabled: false, stattrak_count: 0, souvenir_enabled: false, stickers: [] };
const loadout = () => ({ default_knife_defindex: 0, knife_presets: {}, glove: { enabled: false, defindex: 5030, paint: 10048, seed: 0, wear: 0.01 }, gun_presets: { "9": { ...preset } } });
const config = { schema_version: 3, enabled: true, apply_to_human_players: true, apply_on_pickup: true, music_kit_id: 0, loadouts: { ct: loadout(), t: loadout() }, shared_weapon_links: { "9": true }, stickers_enabled: true };

assert.equal(editor.STICKER_RELEASE_ENABLED, false);
assert.equal(editor.stickerFeatureEnabled({ experimental_features_enabled: true, experimental_stickers_enabled: true }), false);
assert.equal(editor.stickerFeatureEnabled({ experimental_features_enabled: false, experimental_stickers_enabled: true }), false);

const entries = [{ id: 1, name: "Alpha" }, { id: 22, name: "Bravo" }, { id: 3, name: "Charlie" }];
assert.deepEqual(editor.filterStickerCatalog(entries, "22", (entry) => entry.name).map((entry) => entry.id), [22]);
assert.deepEqual(editor.filterStickerCatalog(entries, "bravo", (entry) => entry.name).map((entry) => entry.id), [22]);
assert.deepEqual(editor.paginateStickerCatalog(entries, 8, 2), { page: 1, pageCount: 2, entries: [entries[2]] });

let stickers = editor.replaceSticker([], sticker(2, 10));
stickers = editor.replaceSticker(stickers, sticker(2, 11, { wear: 0.5 }));
assert.deepEqual(stickers, [sticker(2, 11, { wear: 0.5 })]);
stickers = editor.replaceSticker(stickers, sticker(3, 12));
stickers = editor.swapStickerSlots(stickers, 2, 3);
assert.deepEqual(stickers, [sticker(2, 12), sticker(3, 11, { wear: 0.5 })]);
assert.deepEqual(editor.removeSticker(stickers, 2), [sticker(3, 11, { wear: 0.5 })]);

assert.equal(editor.clampStickerValue(Number.NaN, -1, 1), -1);
assert.equal(editor.clampStickerValue(4, -1, 1), 1);
assert.equal(editor.clampStickerValue(-4, -1, 1), -1);

const linked = editor.updateGunPresetStickers(config, "ct", 9, "shared", config.loadouts.ct.gun_presets["9"], [sticker(0, 10)]);
assert.deepEqual(linked.loadouts.ct.gun_presets["9"], linked.loadouts.t.gun_presets["9"]);
const unlinkedConfig = { ...config, shared_weapon_links: { "9": false } };
const unlinked = editor.updateGunPresetStickers(unlinkedConfig, "ct", 9, "shared", unlinkedConfig.loadouts.ct.gun_presets["9"], [sticker(0, 10)]);
assert.equal(unlinked.loadouts.t.gun_presets["9"].stickers.length, 0);

console.log("Sticker editor release gate, catalog, slot, bounds, and shared-link tests passed.");
