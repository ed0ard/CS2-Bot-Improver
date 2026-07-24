import { invoke as tauriInvoke } from "@tauri-apps/api/core";

function invoke<T>(command: string, args?: Record<string, unknown>) {
  return tauriInvoke<T>(command, args);
}

/** Mirrors Rust `AppError` (error.rs). Codes are stable & not localized. */
export type AppError = {
  code: string;
  category: string;
  detail: string;
};

export function isAppError(e: unknown): e is AppError {
  return (
    typeof e === "object" &&
    e !== null &&
    "code" in e &&
    "category" in e &&
    typeof (e as AppError).code === "string"
  );
}

/** Normalize any thrown value into an AppError so the UI always has a code. */
export function toAppError(e: unknown): AppError {
  if (isAppError(e)) return e;

  if (typeof e === "string") {
    try {
      const parsed: unknown = JSON.parse(e);
      if (isAppError(parsed)) return parsed;
    } catch {
      // Plain strings are valid Tauri rejection details.
    }
    return { code: "E1099", category: "internal", detail: e || "Unknown error" };
  }

  if (e instanceof Error) {
    return {
      code: "E1099",
      category: "internal",
      detail: `${e.name}: ${e.message}`,
    };
  }

  if (typeof e === "object" && e !== null && "message" in e) {
    const message = (e as { message?: unknown }).message;
    if (typeof message === "string") {
      return { code: "E1099", category: "internal", detail: message };
    }
  }

  let detail = "Unknown error";
  try {
    detail = JSON.stringify(e) || String(e);
  } catch {
    detail = String(e);
  }
  return {
    code: "E1099",
    category: "internal",
    detail,
  };
}

// ---- DTOs (mirror src-tauri) ----
export type DirectoryInfo = {
  candidates: string[];
  selected: string | null;
  valid: boolean;
  needs_choice: boolean;
  steam_found: boolean;
};

export type FilesReport = {
  ok: boolean;
  total: number;
  present: number;
  missing: string[];
  /** Wrong folder the plugin was extracted into, if detected. */
  misplaced: string | null;
};

export type Cs2ProcessInfo = {
  running: boolean;
  pid: number | null;
  executable: string | null;
  path_accessible: boolean;
  matches_selected: boolean;
};

export type InstallationSource =
  | "clean"
  | "managed_plus"
  | "legacy_plus"
  | "upstream"
  | "mixed_unknown";

export type MigrationKind =
  | "fresh_install"
  | "managed_upgrade"
  | "adopt_legacy_plus"
  | "replace_upstream"
  | "blocked";

export type RestoreBaseline =
  | "steam_original"
  | "pre_migration"
  | "existing_record"
  | "none";

export type InstallationInspection = {
  installed: boolean;
  package_version: string | null;
  manifest_available: boolean;
  total: number;
  healthy: number;
  missing: string[];
  corrupt: string[];
  restore_available: boolean;
  backup_path: string | null;
  interrupted_transaction: boolean;
  source: InstallationSource;
  source_version: string | null;
  source_evidence: string[];
  migration_kind: MigrationKind;
  restore_baseline: RestoreBaseline;
  can_install: boolean;
};

export type InstallPlan = {
  package_version: string;
  target: string;
  total_files: number;
  new_files: number;
  overwritten_files: number;
  backup_path: string;
  required_target_bytes: number;
  available_target_bytes: number;
  required_backup_bytes: number;
  available_backup_bytes: number;
  writable: boolean;
  source: InstallationSource;
  source_version: string | null;
  source_evidence: string[];
  migration_kind: MigrationKind;
  restore_baseline: RestoreBaseline;
  can_install: boolean;
};

export type InstallTransactionResult = {
  package_version: string;
  installed_files: number;
  backup_path: string;
  repaired: boolean;
};

export type RestoreResult = {
  restored_files: number;
  removed_files: number;
  preserved_files: number;
  presets_backup: string | null;
  steam_verify_uri: string;
  result_kind: "restore_previous" | "pristine";
};

export type DiagnosticReport = {
  path: string;
  files_collected: number;
};

export type UiMemory = {
  schema_version: number;
  saved_at: number;
  entries: Record<string, string>;
};

export type DifficultyLevel = "Low" | "Medium" | "High";

export type DifficultyInfo = {
  current: DifficultyLevel | null;
  available: DifficultyLevel[];
  active_present: boolean;
  cs2_running: boolean;
};

export type BotItemKey = "skins" | "profiles" | "agents" | "music";

export type BotItemsState = {
  skins: boolean;
  profiles: boolean;
  agents: boolean;
  music: boolean;
  cfg_present: boolean;
  cs2_running: boolean;
};

export type AimValue = "head" | "mixed" | "body";
export type NadesValue = "max" | "more" | "normal" | "off";

export type PresetsState = {
  aim: AimValue | null;
  aim_supported: boolean;
  aim_active: boolean | null;
  aim_transport: string | null;
  aim_override_count: number | null;
  aim_error_count: number | null;
  nades: NadesValue | null;
  cfg_present: boolean;
  cs2_running: boolean;
};

export type DropKnivesState = {
  bind_key: string;
  selected: number[];
  cfg_present: boolean;
  cs2_running: boolean;
};

export type KnifePreset = {
  paint: number;
  seed: number;
  wear: number;
  name_tag: string;
  stattrak_enabled: boolean;
  stattrak_count: number;
  souvenir_enabled?: boolean;
  stickers?: StickerPreset[];
};

export type StickerPreset = {
  slot: number;
  id: number;
  wear: number;
  scale: number;
  rotation: number;
  offset_x: number;
  offset_y: number;
  custom_position: boolean;
};

export type GlovePreset = {
  enabled: boolean;
  defindex: number;
  paint: number;
  seed: number;
  wear: number;
};

export type CosmeticsTeam = "ct" | "t";

export type TeamCosmeticLoadout = {
  default_knife_defindex: number;
  knife_presets: Record<string, KnifePreset>;
  glove: GlovePreset;
  gun_presets: Record<string, KnifePreset>;
};

export type KnifeCustomizerConfig = {
  schema_version: 3;
  enabled: boolean;
  apply_to_human_players: boolean;
  apply_on_pickup: boolean;
  music_kit_id: number;
  loadouts: Record<CosmeticsTeam, TeamCosmeticLoadout>;
  shared_weapon_links: Record<string, boolean>;
  stickers_enabled: boolean;
};

export type KnifeCustomizerState = {
  plugin_present: boolean;
  config_present: boolean;
  cs2_running: boolean;
  config: KnifeCustomizerConfig;
};

export type CosmeticsPresetExportResult = { path: string; size_bytes: number };
export type CosmeticsPresetImportResult = { state: KnifeCustomizerState; backup_path: string | null };

export type GameMode = "online" | "preview" | "bots";

export type ModeInfo = {
  current: GameMode | null;
  online_present: boolean;
  preview_present: boolean;
  bots_present: boolean;
  layout_healthy: boolean;
  insecure: boolean;
  user_count: number;
  cs2_running: boolean;
  // CS2 running and the on-disk gameinfo.gi doesn't match the remembered mode
  // (the boot-time apply was skipped) — show the mode control yellow.
  pending: boolean;
};

export type LaunchResult = {
  options: string;
  insecure: boolean;
};

export type BotItems = {
  skins: boolean;
  profiles: boolean;
  agents: boolean;
  music: boolean;
};

export type AppConfig = {
  language: string | null;
  difficulty: string | null;
  mode: string | null;
  insecure: boolean;
  bot_items: BotItems;
  aim: string | null;
  nades: string | null;
  drop_knife_bind: string;
  drop_knife_subclasses: number[];
  csgo_path: string | null;
  first_run_done: boolean;
  first_run_step?: string | null;
  cosmetics_enabled_before_online?: boolean | null;
  cosmetics_enabled_before_preview?: boolean | null;
  experimental_features_enabled?: boolean;
  experimental_stickers_enabled?: boolean;
};

export type AppearanceStyle = "paper" | "clean" | "compact" | "immersive";
export type AppearancePalette = "terracotta" | "sky" | "monochrome" | "grass" | "mist" | "berry" | "custom";
export type AppearanceFont = "humanist" | "modern" | "clear" | "classic" | "technical" | "custom";
export type AppearanceDensity = "compact" | "standard" | "relaxed";
export type AppearanceLevel = "none" | "subtle" | "soft" | "strong";
export type AppearanceMotion = "off" | "reduced" | "full";

export type AppearanceBackground = {
  data_url: string;
  fit: "cover" | "contain";
  position_x: number;
  position_y: number;
  dim: number;
  blur: number;
};

export type AppearanceLogo = {
  data_url: string;
  fit: "cover" | "contain";
  shape: "rounded" | "square" | "circle";
};

export type AppearanceCustomFont = {
  data_url: string;
  file_name: string;
  format: "ttf" | "otf" | "woff" | "woff2";
};

export type AppearanceConfig = {
  schema_version: 1;
  team_theme: string | null;
  brand_name: string;
  style: AppearanceStyle;
  palette: AppearancePalette;
  accent_color: string;
  font: AppearanceFont;
  density: AppearanceDensity;
  radius: AppearanceLevel;
  shadow: AppearanceLevel;
  motion: AppearanceMotion;
  custom_font: AppearanceCustomFont | null;
  background: AppearanceBackground | null;
  logo: AppearanceLogo | null;
};

export type AppearanceExportResult = { path: string; size_bytes: number };

export type UpdateComponentState = {
  current_version: string;
  latest_version: string | null;
  update_available: boolean;
  compatible: boolean;
  status: string;
  downloaded_bytes: number;
  total_bytes: number;
  error: string | null;
};

export type OnlineUpdateSnapshot = {
  checked_at: number | null;
  release_version: string | null;
  release_notes_url: string | null;
  panel: UpdateComponentState;
  plugin: UpdateComponentState;
  busy: boolean;
  error: string | null;
};

export type UpdateProgress = {
  component: "panel" | "plugin";
  stage: string;
  downloaded_bytes: number;
  total_bytes: number;
};

export type UpdateResult = {
  component: "panel" | "plugin";
  version: string;
  installed: boolean;
  restart_required: boolean;
  rollback_succeeded: boolean | null;
  detail: string;
};

export type UpdateBatchResult = {
  panel: UpdateResult | null;
  plugin: UpdateResult | null;
  restart_required: boolean;
};

export type RuntimeSnapshot = {
  directory: DirectoryInfo;
  process: Cs2ProcessInfo;
  files: FilesReport | null;
  difficulty: DifficultyInfo | null;
  mode: ModeInfo | null;
  bot_items: BotItemsState | null;
  presets: PresetsState | null;
  drop_knives: DropKnivesState | null;
  installation: InstallationInspection | null;
};

export type MatchMap = {
  id: string;
  display_name: string;
  workshop_name: string;
  thumbnail: string;
  required_vpk: string;
};

export type MatchTeam = {
  id: string;
  name: string;
  badge: string;
  ranking: number | null;
  players: string[];
};

export type MatchCatalog = {
  schema_version: number;
  catalog_version: string;
  freeze_date: string;
  source: string;
  maps: MatchMap[];
  teams: MatchTeam[];
  difficulties: string[];
};

export type MatchPlayer = {
  id: string;
  name: string;
  kind: "human" | "bot";
  is_local_player: boolean;
};

export type MatchRequest = {
  schema_version: number;
  session_id: string;
  created_at_unix: number;
  map_id: string;
  player_side: "ct" | "t";
  difficulty: "low" | "medium" | "high";
  opponent_kind: "featured_team" | "random";
  opponent_team_id: string | null;
  opponent_name: string;
  record_demo: boolean;
  player_team: MatchPlayer[];
  opponent_team: MatchPlayer[];
  result_path: string;
  demo_path: string;
};

export type PrepareMatchInput = {
  schema_version: 1;
  map_id: string;
  player_side: "random" | "ct" | "t";
  difficulty: "low" | "medium" | "high";
  opponent_kind: "featured_team" | "random";
  opponent_team_id: string | null;
  record_demo: boolean;
};

export type DemoStatus = {
  state: "disabled" | "pending" | "recording" | "validating" | "ready" | "failed" | "interrupted";
  path: string | null;
  size_bytes: number;
  error_code: string | null;
  detail: string | null;
};

export type OpenRatingBreakdown = {
  model_version: string;
  kills: number;
  damage: number;
  survival: number;
  kast: number;
  multi_kills: number;
  round_swing: number;
  economy_adjustment: number;
  open_rating?: number;
  /** Legacy field emitted by matches created before the OpenRating migration. */
  rating_plus?: number;
};

export type PlayerMatchStats = {
  player_id: string;
  name: string;
  kind: "human" | "bot";
  team: "ct" | "t";
  kills: number;
  deaths: number;
  assists: number;
  headshots: number;
  damage: number;
  rounds_played: number;
  rounds_survived: number;
  kast_rounds: number;
  first_kills: number;
  first_deaths: number;
  mvps: number;
  clutches: number;
  trade_kills: number;
  trade_denials: number;
  failed_trades: number;
  ct_kills: number;
  t_kills: number;
  round_swing: number;
  economy_adjustment: number;
  multi_kills: Record<string, number>;
  rating: OpenRatingBreakdown | null;
  difference: number;
  adr: number;
  kast_percent: number;
  headshot_percent: number;
};

export type MatchResult = {
  schema_version: number;
  session_id: string;
  state: "prepared" | "launching" | "loading" | "warmup" | "live" | "finished" | "interrupted";
  map_id: string;
  started_at_unix: number;
  finished_at_unix: number;
  player_score: number;
  opponent_score: number;
  opponent_name: string;
  rating_model_version: string;
  demo: DemoStatus;
  players: PlayerMatchStats[];
  interruption_reason: string | null;
};

export type MatchSession = {
  schema_version: number;
  session_id: string;
  state: MatchResult["state"];
  map_id: string;
  opponent_name: string;
  created_at_unix: number;
  player_score: number;
  opponent_score: number;
  demo: DemoStatus;
  result_path: string | null;
};

export type InstallCheckStatus = "pass" | "warn" | "fail";
export type InstallCheckItem = {
  code: string;
  status: InstallCheckStatus;
  blocking: boolean;
  title: string;
  evidence: string;
  cause: string;
  action: string;
};
export type InstallCheckReport = {
  schema_version: number;
  generated_at_unix: number;
  target: string;
  overall: InstallCheckStatus;
  pass_count: number;
  warn_count: number;
  fail_count: number;
  blocking_fail_count: number;
  can_proceed: boolean;
  checks: InstallCheckItem[];
};

// ---- Command wrappers ----
export const api = {
  getConfig: () => invoke<AppConfig>("get_config"),
  saveConfig: (config: AppConfig) => invoke<void>("save_config", { config }),
  getAppearance: () => invoke<AppearanceConfig>("get_appearance"),
  saveAppearance: (config: AppearanceConfig) => invoke<AppearanceConfig>("save_appearance", { config }),
  exportAppearance: (destination: string) =>
    invoke<AppearanceExportResult>("export_appearance", { destination }),
  importAppearance: (source: string) =>
    invoke<AppearanceConfig>("import_appearance", { source }),
  getPanelMemory: () => invoke<UiMemory>("get_panel_memory"),
  savePanelMemory: (entries: Record<string, string>) =>
    invoke<UiMemory>("save_panel_memory", { entries }),
  recordPanelError: (error: AppError, context: string) =>
    invoke<void>("record_panel_error", { error: { ...error, context } }),
  getRuntimeSnapshot: () => invoke<RuntimeSnapshot>("get_runtime_snapshot"),
  detectDirectories: () => invoke<DirectoryInfo>("detect_directories"),
  selectDirectory: (path: string) =>
    invoke<DirectoryInfo>("select_directory", { path }),
  cleanupBackups: (csgo: string) => invoke<number>("cleanup_backups", { csgo }),
  validateFiles: (csgo: string) => invoke<FilesReport>("validate_files", { csgo }),
  getDifficulty: (csgo: string) => invoke<DifficultyInfo>("get_difficulty", { csgo }),
  setDifficulty: (csgo: string, level: DifficultyLevel) =>
    invoke<DifficultyInfo>("set_difficulty", { csgo, level }),
  getMode: (csgo: string) => invoke<ModeInfo>("get_mode", { csgo }),
  setMode: (csgo: string, mode: GameMode) =>
    invoke<ModeInfo>("set_mode", { csgo, mode }),
  reconcileLaunchOptions: () => invoke<number>("reconcile_launch_options"),
  launchCs2: () => invoke<LaunchResult>("launch_cs2"),
  getMatchCatalog: (csgo: string | null) => invoke<MatchCatalog>("get_match_catalog", { csgo }),
  prepareAndLaunchMatch: (csgo: string, input: PrepareMatchInput) =>
    invoke<MatchRequest>("prepare_and_launch_match", { csgo, input }),
  finishActiveMatch: (csgo: string, sessionId: string) =>
    invoke<MatchSession>("finish_active_match", { csgo, sessionId }),
  getActiveMatch: (csgo: string) => invoke<MatchSession | null>("get_active_match", { csgo }),
  listMatchHistory: (csgo: string) => invoke<MatchSession[]>("list_match_history", { csgo }),
  getMatchResult: (csgo: string, sessionId: string) =>
    invoke<MatchResult>("get_match_result", { csgo, sessionId }),
  deleteMatch: (csgo: string, sessionId: string, confirmed: boolean) =>
    invoke<void>("delete_match", { csgo, sessionId, confirmed }),
  playDemo: (csgo: string, demoPath: string) =>
    invoke<void>("play_demo", { csgo, demoPath }),
  openDemoFolder: (csgo: string, demoPath: string) =>
    invoke<void>("open_demo_folder", { csgo, demoPath }),
  runInstallChecks: (csgo: string, selectedMap: string | null = null) =>
    invoke<InstallCheckReport>("run_install_checks", { csgo, selectedMap }),
  reconcileCoreJson: (csgo: string) => invoke<void>("reconcile_core_json", { csgo }),
  getBotItems: (csgo: string) => invoke<BotItemsState>("get_bot_items", { csgo }),
  setBotItem: (csgo: string, item: BotItemKey, on: boolean) =>
    invoke<BotItemsState>("set_bot_item", { csgo, item, on }),
  getPresets: (csgo: string) => invoke<PresetsState>("get_presets", { csgo }),
  setAim: (csgo: string, value: AimValue) =>
    invoke<PresetsState>("set_aim", { csgo, value }),
  setNades: (csgo: string, value: NadesValue) =>
    invoke<PresetsState>("set_nades", { csgo, value }),
  getDropKnives: (csgo: string) =>
    invoke<DropKnivesState>("get_drop_knives", { csgo }),
  setDropKnives: (csgo: string, bindKey: string, selected: number[]) =>
    invoke<DropKnivesState>("set_drop_knives", { csgo, bindKey, selected }),
  getKnifeCustomizer: (csgo: string) =>
    invoke<KnifeCustomizerState>("get_knife_customizer", { csgo }),
  saveKnifeCustomizer: (csgo: string, config: KnifeCustomizerConfig) =>
    invoke<KnifeCustomizerState>("save_knife_customizer", { csgo, config }),
  exportCosmeticsPreset: (csgo: string, destination: string) =>
    invoke<CosmeticsPresetExportResult>("export_cosmetics_preset", { csgo, destination }),
  importCosmeticsPreset: (csgo: string, source: string) =>
    invoke<CosmeticsPresetImportResult>("import_cosmetics_preset", { csgo, source }),
  inspectInstallation: (csgo: string) =>
    invoke<InstallationInspection>("inspect_installation", { csgo }),
  getInstallPlan: (csgo: string) => invoke<InstallPlan>("get_install_plan", { csgo }),
  installPayload: (csgo: string) =>
    invoke<InstallTransactionResult>("install_payload", { csgo }),
  repairPayload: (csgo: string) =>
    invoke<InstallTransactionResult>("repair_payload", { csgo }),
  restorePayload: (csgo: string) => invoke<RestoreResult>("restore_payload", { csgo }),
  restorePristineCs2: (csgo: string) => invoke<RestoreResult>("restore_pristine_cs2", { csgo }),
  exportDiagnostics: (csgo: string | null) =>
    invoke<DiagnosticReport>("export_diagnostics", { csgo }),
  getUpdateSnapshot: () => invoke<OnlineUpdateSnapshot>("get_update_snapshot"),
  checkOnlineUpdates: (force: boolean) =>
    invoke<OnlineUpdateSnapshot>("check_online_updates", { force }),
  installPanelUpdate: () => invoke<UpdateResult>("install_panel_update"),
  installPluginUpdate: (csgo: string) =>
    invoke<UpdateResult>("install_plugin_update", { csgo }),
  installAllUpdates: (csgo: string | null) =>
    invoke<UpdateBatchResult>("install_all_updates", { csgo }),
  cancelUpdate: () => invoke<void>("cancel_update"),
};
