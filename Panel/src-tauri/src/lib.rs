use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use std::sync::OnceLock;
use sysinfo::{ProcessesToUpdate, System};
use tauri::{AppHandle, Manager};

mod app_storage;
mod app_version;
mod appearance;
mod atomic_fs;
mod diagnostics;
mod install_checks;
mod installer;
mod logging;
mod match_system;
mod mode_files;
mod mode_layout;
mod online_update;
mod runtime_state;
mod steam;
mod update_core;
use installer::{InstallPlan, InstallTransactionResult, InstallationInspection, RestoreResult};
use install_checks::InstallCheckReport;
use match_system::{MatchCatalog, MatchResult, MatchSession, MatchRequest, MatchState, PrepareMatchInput};
use mode_files::{LaunchMode, apply_launch_mode, contains_metamod_search_path};
use runtime_state::{Cs2ProcessInfo, blocks_target_write, inspect_cs2_process};

type Result<T> = std::result::Result<T, AppError>;

#[derive(Debug, Serialize)]
struct AppError {
    code: &'static str,
    category: &'static str,
    detail: String,
}

impl AppError {
    fn new(code: &'static str, category: &'static str, detail: impl Into<String>) -> Self {
        Self {
            code,
            category,
            detail: detail.into(),
        }
    }
    fn io(detail: impl Into<String>) -> Self {
        Self::new("E1001", "filesystem", detail)
    }
    fn invalid(detail: impl Into<String>) -> Self {
        Self::new("E1002", "validation", detail)
    }
    fn directory(detail: impl Into<String>) -> Self {
        Self::new("E1101", "directory", detail)
    }
    fn process(detail: impl Into<String>) -> Self {
        Self::new("E1201", "process", detail)
    }
    pub(crate) fn payload(detail: impl Into<String>) -> Self {
        Self::new("E1301", "payload", detail)
    }
    pub(crate) fn transaction(detail: impl Into<String>) -> Self {
        Self::new("E1401", "installation", detail)
    }
    fn preflight(detail: impl Into<String>) -> Self {
        Self::new("E1402", "installation", detail)
    }
    pub(crate) fn transaction_io(error: std::io::Error) -> Self {
        Self::transaction(error.to_string())
    }
    fn launch(detail: impl Into<String>) -> Self {
        Self::new("E1501", "launch", detail)
    }
    fn update(detail: impl Into<String>) -> Self {
        Self::new("E1601", "update", detail)
    }
}

impl From<std::io::Error> for AppError {
    fn from(value: std::io::Error) -> Self {
        Self::io(value.to_string())
    }
}

impl From<serde_json::Error> for AppError {
    fn from(value: serde_json::Error) -> Self {
        Self::invalid(value.to_string())
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
struct BotItems {
    skins: bool,
    profiles: bool,
    agents: bool,
    music: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct AppConfig {
    language: Option<String>,
    difficulty: Option<String>,
    mode: Option<String>,
    insecure: bool,
    bot_items: BotItems,
    aim: Option<String>,
    nades: Option<String>,
    drop_knife_bind: String,
    drop_knife_subclasses: Vec<u16>,
    csgo_path: Option<String>,
    first_run_done: bool,
    #[serde(default)]
    first_run_step: Option<String>,
    #[serde(default)]
    cosmetics_enabled_before_online: Option<bool>,
    #[serde(default)]
    cosmetics_enabled_before_preview: Option<bool>,
    #[serde(default)]
    experimental_features_enabled: bool,
    #[serde(default)]
    experimental_stickers_enabled: bool,
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            language: Some("schinese".into()),
            difficulty: Some("Medium".into()),
            mode: Some("bots".into()),
            insecure: true,
            bot_items: BotItems::default(),
            aim: Some("mixed".into()),
            nades: Some("normal".into()),
            drop_knife_bind: "\\".into(),
            drop_knife_subclasses: vec![],
            csgo_path: None,
            first_run_done: false,
            first_run_step: Some("language".into()),
            cosmetics_enabled_before_online: None,
            cosmetics_enabled_before_preview: None,
            experimental_features_enabled: false,
            experimental_stickers_enabled: false,
        }
    }
}

fn prepare_legacy_config_for_portable_state(mut config: AppConfig) -> AppConfig {
    config.first_run_done = false;
    config.first_run_step = Some("language".into());
    config.experimental_stickers_enabled = false;
    config
}

fn apply_release_feature_gates(config: &mut AppConfig) {
    config.experimental_stickers_enabled = false;
}

#[derive(Serialize)]
struct DirectoryInfo {
    candidates: Vec<String>,
    selected: Option<String>,
    valid: bool,
    needs_choice: bool,
    steam_found: bool,
}
#[derive(Serialize)]
struct FilesReport {
    ok: bool,
    total: usize,
    present: usize,
    missing: Vec<String>,
    misplaced: Option<String>,
}
#[derive(Serialize)]
struct DifficultyInfo {
    current: Option<String>,
    available: Vec<String>,
    active_present: bool,
    cs2_running: bool,
}
#[derive(Serialize)]
struct ModeInfo {
    current: Option<String>,
    online_present: bool,
    preview_present: bool,
    bots_present: bool,
    layout_healthy: bool,
    insecure: bool,
    user_count: u32,
    cs2_running: bool,
    pending: bool,
}
#[derive(Serialize)]
struct LaunchResult {
    options: String,
    insecure: bool,
}
#[derive(Serialize)]
struct BotItemsState {
    skins: bool,
    profiles: bool,
    agents: bool,
    music: bool,
    cfg_present: bool,
    cs2_running: bool,
}
#[derive(Serialize)]
struct PresetsState {
    aim: Option<String>,
    aim_supported: bool,
    aim_active: Option<bool>,
    aim_transport: Option<String>,
    aim_override_count: Option<u64>,
    aim_error_count: Option<u64>,
    nades: Option<String>,
    cfg_present: bool,
    cs2_running: bool,
}

#[derive(Deserialize)]
struct AimRuntimeStatus {
    schema_version: u8,
    transport: String,
    active: bool,
    override_count: u64,
    error_count: u64,
}
#[derive(Serialize)]
struct DropKnivesState {
    bind_key: String,
    selected: Vec<u16>,
    cfg_present: bool,
    cs2_running: bool,
}

#[derive(Serialize)]
struct RuntimeSnapshot {
    directory: DirectoryInfo,
    process: Cs2ProcessInfo,
    files: Option<FilesReport>,
    difficulty: Option<DifficultyInfo>,
    mode: Option<ModeInfo>,
    bot_items: Option<BotItemsState>,
    presets: Option<PresetsState>,
    drop_knives: Option<DropKnivesState>,
    installation: Option<InstallationInspection>,
}

#[derive(Deserialize)]
struct PanelErrorRecord {
    code: String,
    category: String,
    detail: String,
    context: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
struct StickerPreset {
    slot: u8,
    id: u32,
    wear: f32,
    scale: f32,
    rotation: f32,
    offset_x: f32,
    offset_y: f32,
    #[serde(default)]
    custom_position: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
struct KnifePreset {
    paint: i32,
    seed: i32,
    wear: f32,
    name_tag: String,
    stattrak_enabled: bool,
    stattrak_count: i32,
    #[serde(default)]
    souvenir_enabled: bool,
    #[serde(default)]
    stickers: Vec<StickerPreset>,
}

const DEFAULT_GLOVE_DEFINDEX: u16 = 5030;
const DEFAULT_GLOVE_PAINT: i32 = 10048;
const DEFAULT_GLOVE_WEAR: f32 = 0.01;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
struct GlovePreset {
    enabled: bool,
    defindex: u16,
    paint: i32,
    seed: i32,
    wear: f32,
}

impl Default for GlovePreset {
    fn default() -> Self {
        Self {
            enabled: false,
            defindex: DEFAULT_GLOVE_DEFINDEX,
            paint: DEFAULT_GLOVE_PAINT,
            seed: 0,
            wear: DEFAULT_GLOVE_WEAR,
        }
    }
}

const COSMETICS_SCHEMA_VERSION: u8 = 3;
const STICKER_RELEASE_ENABLED: bool = false;
const CT_ONLY_WEAPONS: &[u16] = &[3, 8, 10, 16, 27, 32, 34, 38, 60, 61];
const T_ONLY_WEAPONS: &[u16] = &[4, 7, 11, 13, 17, 29, 30, 39];
const SHARED_WEAPONS: &[u16] = &[
    1, 2, 9, 14, 19, 23, 24, 25, 26, 28, 31, 33, 35, 36, 40, 63, 64,
];

#[derive(Clone, Copy, Debug, PartialEq)]
enum WeaponSide {
    Ct,
    T,
    Shared,
}

fn weapon_side(defindex: u16) -> WeaponSide {
    if CT_ONLY_WEAPONS.contains(&defindex) {
        WeaponSide::Ct
    } else if T_ONLY_WEAPONS.contains(&defindex) {
        WeaponSide::T
    } else {
        WeaponSide::Shared
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
struct TeamLoadout {
    #[serde(default)]
    default_knife_defindex: u16,
    #[serde(default)]
    knife_presets: BTreeMap<String, KnifePreset>,
    #[serde(default)]
    glove: GlovePreset,
    #[serde(default)]
    gun_presets: BTreeMap<String, KnifePreset>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
struct TeamLoadouts {
    #[serde(default)]
    ct: TeamLoadout,
    #[serde(default)]
    t: TeamLoadout,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
struct TeamGunConfig {
    #[serde(default)]
    schema_version: u8,
    #[serde(default)]
    ct: BTreeMap<String, KnifePreset>,
    #[serde(default)]
    t: BTreeMap<String, KnifePreset>,
    #[serde(default)]
    shared_weapon_links: BTreeMap<String, bool>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct KnifeCustomizerConfig {
    #[serde(default)]
    schema_version: u8,
    #[serde(default)]
    enabled: bool,
    #[serde(default = "default_true")]
    apply_to_human_players: bool,
    #[serde(default = "default_true")]
    apply_on_pickup: bool,
    #[serde(default)]
    music_kit_id: i32,
    #[serde(default)]
    loadouts: TeamLoadouts,
    #[serde(default)]
    shared_weapon_links: BTreeMap<String, bool>,
    #[serde(default)]
    stickers_enabled: bool,

    // Read-only v1 fields. They are migrated in memory and never serialized again.
    #[serde(default, skip_serializing)]
    default_knife_defindex: u16,
    #[serde(default, skip_serializing)]
    presets: BTreeMap<String, KnifePreset>,
    #[serde(default, skip_serializing)]
    gun_presets: BTreeMap<String, KnifePreset>,
    #[serde(default, skip_serializing)]
    glove: GlovePreset,
}

fn default_true() -> bool {
    true
}

#[derive(Deserialize)]
struct StickerCatalogEntry {
    id: u32,
}

fn valid_sticker_ids() -> &'static BTreeSet<u32> {
    static IDS: OnceLock<BTreeSet<u32>> = OnceLock::new();
    IDS.get_or_init(|| {
        serde_json::from_str::<Vec<StickerCatalogEntry>>(include_str!("../../src/data/stickerCatalog.json"))
            .unwrap_or_default()
            .into_iter()
            .map(|entry| entry.id)
            .collect()
    })
}

fn valid_sticker_weapon_ids() -> &'static BTreeSet<u16> {
    static IDS: OnceLock<BTreeSet<u16>> = OnceLock::new();
    IDS.get_or_init(|| {
        serde_json::from_str::<Vec<StickerCatalogEntry>>(include_str!(
            "../../src/data/stickerWeaponIds.json"
        ))
        .unwrap_or_default()
        .into_iter()
        .filter_map(|entry| u16::try_from(entry.id).ok())
        .collect()
    })
}

impl Default for KnifeCustomizerConfig {
    fn default() -> Self {
        let mut shared_weapon_links = BTreeMap::new();
        for defindex in SHARED_WEAPONS {
            shared_weapon_links.insert(defindex.to_string(), true);
        }
        Self {
            schema_version: COSMETICS_SCHEMA_VERSION,
            enabled: false,
            apply_to_human_players: true,
            apply_on_pickup: true,
            music_kit_id: 0,
            loadouts: TeamLoadouts::default(),
            shared_weapon_links,
            stickers_enabled: false,
            default_knife_defindex: 0,
            presets: BTreeMap::new(),
            gun_presets: BTreeMap::new(),
            glove: GlovePreset::default(),
        }
    }
}

#[derive(Serialize)]
struct KnifeCustomizerState {
    plugin_present: bool,
    config_present: bool,
    cs2_running: bool,
    config: KnifeCustomizerConfig,
}

const COSMETICS_EXPORT_SCHEMA_VERSION: u8 = 2;
const LEGACY_COSMETICS_EXPORT_SCHEMA_VERSION: u8 = 1;
const COSMETICS_EXPORT_KIND: &str = "cs2bip-cosmetics-preset";
const MAX_COSMETICS_IMPORT_BYTES: u64 = 4 * 1024 * 1024;

#[derive(Clone, Debug, Serialize, Deserialize)]
struct CosmeticsPresetBundle {
    schema_version: u8,
    kind: String,
    exported_at_unix: u64,
    config: KnifeCustomizerConfig,
}

#[derive(Serialize)]
struct CosmeticsPresetExportResult {
    path: String,
    size_bytes: u64,
}

#[derive(Serialize)]
struct CosmeticsPresetImportResult {
    state: KnifeCustomizerState,
    backup_path: Option<String>,
}

fn legacy_config_path(app: &AppHandle) -> Result<PathBuf> {
    let dir = app
        .path()
        .app_config_dir()
        .map_err(|e| AppError::io(e.to_string()))?;
    fs::create_dir_all(&dir)?;
    Ok(dir.join("config.json"))
}

fn config_path() -> Result<PathBuf> {
    app_storage::panel_config_path()
}

fn read_config(app: &AppHandle) -> Result<AppConfig> {
    let path = config_path()?;
    if !path.is_file() {
        let legacy = legacy_config_path(app)?;
        if legacy.is_file() {
            let config: AppConfig = serde_json::from_str(&fs::read_to_string(&legacy)?)?;
            let config = prepare_legacy_config_for_portable_state(config);
            write_json_atomic(&path, &config)?;
            if let Ok(root) = app_storage::root() {
                logging::append(&root, "INFO", "config.migrated", &legacy.to_string_lossy());
            }
            return Ok(config);
        }
    }
    if !path.exists() {
        return Ok(AppConfig::default());
    }
    let mut config: AppConfig = serde_json::from_str(&fs::read_to_string(path)?)?;
    apply_release_feature_gates(&mut config);
    Ok(config)
}

fn write_json_atomic<T: Serialize>(path: &Path, value: &T) -> Result<()> {
    let bytes = serde_json::to_vec_pretty(value)?;
    atomic_fs::write_replace(path, &bytes).map_err(AppError::transaction_io)
}

fn write_config(_app: &AppHandle, config: &AppConfig) -> Result<()> {
    let mut config = config.clone();
    apply_release_feature_gates(&mut config);
    write_json_atomic(&config_path()?, &config)
}

fn cs2_running() -> bool {
    inspect_cs2_process(None).running
}

fn ensure_target_not_running(root: &Path) -> Result<()> {
    let process = inspect_cs2_process(Some(root));
    if blocks_target_write(&process) {
        let detail = match (&process.executable, process.path_accessible) {
            (Some(path), _) => format!("Close CS2 before modifying this installation (PID {}, {path})", process.pid.unwrap_or_default()),
            (_, false) => "A running cs2.exe could not be matched to an installation. Close CS2, then try again".to_string(),
            _ => "Close CS2 before modifying this installation".to_string(),
        };
        return Err(AppError::process(detail));
    }
    Ok(())
}

fn ensure_steam_app_idle(root: &Path) -> Result<()> {
    steam::wait_for_app_730_idle(root, Duration::from_secs(15)).map_err(|detail| {
        AppError::transaction(format!(
            "Steam is modifying CS2 files. Pause the App 730 update or verification, wait for Steam activity to finish, then retry installation. {detail}"
        ))
    })?;
    Ok(())
}

fn valid_csgo(path: &Path) -> bool {
    path.join("gameinfo.gi").is_file() && path.join("cfg").is_dir()
}

fn normalize_windows_canonical(path: PathBuf) -> PathBuf {
    let raw = path.to_string_lossy();
    if let Some(value) = raw.strip_prefix(r"\\?\UNC\") {
        return PathBuf::from(format!(r"\\{value}"));
    }
    if let Some(value) = raw.strip_prefix(r"\\?\") {
        return PathBuf::from(value);
    }
    path
}

fn csgo_path(raw: &str) -> Result<PathBuf> {
    let selected = PathBuf::from(raw.trim().trim_matches('"'));
    let candidates = [
        selected.clone(),
        selected.join("game").join("csgo"),
        selected.join("csgo"),
    ];
    for candidate in candidates {
        if valid_csgo(&candidate) {
            let canonical = fs::canonicalize(&candidate).map_err(|error| {
                AppError::directory(format!(
                    "Cannot resolve the selected CS2 directory ({}): {error}",
                    candidate.display()
                ))
            })?;
            return Ok(normalize_windows_canonical(canonical));
        }
    }
    Err(AppError::directory(format!(
        "The selected path is not a CS2 installation root, game directory, or game/csgo directory: {}",
        selected.display()
    )))
}

fn cfg_paths(csgo: &Path) -> [PathBuf; 2] {
    [
        csgo.join("cfg/my_bot_normal_config.cfg"),
        csgo.join("cfg/my_bot_ffa_config.cfg"),
    ]
}

fn cfg_files_present(csgo: &Path) -> bool {
    cfg_paths(csgo)
        .iter()
        .all(|path| mode_layout::active_or_disabled(path).is_some())
}

fn replace_managed_cfg_command(csgo: &Path, command: &str, replacement: &str) -> Result<()> {
    for canonical in cfg_paths(csgo) {
        let path = mode_layout::active_or_disabled(&canonical).unwrap_or(canonical);
        replace_cfg_command(&path, command, replacement)?;
    }
    Ok(())
}

fn replace_cfg_command(path: &Path, command: &str, replacement: &str) -> Result<()> {
    let text = fs::read_to_string(path)?;
    let mut found = false;
    let mut lines = Vec::new();
    for line in text.lines() {
        if line.trim_start().starts_with(command) {
            if !found {
                lines.push(replacement.to_string());
                found = true;
            }
        } else {
            lines.push(line.to_string());
        }
    }
    if !found {
        lines.push(replacement.to_string());
    }
    fs::write(path, format!("{}\r\n", lines.join("\r\n")))?;
    Ok(())
}

#[tauri::command]
fn get_config(app: AppHandle) -> Result<AppConfig> {
    read_config(&app)
}

#[tauri::command]
fn save_config(app: AppHandle, config: AppConfig) -> Result<()> {
    let current = read_config(&app)?;
    if cs2_running()
        && (current.experimental_features_enabled != config.experimental_features_enabled
            || current.experimental_stickers_enabled != config.experimental_stickers_enabled)
    {
        return Err(AppError::process(
            "Close CS2 before changing experimental feature settings",
        ));
    }
    write_config(&app, &config)
}

#[tauri::command]
fn detect_directories(app: AppHandle) -> Result<DirectoryInfo> {
    let mut config = read_config(&app)?;
    let mut candidates = Vec::new();
    if let Some(path) = &config.csgo_path {
        if let Ok(resolved) = csgo_path(path) {
            candidates.push(resolved.to_string_lossy().into_owned());
        }
    }
    for path in steam::detect_cs2_directories() {
        let path = path.to_string_lossy().into_owned();
        if !candidates
            .iter()
            .any(|candidate| steam::paths_equal(Path::new(candidate), Path::new(&path)))
        {
            candidates.push(path);
        }
    }
    let saved = config
        .csgo_path
        .clone()
        .filter(|p| valid_csgo(Path::new(p)));
    let selected = saved.or_else(|| (candidates.len() == 1).then(|| candidates[0].clone()));
    if selected.is_some() && config.csgo_path != selected {
        config.csgo_path = selected.clone();
        write_config(&app, &config)?;
    }
    Ok(DirectoryInfo {
        valid: selected.is_some(),
        needs_choice: candidates.len() > 1 && selected.is_none(),
        steam_found: !candidates.is_empty(),
        candidates,
        selected,
    })
}

#[tauri::command]
fn select_directory(app: AppHandle, path: String) -> Result<DirectoryInfo> {
    let resolved = csgo_path(&path)?.to_string_lossy().into_owned();
    let mut config = read_config(&app)?;
    config.csgo_path = Some(resolved);
    write_config(&app, &config)?;
    detect_directories(app)
}

fn payload_root() -> Result<PathBuf> {
    if let Some(payload) = online_update::active_payload_root() {
        return Ok(payload);
    }
    let executable =
        std::env::current_exe().map_err(|error| AppError::payload(error.to_string()))?;
    executable
        .parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| AppError::payload("Panel executable has no parent directory"))
}

fn local_state_root(_app: &AppHandle) -> Result<PathBuf> {
    app_storage::root()
}

#[tauri::command]
fn get_panel_memory() -> Result<app_storage::UiMemory> {
    app_storage::read_ui_memory()
}

#[tauri::command]
fn save_panel_memory(entries: BTreeMap<String, String>) -> Result<app_storage::UiMemory> {
    app_storage::write_ui_memory(entries)
}

#[tauri::command]
fn record_panel_error(error: PanelErrorRecord) -> Result<()> {
    let root = app_storage::root()?;
    let detail = format!(
        "{} [{}] {} | {}",
        error.code,
        error.category,
        error.detail,
        error.context.unwrap_or_else(|| "no-context".to_string())
    );
    logging::append(&root, "ERROR", "panel.error", &detail);
    Ok(())
}

#[tauri::command]
fn cleanup_backups(_csgo: String) -> u32 {
    0
}

fn validate_files_at(
    app: Option<&AppHandle>,
    root: &Path,
    verify_hashes: bool,
) -> Result<FilesReport> {
    if let Some(app) = app {
        if let (Ok(payload), Ok(state)) = (payload_root(), local_state_root(app)) {
            let inspection = if verify_hashes {
                installer::inspect(&payload, &state, root)
            } else {
                installer::inspect_quick(&payload, &state, root)
            };
            if let Ok(inspection) = inspection {
                if inspection.total > 0 {
                    let mut missing = inspection.missing;
                    missing.extend(
                        inspection
                            .corrupt
                            .into_iter()
                            .map(|path| format!("{path} (hash mismatch)")),
                    );
                    return Ok(FilesReport {
                        ok: missing.is_empty(),
                        total: inspection.total,
                        present: inspection.healthy,
                        missing,
                        misplaced: None,
                    });
                }
            }
        }
    }
    let required = [
        "gameinfo.gi",
        "cfg/my_bot_normal_config.cfg",
        "cfg/my_bot_ffa_config.cfg",
        "addons/counterstrikesharp/plugins/BotAI/BotAI.dll",
        "addons/counterstrikesharp/plugins/BotRandomizer/BotRandomizer.dll",
        "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/PlayerKnifeCustomizer.dll",
        "addons/MetaMod/bin/win64/server.dll",
        "addons/counterstrikesharp/bin/win64/counterstrikesharp.dll",
    ];
    let missing: Vec<String> = required
        .iter()
        .filter(|p| {
            let canonical = root.join(p);
            !canonical.is_file() && !mode_layout::disabled_path(&canonical).is_file()
        })
        .map(|p| p.to_string())
        .collect();
    Ok(FilesReport {
        ok: missing.is_empty(),
        total: required.len(),
        present: required.len() - missing.len(),
        missing,
        misplaced: None,
    })
}

#[tauri::command]
fn validate_files(app: AppHandle, csgo: String) -> Result<FilesReport> {
    let root = csgo_path(&csgo)?;
    validate_files_at(Some(&app), &root, true)
}

fn same_file(a: &Path, b: &Path) -> bool {
    fs::read(a)
        .ok()
        .zip(fs::read(b).ok())
        .is_some_and(|(a, b)| a == b)
}

fn difficulty_at(root: &Path, running: bool) -> DifficultyInfo {
    let active = root.join("overrides/botprofile.vpk");
    let selected = mode_layout::active_or_disabled(&active);
    let current = ["Low", "Medium", "High"]
        .iter()
        .find(|name| {
            selected.as_deref().is_some_and(|path| {
                same_file(path, &root.join(format!("overrides/{name}/botprofile.vpk")))
            })
        })
        .map(|name| name.to_string());
    DifficultyInfo {
        current,
        available: vec!["Low".into(), "Medium".into(), "High".into()],
        active_present: selected.is_some(),
        cs2_running: running,
    }
}

fn set_difficulty_at(
    root: &Path,
    state: &Path,
    level: &str,
    running: bool,
) -> Result<DifficultyInfo> {
    if !["Low", "Medium", "High"].contains(&level) {
        return Err(AppError::invalid("Unknown difficulty"));
    }
    let source = root.join(format!("overrides/{level}/botprofile.vpk"));
    let bytes = fs::read(&source).map_err(|error| {
        AppError::payload(format!(
            "The {level} difficulty profile is missing or unreadable ({}): {error}",
            source.display()
        ))
    })?;
    mode_layout::write_managed_file(
        root,
        "overrides/botprofile.vpk",
        &bytes,
        mode_layout::is_preview(state, root),
    )?;
    Ok(difficulty_at(root, running))
}

#[tauri::command]
fn get_difficulty(csgo: String) -> Result<DifficultyInfo> {
    let root = csgo_path(&csgo)?;
    Ok(difficulty_at(
        &root,
        inspect_cs2_process(Some(&root)).running,
    ))
}

#[tauri::command]
fn set_difficulty(app: AppHandle, csgo: String, level: String) -> Result<DifficultyInfo> {
    let root = csgo_path(&csgo)?;
    let running = inspect_cs2_process(Some(&root)).running;
    set_difficulty_at(&root, &local_state_root(&app)?, &level, running)
}

#[tauri::command]
fn get_mode(app: AppHandle, csgo: String) -> Result<ModeInfo> {
    let root = csgo_path(&csgo)?;
    let config = read_config(&app)?;
    Ok(mode_at(
        &root,
        &config,
        inspect_cs2_process(Some(&root)).running,
    ))
}

fn mode_at(root: &Path, config: &AppConfig, running: bool) -> ModeInfo {
    let gameinfo = root.join("gameinfo.gi");
    let online_present = gameinfo.is_file();
    let bots_present = gameinfo.is_file()
        && root.join("addons/metamod/counterstrikesharp.vdf").is_file()
        && root.join("overrides/botprofile.vpk").is_file();
    let preview_present = root
        .join("addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/PlayerKnifeCustomizer.dll")
        .is_file();
    let state = app_storage::root().ok();
    let current = fs::read(&gameinfo).ok().map(|bytes| {
        if !contains_metamod_search_path(&bytes) {
            "online".into()
        } else if state
            .as_deref()
            .is_some_and(|state| mode_layout::is_preview(state, root))
        {
            "preview".into()
        } else {
            "bots".into()
        }
    });
    let expects_managed_plugins_disabled = current.as_deref() != Some("bots");
    let layout_healthy = mode_layout::layout_healthy(root, expects_managed_plugins_disabled);
    ModeInfo {
        pending: current.as_deref() != config.mode.as_deref() || !layout_healthy,
        current,
        online_present,
        preview_present,
        bots_present,
        layout_healthy,
        insecure: config.insecure,
        user_count: 1,
        cs2_running: running,
    }
}

#[tauri::command]
fn set_mode(app: AppHandle, csgo: String, mode: String) -> Result<ModeInfo> {
    let root = csgo_path(&csgo)?;
    ensure_target_not_running(&root)?;
    let launch_mode = LaunchMode::parse(Some(&mode)).map_err(AppError::invalid)?;
    let state = local_state_root(&app)?;
    mode_layout::recover(&state, &root)?;
    apply_launch_mode(&root, launch_mode).map_err(AppError::invalid)?;
    mode_layout::set_preview(&state, &root, launch_mode != LaunchMode::Bots)?;
    let mut config = read_config(&app)?;
    enforce_mode_cosmetics(&root, &mut config, launch_mode)?;
    write_bot_randomizer_options(&root, &config.bot_items)?;
    config.mode = Some(mode.clone());
    config.insecure = launch_mode.insecure();
    write_config(&app, &config)?;
    get_mode(app, csgo)
}

#[tauri::command]
fn reconcile_launch_options() -> u32 {
    0
}

fn find_steam_executable() -> Result<PathBuf> {
    let mut system = System::new();
    system.refresh_processes(ProcessesToUpdate::All, true);
    if let Some(path) = system
        .processes()
        .values()
        .find(|process| process.name().eq_ignore_ascii_case("steam.exe"))
        .and_then(|process| process.exe())
        .filter(|path| path.is_file())
    {
        return Ok(path.to_path_buf());
    }

    let mut candidates = Vec::new();
    for variable in ["ProgramFiles(x86)", "ProgramFiles"] {
        if let Some(root) = std::env::var_os(variable) {
            candidates.push(PathBuf::from(root).join("Steam/Steam.exe"));
        }
    }
    candidates.push(PathBuf::from(r"C:\Program Files (x86)\Steam\Steam.exe"));

    candidates
        .into_iter()
        .find(|path| path.is_file())
        .ok_or_else(|| AppError::launch("Steam.exe was not found. Start Steam, then try again"))
}

fn launch_request(mode: LaunchMode) -> (Vec<&'static str>, String) {
    if mode.insecure() {
        (
            vec!["-applaunch", "730", "-insecure", "-console"],
            "-insecure -console".into(),
        )
    } else {
        (vec!["-applaunch", "730"], String::new())
    }
}

#[tauri::command]
fn launch_cs2(app: AppHandle) -> Result<LaunchResult> {
    let mut config = read_config(&app)?;
    let mode = LaunchMode::parse(config.mode.as_deref()).map_err(AppError::invalid)?;
    let configured_path = config.csgo_path.as_deref().ok_or_else(|| {
        AppError::directory("Select the CS2 game/csgo directory before launching")
    })?;
    let root = csgo_path(configured_path)?;
    ensure_target_not_running(&root)?;
    let state = local_state_root(&app)?;
    mode_layout::recover(&state, &root)?;
    apply_launch_mode(&root, mode).map_err(AppError::invalid)?;
    mode_layout::set_preview(&state, &root, mode != LaunchMode::Bots)?;
    enforce_mode_cosmetics(&root, &mut config, mode)?;
    write_bot_randomizer_options(&root, &config.bot_items)?;

    config.insecure = mode.insecure();
    write_config(&app, &config)?;
    let steam = find_steam_executable()?;
    let (arguments, options) = launch_request(mode);
    Command::new(steam).args(arguments).spawn()?;
    Ok(LaunchResult {
        options,
        insecure: mode.insecure(),
    })
}

#[tauri::command]
fn get_match_catalog(_app: AppHandle, csgo: Option<String>) -> Result<MatchCatalog> {
    let selected = csgo.as_deref().map(csgo_path).transpose()?;
    match_system::load_catalog(&payload_root()?, selected.as_deref())
}

fn match_launch_arguments(record_demo: bool, map: &str) -> Vec<String> {
    [
        "-applaunch", "730", "-worldwide", "-insecure", "-console",
        "+game_type", "0", "+game_mode", "1", "+tv_enable",
        if record_demo { "1" } else { "0" }, "+map", map,
    ].into_iter().map(str::to_owned).collect()
}

#[tauri::command]
fn prepare_and_launch_match(app: AppHandle, csgo: String, input: PrepareMatchInput) -> Result<MatchRequest> {
    let root = csgo_path(&csgo)?;
    ensure_target_not_running(&root)?;
    let state = local_state_root(&app)?;
    let payload = payload_root()?;
    let difficulty = match input.difficulty.as_str() {
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        _ => return Err(AppError::invalid("Difficulty must be low, medium, or high")),
    };
    let previous_mode = LaunchMode::parse(read_config(&app)?.mode.as_deref()).map_err(AppError::invalid)?;
    mode_layout::recover(&state, &root)?;
    apply_launch_mode(&root, LaunchMode::Bots).map_err(AppError::invalid)?;
    mode_layout::set_preview(&state, &root, false)?;
    let preparation = (|| -> Result<()> {
        let report = collect_install_checks(&payload, &state, &root, Some(&input.map_id))?;
        ensure_install_checks_pass(&report)?;
        ensure_match_components_pass(&report)
    })();
    if let Err(error) = preparation {
        let _ = restore_demo_layout(&state, &root, previous_mode);
        return Err(error);
    }
    if let Err(error) = set_difficulty_at(&root, &state, difficulty, false) {
        let _ = restore_demo_layout(&state, &root, previous_mode);
        return Err(error);
    }
    let request = match match_system::prepare(&root, &payload, input) {
        Ok(request) => request,
        Err(error) => {
            let _ = restore_demo_layout(&state, &root, previous_mode);
            return Err(error);
        }
    };
    if let Err(error) = match_system::watch(app.clone(), &root) {
        let _ = match_system::interrupt_active(&root, "WATCHER_FAILED", &error.detail, true);
        let _ = restore_demo_layout(&state, &root, previous_mode);
        return Err(error);
    }
    let steam = match find_steam_executable() {
        Ok(steam) => steam,
        Err(error) => {
            let _ = match_system::interrupt_active(&root, "STEAM_NOT_FOUND", &error.detail, true);
            let _ = restore_demo_layout(&state, &root, previous_mode);
            return Err(error);
        }
    };
    let arguments = match_launch_arguments(request.record_demo, &request.map_id);
    if let Err(error) = Command::new(steam)
        .args(arguments)
        .spawn()
    {
        let _ = match_system::interrupt_active(&root, "LAUNCH_FAILED", &format!("steam_launch_failed: {error}"), true);
        let _ = restore_demo_layout(&state, &root, previous_mode);
        return Err(AppError::launch(format!("Steam could not launch the match map: {error}")));
    }
    monitor_match_process(root, request.session_id.clone());
    Ok(request)
}

#[tauri::command]
fn finish_active_match(csgo: String, session_id: String) -> Result<MatchSession> {
    match_system::finish_active(&csgo_path(&csgo)?, &session_id)
}

fn monitor_match_process(root: PathBuf, session_id: String) {
    std::thread::spawn(move || {
        let launch_deadline = Instant::now() + Duration::from_secs(120);
        loop {
            if !match_system::active(&root)
                .ok()
                .flatten()
                .is_some_and(|session| session.session_id == session_id)
            {
                return;
            }
            if cs2_running() {
                break;
            }
            if Instant::now() >= launch_deadline {
                let _ = match_system::interrupt_active(
                    &root,
                    "CS2_LAUNCH_TIMEOUT",
                    "cs2_process_did_not_start_within_120_seconds",
                    false,
                );
                return;
            }
            std::thread::sleep(Duration::from_millis(500));
        }

        loop {
            std::thread::sleep(Duration::from_secs(1));
            if !match_system::active(&root)
                .ok()
                .flatten()
                .is_some_and(|session| session.session_id == session_id && matches!(
                    session.state,
                    MatchState::Launching | MatchState::Loading | MatchState::Warmup | MatchState::Live
                ))
            {
                return;
            }
            if !cs2_running() {
                let _ = match_system::interrupt_active(
                    &root,
                    "CS2_EXITED",
                    "cs2_process_exited_before_match_completion",
                    false,
                );
                return;
            }
        }
    });
}

fn restore_demo_layout(state: &Path, root: &Path, mode: LaunchMode) -> Result<()> {
    mode_layout::recover(state, root)?;
    apply_launch_mode(root, mode).map_err(AppError::launch)?;
    mode_layout::set_preview(state, root, mode != LaunchMode::Bots)
}

fn selected_cs2_running(root: &Path) -> bool {
    let process = inspect_cs2_process(Some(root));
    process.matches_selected || (process.running && !process.path_accessible)
}

const DEMO_PLAYBACK_CONFIG_PREFIX: &str = "csbip_play_demo_";
const DEMO_LAUNCH_ARGUMENTS: [&str; 6] = [
    "-applaunch",
    "730",
    "-worldwide",
    "-console",
    "-condebug",
    "+exec",
];

fn demo_playback_config(argument: &str) -> Result<String> {
    if argument.contains(['\r', '\n', '"']) {
        return Err(AppError::invalid(
            "Demo path contains characters that cannot be passed to the CS2 console",
        ));
    }
    Ok(format!("playdemo \"{argument}\"\n"))
}

fn demo_playback_confirmed(log: &str, argument: &str, config_name: &str) -> bool {
    log.contains(&format!("execing {config_name}"))
        && log.contains(&format!("Playing Demo ({argument})"))
        && log.contains("CSGO_GAME_UI_STATE_INGAME")
}

fn wait_for_demo_playback(
    root: &Path,
    argument: &str,
    config_name: &str,
    deadline: Instant,
) -> Result<()> {
    let console = root.join("console.log");
    while Instant::now() < deadline {
        if !selected_cs2_running(root) {
            return Err(AppError::launch(
                "CS2 exited before Demo playback reached the in-game view",
            ));
        }
        if fs::read_to_string(&console)
            .is_ok_and(|log| demo_playback_confirmed(&log, argument, config_name))
        {
            return Ok(());
        }
        std::thread::sleep(Duration::from_millis(500));
    }
    Err(AppError::launch(
        "CS2 started, but Demo playback did not reach the in-game view within 120 seconds",
    ))
}

fn monitor_demo_process(state: PathBuf, root: PathBuf, previous_mode: LaunchMode) {
    std::thread::spawn(move || {
        while selected_cs2_running(&root) {
            std::thread::sleep(Duration::from_secs(1));
        }
        logging::append(
            &state,
            "INFO",
            "demo.play.exited",
            &format!("target={}", root.display()),
        );
        match restore_demo_layout(&state, &root, previous_mode) {
            Ok(()) => logging::append(
                &state,
                "INFO",
                "demo.play.layout_restored",
                &format!("target={}, mode={previous_mode:?}", root.display()),
            ),
            Err(error) => logging::append(
                &state,
                "ERROR",
                "demo.play.restore_failed",
                &format!("target={}, detail={}", root.display(), error.detail),
            ),
        }
    });
}

fn managed_demo_location(root: &Path, demo_path: &str) -> Result<(PathBuf, PathBuf)> {
    let managed = fs::canonicalize(root.join("demos/csbip"))
        .map_err(|error| AppError::invalid(format!("Demo directory is unavailable: {error}")))?;
    let demo = PathBuf::from(demo_path);
    if demo.extension()
        .and_then(|value| value.to_str())
        .is_none_or(|value| !value.eq_ignore_ascii_case("dem"))
    {
        return Err(AppError::invalid("Demo path must identify a .dem file"));
    }
    let parent = demo
        .parent()
        .ok_or_else(|| AppError::invalid("Demo path has no managed parent directory"))?;
    let canonical_parent = fs::canonicalize(parent)
        .map_err(|error| AppError::invalid(format!("Demo directory is unavailable: {error}")))?;
    if !canonical_parent
        .to_string_lossy()
        .eq_ignore_ascii_case(&managed.to_string_lossy())
    {
        return Err(AppError::invalid(
            "Demo path is outside the managed CS2BotImproverPlus directory",
        ));
    }
    Ok((managed, demo))
}

fn demo_playback_argument(root: &Path, demo: &Path) -> String {
    let normalized_root = normalize_windows_canonical(root.to_path_buf());
    let normalized_demo = normalize_windows_canonical(demo.to_path_buf());
    normalized_demo.strip_prefix(&normalized_root)
        .map(|path| path.to_string_lossy().replace('\\', "/"))
        .unwrap_or_else(|_| normalized_demo.to_string_lossy().into_owned())
}

#[tauri::command]
fn open_demo_folder(app: AppHandle, csgo: String, demo_path: String) -> Result<()> {
    let root = csgo_path(&csgo)?;
    let (directory, demo) = managed_demo_location(&root, &demo_path)?;
    let mut command = Command::new("explorer.exe");
    if demo.is_file() {
        command.arg(format!("/select,{}", demo.display()));
    } else {
        command.arg(&directory);
    }
    command
        .spawn()
        .map_err(|error| AppError::launch(format!("Cannot open the Demo directory: {error}")))?;
    if let Ok(state) = local_state_root(&app) {
        logging::append(
            &state,
            "INFO",
            "demo.folder.opened",
            &format!("directory={}, demo={}", directory.display(), demo.display()),
        );
    }
    Ok(())
}

#[tauri::command]
fn play_demo(app: AppHandle, csgo: String, demo_path: String) -> Result<()> {
    let root = csgo_path(&csgo)?;
    ensure_target_not_running(&root)?;
    let demo = match_system::validate_playable_demo(&root, Path::new(&demo_path))?;
    let state = local_state_root(&app)?;
    let config = read_config(&app)?;
    let previous_mode = LaunchMode::parse(config.mode.as_deref()).map_err(AppError::invalid)?;
    let steam = find_steam_executable()?;
    // CS2's +playdemo resolves paths relative to game/csgo, so prefer the
    // relative form with forward slashes and fall back to the absolute path.
    let argument = demo_playback_argument(&root, &demo);
    let request_id = format!(
        "{}",
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos()
    );
    let playback_config_name = format!("{DEMO_PLAYBACK_CONFIG_PREFIX}{request_id}.cfg");
    let playback_config = root.join("cfg").join(&playback_config_name);
    let config_content = demo_playback_config(&argument)?;
    logging::append(
        &state,
        "INFO",
        "demo.play.requested",
        &format!(
            "target={}, demo={}, argument={}, previous_mode={previous_mode:?}",
            root.display(),
            demo.display(),
            argument
        ),
    );
    mode_layout::recover(&state, &root)?;
    if let Err(detail) = apply_launch_mode(&root, LaunchMode::Online) {
        let launch_error = AppError::launch(detail);
        let restore_error = restore_demo_layout(&state, &root, previous_mode).err();
        logging::append(
            &state,
            "ERROR",
            "demo.play.failed",
            &format!(
                "detail={}, restore={}",
                launch_error.detail,
                restore_error
                    .as_ref()
                    .map_or("ok", |value| value.detail.as_str())
            ),
        );
        return Err(restore_error.unwrap_or(launch_error));
    }
    if let Err(error) = mode_layout::set_preview(&state, &root, true) {
        let _ = restore_demo_layout(&state, &root, previous_mode);
        logging::append(&state, "ERROR", "demo.play.failed", &error.detail);
        return Err(error);
    }

    if let Err(error) = atomic_fs::write_replace(&playback_config, config_content.as_bytes()) {
        let launch_error = AppError::transaction_io(error);
        let restore_error = restore_demo_layout(&state, &root, previous_mode).err();
        logging::append(
            &state,
            "ERROR",
            "demo.play.failed",
            &format!(
                "detail={}, restore={}",
                launch_error.detail,
                restore_error
                    .as_ref()
                    .map_or("ok", |value| value.detail.as_str())
            ),
        );
        return Err(restore_error.unwrap_or(launch_error));
    }
    if let Err(error) = Command::new(steam)
        .args(DEMO_LAUNCH_ARGUMENTS)
        .arg(&playback_config_name)
        .spawn()
    {
        let _ = fs::remove_file(&playback_config);
        let launch_error = AppError::launch(format!("Steam could not launch the Demo: {error}"));
        let restore_error = restore_demo_layout(&state, &root, previous_mode).err();
        logging::append(
            &state,
            "ERROR",
            "demo.play.failed",
            &format!(
                "detail={}, restore={}",
                launch_error.detail,
                restore_error
                    .as_ref()
                    .map_or("ok", |value| value.detail.as_str())
            ),
        );
        return Err(restore_error.unwrap_or(launch_error));
    }

    let launch_deadline = Instant::now() + Duration::from_secs(120);
    while !selected_cs2_running(&root) {
        if Instant::now() >= launch_deadline {
            let launch_error = AppError::launch("CS2 did not start Demo playback within 120 seconds");
            let restore_error = restore_demo_layout(&state, &root, previous_mode).err();
            logging::append(
                &state,
                "ERROR",
                "demo.play.failed",
                &format!(
                    "detail={}, restore={}",
                    launch_error.detail,
                    restore_error
                        .as_ref()
                        .map_or("ok", |value| value.detail.as_str())
                ),
            );
            let _ = fs::remove_file(&playback_config);
            return Err(restore_error.unwrap_or(launch_error));
        }
        std::thread::sleep(Duration::from_millis(500));
    }

    monitor_demo_process(state.clone(), root.clone(), previous_mode);
    let playback_result = wait_for_demo_playback(
        &root,
        &argument,
        &playback_config_name,
        launch_deadline,
    );
    let _ = fs::remove_file(&playback_config);
    if let Err(error) = playback_result {
        logging::append(
            &state,
            "ERROR",
            "demo.play.failed",
            &format!(
                "target={}, demo={}, argument={}, detail={}",
                root.display(),
                demo.display(),
                argument,
                error.detail
            ),
        );
        return Err(error);
    }

    logging::append(
        &state,
        "INFO",
        "demo.play.started",
        &format!(
            "target={}, demo={}, argument={}, confirmed=CSGO_GAME_UI_STATE_INGAME",
            root.display(),
            demo.display(),
            argument
        ),
    );
    Ok(())
}

#[tauri::command]
fn get_active_match(csgo: String) -> Result<Option<MatchSession>> {
    let root = csgo_path(&csgo)?;
    let active = match_system::active(&root)?;
    if active.as_ref().is_some_and(|session| {
        matches!(session.state, MatchState::Launching | MatchState::Loading | MatchState::Warmup | MatchState::Live) &&
            !cs2_running() &&
            SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs().saturating_sub(session.created_at_unix) >= 30
    }) {
        let _ = match_system::interrupt_active(&root, "CS2_EXITED", "cs2_exited_before_match_completion", false)?;
        return match_system::active(&root);
    }
    Ok(active)
}

#[tauri::command]
fn list_match_history(csgo: String) -> Result<Vec<MatchSession>> {
    match_system::history(&csgo_path(&csgo)?)
}

#[tauri::command]
fn get_match_result(csgo: String, session_id: String) -> Result<MatchResult> {
    match_system::get_result(&csgo_path(&csgo)?, &session_id)
}

#[tauri::command]
fn delete_match(csgo: String, session_id: String, confirmed: bool) -> Result<()> {
    if !confirmed { return Err(AppError::invalid("Deleting a match requires explicit confirmation")); }
    match_system::delete(&csgo_path(&csgo)?, &session_id)
}

#[tauri::command]
fn run_install_checks(app: AppHandle, csgo: String, selected_map: Option<String>) -> Result<InstallCheckReport> {
    let root = csgo_path(&csgo)?;
    collect_install_checks(&payload_root()?, &local_state_root(&app)?, &root, selected_map.as_deref())
}

fn collect_install_checks(payload: &Path, state: &Path, root: &Path, selected_map: Option<&str>) -> Result<InstallCheckReport> {
    let process = inspect_cs2_process(Some(root));
    let report = install_checks::run(payload, state, root, process.running, selected_map)?;
    let report_path = install_checks::persist(state, &report)?;
    logging::append(
        state,
        if report.can_proceed { "INFO" } else { "ERROR" },
        "install.preflight",
        &format!(
            "target={}, pass={}, warn={}, fail={}, blocking_fail={}, can_proceed={}, report={}",
            report.target,
            report.pass_count,
            report.warn_count,
            report.fail_count,
            report.blocking_fail_count,
            report.can_proceed,
            report_path.display()
        ),
    );
    for check in report.checks.iter().filter(|check| check.status != install_checks::CheckStatus::Pass) {
        logging::append(
            state,
            if check.status == install_checks::CheckStatus::Fail { "ERROR" } else { "WARN" },
            "install.preflight.item",
            &format!(
                "code={}, blocking={}, title={}, evidence={}, cause={}, action={}",
                check.code, check.blocking, check.title, check.evidence, check.cause, check.action
            ),
        );
    }
    Ok(report)
}

fn ensure_install_checks_pass(report: &InstallCheckReport) -> Result<()> {
    if report.can_proceed {
        return Ok(());
    }
    let detail = report.checks.iter()
        .filter(|check| check.blocking && check.status == install_checks::CheckStatus::Fail)
        .map(|check| format!("{} {}: {}", check.code, check.title, check.action))
        .collect::<Vec<_>>()
        .join(" | ");
    Err(AppError::preflight(format!(
        "Installation preflight found {} blocking error(s). {detail}",
        report.blocking_fail_count
    )))
}

fn ensure_match_components_pass(report: &InstallCheckReport) -> Result<()> {
    let required_prefixes = [
        "TARGET_METAMOD_X64",
        "TARGET_CSS_X64",
        "TARGET_CSS_DOTNET_X64",
        "TARGET_RAYTRACE_X64",
        "TARGET_BOTHIDER_X64",
        "TARGET_MATCH_COORDINATOR_MANAGED",
        "TARGET_MATCH_CORE_MANAGED",
        "TARGET_BOTHIDER_API_MANAGED",
        "TARGET_MATCH_CATALOG",
        "TARGET_OPEN_RATING_MODEL",
        "TARGET_BOTHIDER_IDENTITIES",
        "TARGET_MATCH_PROFILE_",
        "MATCH_MAP",
    ];
    let unavailable = report.checks.iter()
        .filter(|check| required_prefixes.iter().any(|prefix| check.code.starts_with(prefix)))
        .filter(|check| check.status != install_checks::CheckStatus::Pass)
        .map(|check| format!("{}: {}", check.code, check.action))
        .collect::<Vec<_>>();
    if unavailable.is_empty() {
        return Ok(());
    }
    Err(AppError::preflight(format!(
        "Match runtime checks failed. {}",
        unavailable.join(" | ")
    )))
}

#[tauri::command]
fn reconcile_core_json(_csgo: String) -> Result<()> {
    Ok(())
}

#[tauri::command]
fn get_bot_items(app: AppHandle, csgo: String) -> Result<BotItemsState> {
    let root = csgo_path(&csgo)?;
    let config = read_config(&app)?;
    Ok(bot_items_at(
        &root,
        &config,
        inspect_cs2_process(Some(&root)).running,
    ))
}

fn bot_items_at(root: &Path, config: &AppConfig, running: bool) -> BotItemsState {
    let b = &config.bot_items;
    BotItemsState {
        skins: b.skins,
        profiles: b.profiles,
        agents: b.agents,
        music: b.music,
        cfg_present: root
            .join("addons/counterstrikesharp/configs/core.json")
            .is_file(),
        cs2_running: running,
    }
}

fn bot_randomizer_options_path(root: &Path) -> PathBuf {
    root.join("addons/counterstrikesharp/plugins/BotRandomizer/bot_randomizer_options.json")
}

fn write_bot_randomizer_options(root: &Path, options: &BotItems) -> Result<()> {
    write_json_atomic(&bot_randomizer_options_path(root), options)
}

#[tauri::command]
fn set_bot_item(app: AppHandle, csgo: String, item: String, on: bool) -> Result<BotItemsState> {
    let root = csgo_path(&csgo)?;
    let mut config = read_config(&app)?;
    match item.as_str() {
        "skins" => config.bot_items.skins = on,
        "profiles" => config.bot_items.profiles = on,
        "agents" => config.bot_items.agents = on,
        "music" => config.bot_items.music = on,
        _ => return Err(AppError::invalid("Unknown bot item")),
    }
    write_bot_randomizer_options(&root, &config.bot_items)?;
    write_config(&app, &config)?;
    get_bot_items(app, csgo)
}

#[tauri::command]
fn get_presets(app: AppHandle, csgo: String) -> Result<PresetsState> {
    let root = csgo_path(&csgo)?;
    let config = read_config(&app)?;
    Ok(presets_at(
        &root,
        &config,
        inspect_cs2_process(Some(&root)).running,
    ))
}

fn presets_at(root: &Path, config: &AppConfig, running: bool) -> PresetsState {
    let aim_plugin = root.join("addons/counterstrikesharp/plugins/BotAimImprover/BotAimImprover.dll");
    let aim_supported = aim_plugin.is_file() || mode_layout::disabled_path(&aim_plugin).is_file();
    let aim_runtime = fs::read(root.join(".csbip/aim-runtime.json"))
        .ok()
        .and_then(|bytes| serde_json::from_slice::<AimRuntimeStatus>(&bytes).ok())
        .filter(|status| status.schema_version == 1);
    PresetsState {
        aim: config.aim.clone(),
        aim_supported,
        aim_active: aim_runtime.as_ref().map(|status| status.active),
        aim_transport: aim_runtime.as_ref().map(|status| status.transport.clone()),
        aim_override_count: aim_runtime.as_ref().map(|status| status.override_count),
        aim_error_count: aim_runtime.as_ref().map(|status| status.error_count),
        nades: config.nades.clone(),
        cfg_present: cfg_files_present(root),
        cs2_running: running,
    }
}

#[tauri::command]
fn set_aim(app: AppHandle, csgo: String, value: String) -> Result<PresetsState> {
    if !["head", "mixed", "body"].contains(&value.as_str()) {
        return Err(AppError::invalid("Unknown aim mode"));
    }
    let root = csgo_path(&csgo)?;
    replace_managed_cfg_command(&root, "bot_aim", &format!("bot_aim {value}"))?;
    let mut config = read_config(&app)?;
    config.aim = Some(value);
    write_config(&app, &config)?;
    get_presets(app, csgo)
}

#[tauri::command]
fn set_nades(app: AppHandle, csgo: String, value: String) -> Result<PresetsState> {
    if !["max", "more", "normal", "off"].contains(&value.as_str()) {
        return Err(AppError::invalid("Unknown nade mode"));
    }
    let root = csgo_path(&csgo)?;
    replace_managed_cfg_command(&root, "bot_nades", &format!("bot_nades {value}"))?;
    let mut config = read_config(&app)?;
    config.nades = Some(value);
    write_config(&app, &config)?;
    get_presets(app, csgo)
}

#[tauri::command]
fn get_drop_knives(app: AppHandle, csgo: String) -> Result<DropKnivesState> {
    let root = csgo_path(&csgo)?;
    let config = read_config(&app)?;
    Ok(drop_knives_at(
        &root,
        &config,
        inspect_cs2_process(Some(&root)).running,
    ))
}

fn drop_knives_at(root: &Path, config: &AppConfig, running: bool) -> DropKnivesState {
    DropKnivesState {
        bind_key: config.drop_knife_bind.clone(),
        selected: config.drop_knife_subclasses.clone(),
        cfg_present: cfg_files_present(root),
        cs2_running: running,
    }
}

#[tauri::command]
fn set_drop_knives(
    app: AppHandle,
    csgo: String,
    bind_key: String,
    selected: Vec<u16>,
) -> Result<DropKnivesState> {
    let root = csgo_path(&csgo)?;
    let commands = selected
        .iter()
        .map(|id| format!("subclass_create {id}"))
        .collect::<Vec<_>>()
        .join(";");
    let line = format!("bind {bind_key} \"{commands}\"");
    replace_managed_cfg_command(&root, "bind ", &line)?;
    let mut config = read_config(&app)?;
    config.drop_knife_bind = bind_key;
    config.drop_knife_subclasses = selected;
    write_config(&app, &config)?;
    get_drop_knives(app, csgo)
}

fn knife_config_path(root: &Path) -> PathBuf {
    root.join("addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/player_knife_presets.json")
}

fn gun_config_path(root: &Path) -> PathBuf {
    root.join("addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/player_gun_presets.json")
}

fn normalize_preset(preset: &mut KnifePreset) {
    preset.seed = preset.seed.clamp(0, 1000);
    preset.wear = preset.wear.clamp(0.0, 1.0);
    preset.stattrak_count = preset.stattrak_count.max(0);
    preset.name_tag = preset.name_tag.chars().take(20).collect();
    if preset.souvenir_enabled {
        preset.stattrak_enabled = false;
    }
}

fn normalize_stickers(defindex: u16, stickers: &mut Vec<StickerPreset>) -> Result<()> {
    if stickers.len() > 5 {
        return Err(AppError::invalid("A weapon cannot have more than five stickers"));
    }
    if !stickers.is_empty() && !valid_sticker_weapon_ids().contains(&defindex) {
        return Err(AppError::invalid("Stickers are not supported for this weapon"));
    }
    let valid_ids = valid_sticker_ids();
    let mut slots = BTreeSet::new();
    for sticker in stickers.iter_mut() {
        if sticker.slot > 4 || !slots.insert(sticker.slot) {
            return Err(AppError::invalid("Sticker slots must be unique and between 0 and 4"));
        }
        if sticker.id == 0 || !valid_ids.contains(&sticker.id) {
            return Err(AppError::invalid("Unknown sticker id"));
        }
        if !sticker.wear.is_finite()
            || !sticker.scale.is_finite()
            || !sticker.rotation.is_finite()
            || !sticker.offset_x.is_finite()
            || !sticker.offset_y.is_finite()
            || !(0.0..=1.0).contains(&sticker.wear)
            || !(0.1..=2.0).contains(&sticker.scale)
            || !(0.0..=360.0).contains(&sticker.rotation)
            || !(-1.0..=1.0).contains(&sticker.offset_x)
            || !(-1.0..=1.0).contains(&sticker.offset_y)
        {
            return Err(AppError::invalid("Sticker values are outside the supported range"));
        }
    }
    stickers.sort_by_key(|sticker| sticker.slot);
    Ok(())
}

fn sanitize_preset_stickers(
    preset: &mut serde_json::Value,
    allow_stickers: bool,
    defindex: Option<u16>,
) -> bool {
    let Some(object) = preset.as_object_mut() else {
        return false;
    };
    let Some(value) = object.get_mut("stickers") else {
        return false;
    };
    if !allow_stickers && value.as_array().is_some_and(Vec::is_empty) {
        return false;
    }
    let valid = allow_stickers
        && defindex.is_some()
        && serde_json::from_value::<Vec<StickerPreset>>(value.clone())
            .ok()
            .is_some_and(|mut stickers| normalize_stickers(defindex.unwrap(), &mut stickers).is_ok());
    if valid {
        return false;
    }
    *value = serde_json::Value::Array(Vec::new());
    true
}

fn sanitize_preset_map(value: Option<&mut serde_json::Value>, allow_stickers: bool) -> bool {
    let Some(map) = value.and_then(serde_json::Value::as_object_mut) else {
        return false;
    };
    map.iter_mut().fold(false, |changed, (defindex, preset)| {
        sanitize_preset_stickers(preset, allow_stickers, defindex.parse().ok()) || changed
    })
}

fn sanitize_knife_config_stickers(value: &mut serde_json::Value) -> bool {
    let Some(root) = value.as_object_mut() else {
        return false;
    };
    let mut changed = sanitize_preset_map(root.get_mut("presets"), false)
        | sanitize_preset_map(root.get_mut("gun_presets"), true);
    let Some(loadouts) = root
        .get_mut("loadouts")
        .and_then(serde_json::Value::as_object_mut)
    else {
        return changed;
    };
    for side in ["ct", "t"] {
        let Some(team) = loadouts
            .get_mut(side)
            .and_then(serde_json::Value::as_object_mut)
        else {
            continue;
        };
        changed |= sanitize_preset_map(team.get_mut("knife_presets"), false);
        changed |= sanitize_preset_map(team.get_mut("gun_presets"), true);
    }
    changed
}

fn sanitize_team_gun_stickers(value: &mut serde_json::Value, schema: Option<u64>) -> bool {
    if matches!(schema, Some(2) | Some(3)) {
        let Some(root) = value.as_object_mut() else {
            return false;
        };
        sanitize_preset_map(root.get_mut("ct"), true)
            | sanitize_preset_map(root.get_mut("t"), true)
    } else {
        sanitize_preset_map(Some(value), true)
    }
}

fn normalize_team_loadout(team: WeaponSide, loadout: &mut TeamLoadout) -> Result<()> {
    for (def, preset) in &mut loadout.knife_presets {
        let defindex: u16 = def
            .parse()
            .map_err(|_| AppError::invalid("Invalid knife defindex"))?;
        if !(500..=526).contains(&defindex) || preset.paint <= 0 {
            return Err(AppError::invalid("Invalid knife preset"));
        }
        if !preset.stickers.is_empty() {
            return Err(AppError::invalid("Knife stickers are not supported"));
        }
        normalize_preset(preset);
    }
    for (def, preset) in &mut loadout.gun_presets {
        let defindex: u16 = def
            .parse()
            .map_err(|_| AppError::invalid("Invalid gun defindex"))?;
        if defindex == 0 || defindex >= 500 || preset.paint <= 0 {
            return Err(AppError::invalid("Invalid gun preset"));
        }
        if (team == WeaponSide::Ct && weapon_side(defindex) == WeaponSide::T)
            || (team == WeaponSide::T && weapon_side(defindex) == WeaponSide::Ct)
        {
            return Err(AppError::invalid(
                "Weapon preset is stored under the wrong team",
            ));
        }
        normalize_preset(preset);
        normalize_stickers(defindex, &mut preset.stickers)?;
    }
    if loadout.default_knife_defindex != 0
        && !loadout
            .knife_presets
            .contains_key(&loadout.default_knife_defindex.to_string())
    {
        return Err(AppError::invalid("Default knife has no saved preset"));
    }
    loadout.glove.seed = loadout.glove.seed.clamp(0, 1000);
    loadout.glove.wear = loadout.glove.wear.clamp(0.0, 1.0);
    if loadout.glove.enabled && loadout.glove.defindex == 0 && loadout.glove.paint == 0 {
        loadout.glove = GlovePreset {
            enabled: true,
            ..GlovePreset::default()
        };
    }
    if loadout.glove.enabled
        && (!(4725..=5035).contains(&loadout.glove.defindex) || loadout.glove.paint <= 0)
    {
        return Err(AppError::invalid("Invalid glove preset"));
    }
    Ok(())
}

fn ensure_shared_link_defaults(config: &mut KnifeCustomizerConfig) {
    for defindex in SHARED_WEAPONS {
        config
            .shared_weapon_links
            .entry(defindex.to_string())
            .or_insert(true);
    }
}

fn apply_legacy_guns(config: &mut KnifeCustomizerConfig, guns: BTreeMap<String, KnifePreset>) {
    config.loadouts.ct.gun_presets.clear();
    config.loadouts.t.gun_presets.clear();
    for (key, preset) in guns {
        let Ok(defindex) = key.parse::<u16>() else {
            continue;
        };
        match weapon_side(defindex) {
            WeaponSide::Ct => {
                config.loadouts.ct.gun_presets.insert(key, preset);
            }
            WeaponSide::T => {
                config.loadouts.t.gun_presets.insert(key, preset);
            }
            WeaponSide::Shared => {
                config
                    .loadouts
                    .ct
                    .gun_presets
                    .insert(key.clone(), preset.clone());
                config.loadouts.t.gun_presets.insert(key.clone(), preset);
                config.shared_weapon_links.insert(key, true);
            }
        }
    }
}

fn migrate_legacy_config(config: &mut KnifeCustomizerConfig) {
    if config.schema_version >= COSMETICS_SCHEMA_VERSION {
        ensure_shared_link_defaults(config);
        return;
    }
    if config.schema_version == 2 {
        config.schema_version = COSMETICS_SCHEMA_VERSION;
        config.stickers_enabled = false;
        ensure_shared_link_defaults(config);
        return;
    }
    let legacy_guns = std::mem::take(&mut config.gun_presets);
    let legacy_loadout = TeamLoadout {
        default_knife_defindex: config.default_knife_defindex,
        knife_presets: std::mem::take(&mut config.presets),
        glove: std::mem::take(&mut config.glove),
        gun_presets: BTreeMap::new(),
    };
    config.loadouts.ct = legacy_loadout.clone();
    config.loadouts.t = legacy_loadout;
    config.schema_version = COSMETICS_SCHEMA_VERSION;
    apply_legacy_guns(config, legacy_guns);
    ensure_shared_link_defaults(config);
}

fn read_knife_config(root: &Path) -> Result<KnifeCustomizerConfig> {
    let path = knife_config_path(root);
    let mut needs_migration = false;
    let mut config = if path.is_file() {
        let text = fs::read_to_string(&path)?;
        let mut value: serde_json::Value = serde_json::from_str(&text)?;
        needs_migration = value
            .get("schema_version")
            .and_then(serde_json::Value::as_u64)
            != Some(COSMETICS_SCHEMA_VERSION as u64);
        needs_migration |= sanitize_knife_config_stickers(&mut value);
        serde_json::from_value(value)?
    } else {
        KnifeCustomizerConfig::default()
    };
    migrate_legacy_config(&mut config);

    let gun_path = gun_config_path(root);
    if gun_path.is_file() {
        let text = fs::read_to_string(&gun_path)?;
        let mut value: serde_json::Value = serde_json::from_str(&text)?;
        let gun_schema = value
            .get("schema_version")
            .and_then(serde_json::Value::as_u64);
        needs_migration |= sanitize_team_gun_stickers(&mut value, gun_schema);
        if matches!(gun_schema, Some(2) | Some(3)) {
            let guns: TeamGunConfig = serde_json::from_value(value)?;
            config.loadouts.ct.gun_presets = guns.ct;
            config.loadouts.t.gun_presets = guns.t;
            config.shared_weapon_links = guns.shared_weapon_links;
            needs_migration |= gun_schema != Some(COSMETICS_SCHEMA_VERSION as u64);
        } else {
            apply_legacy_guns(&mut config, serde_json::from_value(value)?);
            needs_migration = true;
        }
    }
    ensure_shared_link_defaults(&mut config);
    if needs_migration {
        save_knife_config(root, &mut config)?;
    }
    Ok(config)
}

fn normalize_knife_config(config: &mut KnifeCustomizerConfig) -> Result<()> {
    migrate_legacy_config(config);
    config.schema_version = COSMETICS_SCHEMA_VERSION;
    config.stickers_enabled = STICKER_RELEASE_ENABLED && config.stickers_enabled;
    config.music_kit_id = config.music_kit_id.clamp(0, u16::MAX as i32);
    normalize_team_loadout(WeaponSide::Ct, &mut config.loadouts.ct)?;
    normalize_team_loadout(WeaponSide::T, &mut config.loadouts.t)?;
    ensure_shared_link_defaults(config);

    for (key, linked) in &config.shared_weapon_links {
        let defindex: u16 = key
            .parse()
            .map_err(|_| AppError::invalid("Invalid shared weapon defindex"))?;
        if defindex == 0 || defindex >= 500 || weapon_side(defindex) != WeaponSide::Shared {
            return Err(AppError::invalid("Invalid shared weapon link"));
        }
        if !linked {
            continue;
        }
        let ct = config.loadouts.ct.gun_presets.get(key);
        let t = config.loadouts.t.gun_presets.get(key);
        match (ct, t) {
            (Some(left), Some(right)) if left != right => {
                return Err(AppError::invalid("Linked CT/T weapon presets must match"));
            }
            (Some(preset), None) => {
                config
                    .loadouts
                    .t
                    .gun_presets
                    .insert(key.clone(), preset.clone());
            }
            (None, Some(preset)) => {
                config
                    .loadouts
                    .ct
                    .gun_presets
                    .insert(key.clone(), preset.clone());
            }
            _ => {}
        }
    }
    Ok(())
}

fn versioned_backup_path(path: &Path, schema_version: u64) -> PathBuf {
    let version = if schema_version <= 1 { 1 } else { schema_version };
    PathBuf::from(format!("{}.v{version}.bak", path.to_string_lossy()))
}

#[cfg(test)]
fn legacy_backup_path(path: &Path) -> PathBuf {
    versioned_backup_path(path, 1)
}

fn backup_legacy_file(path: &Path) -> Result<()> {
    if !path.is_file() {
        return Ok(());
    }
    let value: serde_json::Value = serde_json::from_str(&fs::read_to_string(path)?)?;
    let schema_version = value
        .get("schema_version")
        .and_then(serde_json::Value::as_u64)
        .unwrap_or(1);
    if schema_version == COSMETICS_SCHEMA_VERSION as u64 {
        return Ok(());
    }
    let backup = versioned_backup_path(path, schema_version);
    if !backup.exists() {
        fs::copy(path, backup)?;
    }
    Ok(())
}

fn save_knife_config(root: &Path, config: &mut KnifeCustomizerConfig) -> Result<()> {
    normalize_knife_config(config)?;
    let knife_path = knife_config_path(root);
    let gun_path = gun_config_path(root);
    backup_legacy_file(&knife_path)?;
    backup_legacy_file(&gun_path)?;
    write_json_atomic(&knife_path, config)?;
    let guns = TeamGunConfig {
        schema_version: COSMETICS_SCHEMA_VERSION,
        ct: config.loadouts.ct.gun_presets.clone(),
        t: config.loadouts.t.gun_presets.clone(),
        shared_weapon_links: config.shared_weapon_links.clone(),
    };
    write_json_atomic(&gun_path, &guns)
}

fn sticker_configuration(config: &KnifeCustomizerConfig) -> BTreeMap<String, Vec<StickerPreset>> {
    let mut result = BTreeMap::new();
    for (side, loadout) in [("ct", &config.loadouts.ct), ("t", &config.loadouts.t)] {
        for (kind, presets) in [("knife", &loadout.knife_presets), ("gun", &loadout.gun_presets)] {
            for (defindex, preset) in presets {
                if !preset.stickers.is_empty() {
                    result.insert(format!("{side}:{kind}:{defindex}"), preset.stickers.clone());
                }
            }
        }
    }
    result
}

fn sticker_configuration_changed(left: &KnifeCustomizerConfig, right: &KnifeCustomizerConfig) -> bool {
    left.stickers_enabled != right.stickers_enabled
        || sticker_configuration(left) != sticker_configuration(right)
}

fn explicit_json_path(value: &str, operation: &str) -> Result<PathBuf> {
    let path = PathBuf::from(value);
    if path.extension().and_then(|value| value.to_str()).is_none_or(|value| !value.eq_ignore_ascii_case("json")) {
        return Err(AppError::invalid(format!("{operation} path must use the .json extension")));
    }
    if path.file_name().is_none() {
        return Err(AppError::invalid(format!("{operation} path has no file name")));
    }
    Ok(path)
}

fn backup_cosmetics_before_import(root: &Path) -> Result<Option<PathBuf>> {
    let sources = [knife_config_path(root), gun_config_path(root)];
    if !sources.iter().any(|path| path.is_file()) {
        return Ok(None);
    }
    let stamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_nanos();
    let directory = root.join(".csbip").join("cosmetics-backups").join(format!("pre-import-{stamp}"));
    fs::create_dir_all(&directory)?;
    for source in sources.into_iter().filter(|path| path.is_file()) {
        let destination = directory.join(source.file_name().unwrap_or_default());
        fs::copy(source, destination)?;
    }
    Ok(Some(directory))
}

fn export_cosmetics_preset_at(root: &Path, destination: &Path) -> Result<CosmeticsPresetExportResult> {
    let parent = destination.parent().ok_or_else(|| AppError::invalid("Export path has no parent directory"))?;
    if !parent.is_dir() {
        return Err(AppError::invalid("Export destination directory does not exist"));
    }
    let mut config = read_knife_config(&root)?;
    normalize_knife_config(&mut config)?;
    let bundle = CosmeticsPresetBundle {
        schema_version: COSMETICS_EXPORT_SCHEMA_VERSION,
        kind: COSMETICS_EXPORT_KIND.into(),
        exported_at_unix: SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs(),
        config,
    };
    let bytes = serde_json::to_vec_pretty(&bundle)?;
    atomic_fs::write_replace(&destination, &bytes)?;
    Ok(CosmeticsPresetExportResult {
        path: destination.to_string_lossy().into_owned(),
        size_bytes: bytes.len() as u64,
    })
}

fn read_cosmetics_preset(source: &Path) -> Result<KnifeCustomizerConfig> {
    let metadata = fs::metadata(&source)?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > MAX_COSMETICS_IMPORT_BYTES {
        return Err(AppError::invalid("Cosmetics preset must be a non-empty JSON file no larger than 4 MiB"));
    }
    let mut bundle: CosmeticsPresetBundle = serde_json::from_slice(&fs::read(&source)?)?;
    if !matches!(bundle.schema_version, LEGACY_COSMETICS_EXPORT_SCHEMA_VERSION | COSMETICS_EXPORT_SCHEMA_VERSION)
        || bundle.kind != COSMETICS_EXPORT_KIND
    {
        return Err(AppError::invalid("Unsupported cosmetics preset schema or file type"));
    }
    normalize_knife_config(&mut bundle.config)?;
    Ok(bundle.config)
}

fn snapshot_cosmetics_files(root: &Path) -> Result<Vec<(PathBuf, Option<Vec<u8>>)>> {
    [knife_config_path(root), gun_config_path(root)]
        .into_iter()
        .map(|path| {
            let bytes = if path.is_file() { Some(fs::read(&path)?) } else { None };
            Ok((path, bytes))
        })
        .collect()
}

fn restore_cosmetics_files(snapshot: &[(PathBuf, Option<Vec<u8>>)]) -> Result<()> {
    for (path, bytes) in snapshot {
        match bytes {
            Some(bytes) => atomic_fs::write_replace(path, bytes).map_err(AppError::transaction_io)?,
            None if path.exists() => fs::remove_file(path).map_err(AppError::transaction_io)?,
            None => {}
        }
    }
    Ok(())
}

fn import_cosmetics_preset_at<F>(root: &Path, source: &Path, after_write: F) -> Result<Option<PathBuf>>
where
    F: FnOnce() -> Result<()>,
{
    let mut config = read_cosmetics_preset(source)?;
    let snapshot = snapshot_cosmetics_files(root)?;
    let backup = backup_cosmetics_before_import(&root)?;
    if let Err(error) = save_knife_config(root, &mut config).and_then(|_| after_write()) {
        if let Err(rollback) = restore_cosmetics_files(&snapshot) {
            return Err(AppError::transaction(format!(
                "Cosmetics import failed ({}) and rollback failed ({})",
                error.detail, rollback.detail
            )));
        }
        return Err(error);
    }
    Ok(backup)
}

#[tauri::command]
fn export_cosmetics_preset(csgo: String, destination: String) -> Result<CosmeticsPresetExportResult> {
    let root = csgo_path(&csgo)?;
    let destination = explicit_json_path(&destination, "Export")?;
    export_cosmetics_preset_at(&root, &destination)
}

#[tauri::command]
fn import_cosmetics_preset(csgo: String, source: String) -> Result<CosmeticsPresetImportResult> {
    let root = csgo_path(&csgo)?;
    let source = explicit_json_path(&source, "Import")?;
    let backup = import_cosmetics_preset_at(&root, &source, || {
        app_storage::mirror_cosmetics(&root)?;
        Ok(())
    })?;
    Ok(CosmeticsPresetImportResult {
        state: get_knife_customizer(csgo)?,
        backup_path: backup.map(|path| path.to_string_lossy().into_owned()),
    })
}

fn set_knife_customizer_enabled(root: &Path, enabled: bool) -> Result<Option<bool>> {
    let path = knife_config_path(root);
    if path.is_file() {
        let mut config = read_knife_config(root)?;
        let previous = config.enabled;
        if previous != enabled {
            config.enabled = enabled;
            save_knife_config(root, &mut config)?;
        }
        return Ok(Some(previous));
    }
    Ok(None)
}

fn enter_online_safety(root: &Path, app_config: &mut AppConfig) -> Result<()> {
    let previous = set_knife_customizer_enabled(root, false)?;
    if app_config.cosmetics_enabled_before_online.is_none() {
        app_config.cosmetics_enabled_before_online = previous;
    }
    Ok(())
}

fn leave_online_safety(root: &Path, app_config: &mut AppConfig) -> Result<()> {
    if let Some(previous) = app_config.cosmetics_enabled_before_online.take() {
        set_knife_customizer_enabled(root, previous)?;
    }
    Ok(())
}

fn enter_preview_safety(root: &Path, app_config: &mut AppConfig) -> Result<()> {
    let previous = set_knife_customizer_enabled(root, true)?;
    if app_config.cosmetics_enabled_before_preview.is_none() {
        app_config.cosmetics_enabled_before_preview = previous;
    }
    Ok(())
}

fn leave_preview_safety(root: &Path, app_config: &mut AppConfig) -> Result<()> {
    if let Some(previous) = app_config.cosmetics_enabled_before_preview.take() {
        set_knife_customizer_enabled(root, previous)?;
    }
    Ok(())
}

fn enforce_mode_cosmetics(root: &Path, app_config: &mut AppConfig, mode: LaunchMode) -> Result<()> {
    match mode {
        LaunchMode::Online => {
            leave_preview_safety(root, app_config)?;
            enter_online_safety(root, app_config)
        }
        LaunchMode::Preview => {
            leave_online_safety(root, app_config)?;
            enter_preview_safety(root, app_config)
        }
        LaunchMode::Bots => {
            leave_online_safety(root, app_config)?;
            leave_preview_safety(root, app_config)
        }
    }
}

#[tauri::command]
fn get_knife_customizer(csgo: String) -> Result<KnifeCustomizerState> {
    let root = csgo_path(&csgo)?;
    let path = knife_config_path(&root);
    let present = path.is_file();
    let config = read_knife_config(&root)?;
    if present {
        app_storage::mirror_cosmetics(&root)?;
    }
    Ok(KnifeCustomizerState {
        plugin_present: path.with_file_name("PlayerKnifeCustomizer.dll").is_file(),
        config_present: present,
        cs2_running: cs2_running(),
        config,
    })
}

#[tauri::command]
fn save_knife_customizer(
    csgo: String,
    mut config: KnifeCustomizerConfig,
) -> Result<KnifeCustomizerState> {
    let root = csgo_path(&csgo)?;
    if blocks_target_write(&inspect_cs2_process(Some(&root))) {
        let current = read_knife_config(&root)?;
        if sticker_configuration_changed(&current, &config) {
            return Err(AppError::process(
                "Close CS2 before changing or saving sticker configuration",
            ));
        }
    }
    save_knife_config(&root, &mut config)?;
    app_storage::mirror_cosmetics(&root)?;
    get_knife_customizer(csgo)
}

#[tauri::command]
async fn get_runtime_snapshot(app: AppHandle) -> Result<RuntimeSnapshot> {
    run_installation_task("Runtime snapshot", move || get_runtime_snapshot_impl(app)).await
}

fn get_runtime_snapshot_impl(app: AppHandle) -> Result<RuntimeSnapshot> {
    let directory = detect_directories(app.clone())?;
    let config = read_config(&app)?;
    let Some(selected) = directory.selected.clone() else {
        return Ok(RuntimeSnapshot {
            directory,
            process: inspect_cs2_process(None),
            files: None,
            difficulty: None,
            mode: None,
            bot_items: None,
            presets: None,
            drop_knives: None,
            installation: None,
        });
    };

    let root = csgo_path(&selected)?;
    let process = inspect_cs2_process(Some(&root));
    let running = process.running;
    let payload = payload_root().ok();
    let state = local_state_root(&app).ok();
    if let Some(state) = &state {
        let _ = installer::recover_incomplete(state, &root);
        if !running {
            let _ = mode_layout::recover(state, &root);
        }
    }
    let installation = payload
        .as_deref()
        .zip(state.as_deref())
        .and_then(|(payload, state)| installer::inspect_quick(payload, state, &root).ok());

    Ok(RuntimeSnapshot {
        files: Some(validate_files_at(Some(&app), &root, false)?),
        difficulty: Some(difficulty_at(&root, running)),
        mode: Some(mode_at(&root, &config, running)),
        bot_items: Some(bot_items_at(&root, &config, running)),
        presets: Some(presets_at(&root, &config, running)),
        drop_knives: Some(drop_knives_at(&root, &config, running)),
        directory,
        process,
        installation,
    })
}

async fn run_installation_task<T, F>(label: &'static str, task: F) -> Result<T>
where
    T: Send + 'static,
    F: FnOnce() -> Result<T> + Send + 'static,
{
    tauri::async_runtime::spawn_blocking(task)
        .await
        .map_err(|error| {
            AppError::new("E1003", "internal", format!("{label} task failed: {error}"))
        })?
}

#[tauri::command]
async fn inspect_installation(app: AppHandle, csgo: String) -> Result<InstallationInspection> {
    run_installation_task("Installation inspection", move || {
        let root = csgo_path(&csgo)?;
        installer::recover_incomplete(&local_state_root(&app)?, &root)?;
        installer::inspect(&payload_root()?, &local_state_root(&app)?, &root)
    })
    .await
}

#[tauri::command]
async fn get_install_plan(app: AppHandle, csgo: String) -> Result<InstallPlan> {
    run_installation_task("Install preflight", move || {
        let _busy = online_update::OperationGuard::acquire()?;
        let root = csgo_path(&csgo)?;
        ensure_target_not_running(&root)?;
        installer::plan(&payload_root()?, &local_state_root(&app)?, &root)
    })
    .await
}

#[tauri::command]
async fn install_payload(app: AppHandle, csgo: String) -> Result<InstallTransactionResult> {
    run_installation_task("Payload installation", move || {
        let _busy = online_update::OperationGuard::acquire()?;
        let root = csgo_path(&csgo)?;
        let payload = payload_root()?;
        let state = local_state_root(&app)?;
        let report = collect_install_checks(&payload, &state, &root, None)?;
        ensure_install_checks_pass(&report)?;
        ensure_target_not_running(&root)?;
        ensure_steam_app_idle(&root)?;
        let config = read_config(&app)?;
        let restore_preview = config.mode.as_deref() == Some("preview");
        logging::append(&state, "INFO", "install.started", &root.to_string_lossy());
        let result = with_canonical_layout(&state, &root, restore_preview, || {
            let result = installer::install(&payload, &state, &root, false)?;
            write_bot_randomizer_options(&root, &config.bot_items)?;
            Ok(result)
        });
        match &result {
            Ok(value) => logging::append(
                &state,
                "INFO",
                "install.completed",
                &format!("{} files", value.installed_files),
            ),
            Err(error) => logging::append(&state, "ERROR", "install.failed", &error.detail),
        }
        result
    })
    .await
}

#[tauri::command]
async fn repair_payload(app: AppHandle, csgo: String) -> Result<InstallTransactionResult> {
    run_installation_task("Payload repair", move || {
        let _busy = online_update::OperationGuard::acquire()?;
        let root = csgo_path(&csgo)?;
        let payload = payload_root()?;
        let state = local_state_root(&app)?;
        let report = collect_install_checks(&payload, &state, &root, None)?;
        ensure_install_checks_pass(&report)?;
        ensure_target_not_running(&root)?;
        ensure_steam_app_idle(&root)?;
        let config = read_config(&app)?;
        let restore_preview = config.mode.as_deref() == Some("preview");
        logging::append(&state, "INFO", "repair.started", &root.to_string_lossy());
        let result = with_canonical_layout(&state, &root, restore_preview, || {
            let result = installer::install(&payload, &state, &root, true)?;
            write_bot_randomizer_options(&root, &config.bot_items)?;
            Ok(result)
        });
        match &result {
            Ok(value) => logging::append(
                &state,
                "INFO",
                "repair.completed",
                &format!("{} files", value.installed_files),
            ),
            Err(error) => logging::append(&state, "ERROR", "repair.failed", &error.detail),
        }
        result
    })
    .await
}

#[tauri::command]
async fn restore_payload(app: AppHandle, csgo: String) -> Result<RestoreResult> {
    run_installation_task("Payload restore", move || {
        restore_payload_impl(&app, &csgo, false)
    })
    .await
}

#[tauri::command]
async fn restore_pristine_cs2(app: AppHandle, csgo: String) -> Result<RestoreResult> {
    run_installation_task("Pristine CS2 restore", move || {
        restore_payload_impl(&app, &csgo, true)
    })
    .await
}

fn restore_payload_impl(app: &AppHandle, csgo: &str, pristine: bool) -> Result<RestoreResult> {
    let _busy = online_update::OperationGuard::acquire()?;
    let root = csgo_path(csgo)?;
    ensure_target_not_running(&root)?;
    ensure_steam_app_idle(&root)?;
    let state = local_state_root(app)?;
    mode_layout::recover(&state, &root)?;
    mode_layout::set_preview(&state, &root, false)?;
    let mut config = read_config(app)?;
    apply_launch_mode(&root, LaunchMode::Online).map_err(AppError::launch)?;
    enforce_mode_cosmetics(&root, &mut config, LaunchMode::Online)?;
    config.mode = Some("online".into());
    config.insecure = false;
    write_bot_randomizer_options(&root, &config.bot_items)?;
    write_config(app, &config)?;
    let operation = if pristine {
        "restore_pristine"
    } else {
        "restore"
    };
    logging::append(
        &state,
        "INFO",
        &format!("{operation}.started"),
        &root.to_string_lossy(),
    );
    let result = if pristine {
        installer::restore_pristine(&payload_root()?, &state, &root)
    } else {
        installer::restore(&payload_root()?, &state, &root)
    };
    match &result {
        Ok(value) => logging::append(
            &state,
            "INFO",
            &format!("{operation}.completed"),
            &format!(
                "restored={}, removed={}, preserved={}",
                value.restored_files, value.removed_files, value.preserved_files
            ),
        ),
        Err(error) => logging::append(
            &state,
            "ERROR",
            &format!("{operation}.failed"),
            &error.detail,
        ),
    }
    result
}

fn installed_plugin_version(app: &AppHandle) -> Option<String> {
    let config = read_config(app).ok()?;
    let root = csgo_path(config.csgo_path.as_deref()?).ok()?;
    installer::inspect_quick(&payload_root().ok()?, &local_state_root(app).ok()?, &root)
        .ok()?
        .package_version
}

#[tauri::command]
fn get_update_snapshot(app: AppHandle) -> Result<online_update::OnlineUpdateSnapshot> {
    online_update::snapshot(installed_plugin_version(&app).as_deref())
}

#[tauri::command]
async fn check_online_updates(
    app: AppHandle,
    force: bool,
) -> Result<online_update::OnlineUpdateSnapshot> {
    tauri::async_runtime::spawn_blocking(move || {
        let plugin_version = installed_plugin_version(&app);
        let result = online_update::check(force, plugin_version.as_deref());
        if let Err(error) = &result {
            online_update::record_check_error(error);
        }
        result
    })
    .await
    .map_err(|error| AppError::update(format!("Update check task failed: {error}")))?
}

fn install_plugin_update_impl(app: &AppHandle, csgo: &str) -> Result<online_update::UpdateResult> {
    let root = csgo_path(csgo)?;
    ensure_target_not_running(&root)?;
    ensure_steam_app_idle(&root)?;
    let state = local_state_root(app)?;
    let config = read_config(app)?;
    let restore_preview = config.mode.as_deref() == Some("preview");
    logging::append(&state, "INFO", "update.plugin_started", "host=github.com");
    let (version, payload) = online_update::prepare_plugin(app)?;
    online_update::activate_payload(&version, &payload)?;
    match with_canonical_layout(&state, &root, restore_preview, || {
        let result = installer::install(&payload, &state, &root, false)?;
        write_bot_randomizer_options(&root, &config.bot_items)?;
        Ok(result)
    }) {
        Ok(value) => {
            logging::append(
                &state,
                "INFO",
                "update.plugin_completed",
                &format!("version={version}, files={}", value.installed_files),
            );
            Ok(online_update::UpdateResult {
                component: "plugin".into(),
                version,
                installed: true,
                restart_required: false,
                rollback_succeeded: None,
                detail: format!("Plugin update installed ({} files)", value.installed_files),
            })
        }
        Err(error) => {
            logging::append(
                &state,
                "ERROR",
                "update.plugin_failed",
                &format!("stage=install, rollback=attempted, {}", error.detail),
            );
            Err(error)
        }
    }
}

#[tauri::command]
async fn install_plugin_update(
    app: AppHandle,
    csgo: String,
) -> Result<online_update::UpdateResult> {
    tauri::async_runtime::spawn_blocking(move || {
        let _busy = online_update::OperationGuard::acquire()?;
        install_plugin_update_impl(&app, &csgo)
    })
    .await
    .map_err(|error| AppError::update(format!("Plugin update task failed: {error}")))?
}

#[tauri::command]
async fn install_panel_update(app: AppHandle) -> Result<online_update::UpdateResult> {
    let worker_app = app.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        let _busy = online_update::OperationGuard::acquire()?;
        online_update::prepare_panel(&worker_app)
    })
    .await
    .map_err(|error| AppError::update(format!("Panel update task failed: {error}")))??;
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_millis(500));
        app.exit(0);
    });
    Ok(result)
}

#[tauri::command]
async fn install_all_updates(
    app: AppHandle,
    csgo: Option<String>,
) -> Result<online_update::UpdateBatchResult> {
    let worker_app = app.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        let _busy = online_update::OperationGuard::acquire()?;
        let plugin_version = installed_plugin_version(&worker_app);
        let snapshot = online_update::snapshot(plugin_version.as_deref())?;

        let plugin = if snapshot.plugin.update_available {
            if !snapshot.plugin.compatible {
                return Err(AppError::update(
                    "This plugin update requires a newer Panel updater",
                ));
            }
            let target = csgo.as_deref().ok_or_else(|| {
                AppError::directory("Select the CS2 game/csgo directory before updating the plugin")
            })?;
            Some(install_plugin_update_impl(&worker_app, target)?)
        } else {
            None
        };

        let panel = if snapshot.panel.update_available {
            if !snapshot.panel.compatible {
                return Err(AppError::update(
                    "This Panel update requires a newer updater baseline",
                ));
            }
            Some(online_update::prepare_panel(&worker_app)?)
        } else {
            None
        };

        Ok(online_update::UpdateBatchResult {
            restart_required: panel.is_some(),
            panel,
            plugin,
        })
    })
    .await
    .map_err(|error| AppError::update(format!("Combined update task failed: {error}")))??;

    if result.restart_required {
        std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_millis(500));
            app.exit(0);
        });
    }
    Ok(result)
}

#[tauri::command]
fn cancel_update() {
    online_update::cancel();
}

fn with_canonical_layout<T>(
    state: &Path,
    root: &Path,
    restore_preview: bool,
    operation: impl FnOnce() -> Result<T>,
) -> Result<T> {
    mode_layout::recover(state, root)?;
    if mode_layout::is_preview(state, root) {
        mode_layout::set_preview(state, root, false)?;
    }
    let result = operation();
    let restore = if restore_preview {
        mode_layout::set_preview(state, root, true)
    } else {
        Ok(())
    };
    match (result, restore) {
        (Ok(value), Ok(())) => Ok(value),
        (Err(error), Ok(())) => Err(error),
        (_, Err(error)) => Err(error),
    }
}

#[tauri::command]
async fn export_diagnostics(
    app: AppHandle,
    csgo: Option<String>,
) -> Result<diagnostics::DiagnosticArchive> {
    run_installation_task("Diagnostic export", move || {
        export_diagnostics_impl(app, csgo)
    })
    .await
}

fn export_diagnostics_impl(
    app: AppHandle,
    csgo: Option<String>,
) -> Result<diagnostics::DiagnosticArchive> {
    let root = csgo.as_deref().map(csgo_path).transpose()?;
    let snapshot = get_runtime_snapshot_impl(app.clone())?;
    let state = local_state_root(&app)?;
    let source = root
        .as_deref()
        .map(|path| path.to_string_lossy().into_owned())
        .unwrap_or_else(|| "panel-only".to_string());
    logging::append(&state, "INFO", "diagnostics.started", &source);
    let snapshot = serde_json::to_value(snapshot)?;
    let result = diagnostics::export(&state, root.as_deref(), &snapshot);
    match &result {
        Ok(value) => logging::append(
            &state,
            "INFO",
            "diagnostics.completed",
            &format!("{} files: {}", value.files_collected, value.path),
        ),
        Err(error) => logging::append(&state, "ERROR", "diagnostics.failed", &error.detail),
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn test_root() -> PathBuf {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!("cs2bi-knife-config-{suffix}"))
    }

    fn test_preset(paint: i32) -> KnifePreset {
        KnifePreset {
            paint,
            seed: 0,
            wear: 0.01,
            name_tag: String::new(),
            stattrak_enabled: false,
            stattrak_count: 0,
            souvenir_enabled: false,
            stickers: vec![],
        }
    }

    fn test_sticker(slot: u8, id: u32) -> StickerPreset {
        StickerPreset {
            slot,
            id,
            wear: 0.0,
            scale: 1.0,
            rotation: 0.0,
            offset_x: 0.0,
            offset_y: 0.0,
            custom_position: false,
        }
    }

    #[test]
    fn csgo_path_accepts_install_root_game_directory_and_csgo_directory() {
        let root = test_root();
        let game = root.join("game");
        let csgo = game.join("csgo");
        fs::create_dir_all(csgo.join("cfg")).unwrap();
        fs::write(csgo.join("gameinfo.gi"), b"gameinfo").unwrap();

        let expected = normalize_windows_canonical(fs::canonicalize(&csgo).unwrap());
        assert_eq!(csgo_path(root.to_str().unwrap()).unwrap(), expected);
        assert_eq!(csgo_path(game.to_str().unwrap()).unwrap(), expected);
        assert_eq!(csgo_path(csgo.to_str().unwrap()).unwrap(), expected);

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn managed_demo_location_accepts_missing_demo_and_rejects_escape() {
        let root = test_root();
        let managed = root.join("demos/csbip");
        let outside = root.join("demos/other");
        fs::create_dir_all(&managed).unwrap();
        fs::create_dir_all(&outside).unwrap();

        let demo = managed.join("session.dem");
        let (resolved, selected) =
            managed_demo_location(&root, demo.to_str().unwrap()).unwrap();
        assert_eq!(resolved, fs::canonicalize(&managed).unwrap());
        assert_eq!(selected, demo);
        assert!(managed_demo_location(
            &root,
            outside.join("session.dem").to_str().unwrap()
        )
        .is_err());

        fs::remove_dir_all(root).unwrap();
    }


    #[test]
    fn demo_playback_uses_a_csgo_relative_forward_slash_path() {
        let root = PathBuf::from(r"C:\Games\Counter-Strike Global Offensive\game\csgo");
        let demo = root.join("demos").join("csbip").join("session.dem");
        assert_eq!(demo_playback_argument(&root, &demo), "demos/csbip/session.dem");
    }

    #[test]
    fn demo_playback_removes_the_windows_verbatim_prefix_before_relativizing() {
        let root = PathBuf::from(r"C:\Games\Counter-Strike Global Offensive\game\csgo");
        let demo = PathBuf::from(
            r"\\?\C:\Games\Counter-Strike Global Offensive\game\csgo\demos\csbip\session.dem",
        );
        assert_eq!(demo_playback_argument(&root, &demo), "demos/csbip/session.dem");
    }

    #[test]
    fn demo_playback_uses_a_post_start_config_and_skips_the_region_picker() {
        assert_eq!(DEMO_LAUNCH_ARGUMENTS[2], "-worldwide");
        assert_eq!(DEMO_LAUNCH_ARGUMENTS.last(), Some(&"+exec"));
        assert!(!DEMO_LAUNCH_ARGUMENTS.contains(&"+playdemo"));
        let config = demo_playback_config("demos/csbip/session.dem").unwrap();
        assert_eq!(
            config,
            "playdemo \"demos/csbip/session.dem\"\n"
        );
    }

    #[test]
    fn match_launch_skips_the_region_picker_and_preserves_map_and_demo_settings() {
        let arguments = match_launch_arguments(true, "de_mirage");
        assert!(arguments.iter().any(|value| value == "-worldwide"));
        assert!(arguments.windows(2).any(|values| values == ["+tv_enable", "1"]));
        assert!(arguments.windows(2).any(|values| values == ["+map", "de_mirage"]));
    }

    #[test]
    fn demo_playback_config_rejects_console_command_injection() {
        assert!(demo_playback_config("demos/csbip/session.dem\nquit").is_err());
        assert!(demo_playback_config("demos/csbip/\"session.dem").is_err());
    }

    #[test]
    fn demo_playback_requires_the_current_request_and_ingame_confirmation() {
        let argument = "demos/csbip/session.dem";
        let config_name = "csbip_play_demo_current.cfg";
        let requested = format!(
            "execing csbip_play_demo_old.cfg\nPlaying Demo ({argument})\nCSGO_GAME_UI_STATE_INGAME\n"
        );
        assert!(!demo_playback_confirmed(&requested, argument, config_name));
        let loading = format!("execing {config_name}\nPlaying Demo ({argument})\n");
        assert!(!demo_playback_confirmed(&loading, argument, config_name));
        let ingame = format!("{loading}CSGO_GAME_UI_STATE_INGAME\n");
        assert!(demo_playback_confirmed(&ingame, argument, config_name));
    }

    #[test]
    fn knife_config_is_clamped_and_written_to_game_plugin_path() {
        let root = test_root();
        let mut config = KnifeCustomizerConfig::default();
        config.enabled = true;
        config.loadouts.ct.default_knife_defindex = 515;
        config.loadouts.ct.knife_presets.insert(
            "515".into(),
            KnifePreset {
                paint: 568,
                seed: 1200,
                wear: -0.25,
                name_tag: "12345678901234567890extra".into(),
                stattrak_enabled: true,
                stattrak_count: -7,
                souvenir_enabled: false,
                stickers: vec![],
            },
        );

        save_knife_config(&root, &mut config).unwrap();

        let saved = read_knife_config(&root).unwrap();
        let preset = &saved.loadouts.ct.knife_presets["515"];
        assert_eq!(saved.schema_version, COSMETICS_SCHEMA_VERSION);
        assert_eq!(saved.loadouts.ct.default_knife_defindex, 515);
        assert_eq!(preset.seed, 1000);
        assert_eq!(preset.wear, 0.0);
        assert_eq!(preset.name_tag, "12345678901234567890");
        assert_eq!(preset.stattrak_count, 0);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn default_knife_requires_a_matching_preset() {
        let mut config = KnifeCustomizerConfig::default();
        config.loadouts.ct.default_knife_defindex = 515;
        let error = normalize_knife_config(&mut config).unwrap_err();
        assert_eq!(error.code, "E1002");
        assert!(error.detail.contains("no saved preset"));
    }

    #[test]
    fn legacy_knife_config_without_glove_remains_readable() {
        let json = r#"{
            "enabled": true,
            "apply_to_human_players": true,
            "apply_to_dropped_knives": true,
            "apply_on_pickup": true,
            "default_knife_defindex": 0,
            "presets": {}
        }"#;
        let mut config: KnifeCustomizerConfig = serde_json::from_str(json).unwrap();
        migrate_legacy_config(&mut config);
        assert!(
            !serde_json::to_string(&config)
                .unwrap()
                .contains("apply_to_dropped_knives")
        );
        assert_eq!(config.schema_version, COSMETICS_SCHEMA_VERSION);
        assert!(!config.loadouts.ct.glove.enabled);
        assert_eq!(config.loadouts.t.glove.defindex, DEFAULT_GLOVE_DEFINDEX);
        assert_eq!(config.loadouts.t.glove.paint, DEFAULT_GLOVE_PAINT);
        assert_eq!(config.music_kit_id, 0);
    }

    #[test]
    fn enabling_an_empty_legacy_glove_uses_the_default_preset() {
        let mut config = KnifeCustomizerConfig::default();
        config.loadouts.t.glove = GlovePreset {
            enabled: true,
            defindex: 0,
            paint: 0,
            seed: 0,
            wear: 0.0,
        };

        normalize_knife_config(&mut config).unwrap();

        assert!(config.loadouts.t.glove.enabled);
        assert_eq!(config.loadouts.t.glove.defindex, DEFAULT_GLOVE_DEFINDEX);
        assert_eq!(config.loadouts.t.glove.paint, DEFAULT_GLOVE_PAINT);
        assert_eq!(config.loadouts.t.glove.wear, DEFAULT_GLOVE_WEAR);
    }

    #[test]
    fn atomic_json_write_replaces_an_existing_file() {
        let root = test_root();
        let path = root.join("config.json");
        write_json_atomic(&path, &serde_json::json!({ "value": 1 })).unwrap();
        write_json_atomic(&path, &serde_json::json!({ "value": 2 })).unwrap();

        let saved: serde_json::Value =
            serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
        assert_eq!(saved["value"], 2);
        assert!(!path.with_extension("json.tmp").exists());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn cosmetics_import_rejects_an_unsupported_bundle_before_writing() {
        let root = test_root();
        fs::create_dir_all(&root).unwrap();
        let source = root.join("unsupported.json");
        fs::write(
            &source,
            serde_json::to_vec(&serde_json::json!({
                "schema_version": 99,
                "kind": COSMETICS_EXPORT_KIND,
                "exported_at_unix": 1,
                "config": KnifeCustomizerConfig::default(),
            }))
            .unwrap(),
        )
        .unwrap();

        let error = read_cosmetics_preset(&source).unwrap_err();

        assert_eq!(error.code, "E1002");
        assert!(error.detail.contains("Unsupported cosmetics preset schema"));
        assert!(!knife_config_path(&root).exists());
        assert!(!gun_config_path(&root).exists());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn cosmetics_export_and_import_are_atomic_and_keep_a_pre_import_backup() {
        let root = test_root();
        let mut original = KnifeCustomizerConfig::default();
        original.enabled = false;
        save_knife_config(&root, &mut original).unwrap();
        let original_knife = fs::read(knife_config_path(&root)).unwrap();
        let original_guns = fs::read(gun_config_path(&root)).unwrap();

        let export = root.join("preset.json");
        fs::write(&export, b"stale").unwrap();
        let result = export_cosmetics_preset_at(&root, &export).unwrap();
        assert_eq!(result.path, export.to_string_lossy());
        assert_eq!(result.size_bytes, fs::metadata(&export).unwrap().len());
        let mut bundle: CosmeticsPresetBundle =
            serde_json::from_slice(&fs::read(&export).unwrap()).unwrap();
        bundle.config.enabled = true;
        fs::write(&export, serde_json::to_vec_pretty(&bundle).unwrap()).unwrap();

        let backup = import_cosmetics_preset_at(&root, &export, || Ok(()))
            .unwrap()
            .unwrap();

        assert!(read_knife_config(&root).unwrap().enabled);
        assert_eq!(
            fs::read(backup.join("player_knife_presets.json")).unwrap(),
            original_knife
        );
        assert_eq!(
            fs::read(backup.join("player_gun_presets.json")).unwrap(),
            original_guns
        );
        assert!(fs::read_dir(knife_config_path(&root).parent().unwrap())
            .unwrap()
            .flatten()
            .all(|entry| !entry.file_name().to_string_lossy().contains(".cs2bi-")));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn cosmetics_import_rolls_back_when_post_write_mirroring_fails() {
        let root = test_root();
        let mut original = KnifeCustomizerConfig::default();
        original.enabled = false;
        save_knife_config(&root, &mut original).unwrap();
        let original_knife = fs::read(knife_config_path(&root)).unwrap();
        let original_guns = fs::read(gun_config_path(&root)).unwrap();

        let source = root.join("preset.json");
        let mut replacement = original;
        replacement.enabled = true;
        let bundle = CosmeticsPresetBundle {
            schema_version: COSMETICS_EXPORT_SCHEMA_VERSION,
            kind: COSMETICS_EXPORT_KIND.into(),
            exported_at_unix: 1,
            config: replacement,
        };
        fs::write(&source, serde_json::to_vec_pretty(&bundle).unwrap()).unwrap();

        let error = import_cosmetics_preset_at(&root, &source, || {
            Err(AppError::transaction("simulated mirror failure"))
        })
        .unwrap_err();

        assert!(error.detail.contains("simulated mirror failure"));
        assert_eq!(fs::read(knife_config_path(&root)).unwrap(), original_knife);
        assert_eq!(fs::read(gun_config_path(&root)).unwrap(), original_guns);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn legacy_config_migration_restarts_the_portable_first_run_wizard() {
        let mut legacy = AppConfig::default();
        legacy.language = Some("english".into());
        legacy.csgo_path = Some(r"F:\SteamLibrary\game\csgo".into());
        legacy.first_run_done = true;
        legacy.first_run_step = None;

        let migrated = prepare_legacy_config_for_portable_state(legacy);

        assert!(!migrated.first_run_done);
        assert_eq!(migrated.first_run_step.as_deref(), Some("language"));
        assert_eq!(migrated.language.as_deref(), Some("english"));
        assert_eq!(
            migrated.csgo_path.as_deref(),
            Some(r"F:\SteamLibrary\game\csgo")
        );
    }

    #[test]
    fn bot_mode_launch_always_includes_insecure_arguments() {
        let (arguments, options) = launch_request(LaunchMode::Bots);
        assert_eq!(
            arguments,
            vec!["-applaunch", "730", "-insecure", "-console"]
        );
        assert_eq!(options, "-insecure -console");

        let (preview_arguments, preview_options) = launch_request(LaunchMode::Preview);
        assert_eq!(
            preview_arguments,
            vec!["-applaunch", "730", "-insecure", "-console"]
        );
        assert_eq!(preview_options, "-insecure -console");

        let (online_arguments, online_options) = launch_request(LaunchMode::Online);
        assert_eq!(online_arguments, vec!["-applaunch", "730"]);
        assert!(online_options.is_empty());
    }

    #[test]
    fn online_safety_restores_the_previous_cosmetic_state() {
        let root = test_root();
        let mut config = KnifeCustomizerConfig::default();
        config.enabled = true;
        config.loadouts.ct.default_knife_defindex = 515;
        config.loadouts.ct.knife_presets.insert(
            "515".into(),
            KnifePreset {
                paint: 568,
                seed: 42,
                wear: 0.12,
                name_tag: "saved".into(),
                stattrak_enabled: true,
                stattrak_count: 99,
                souvenir_enabled: false,
                stickers: vec![],
            },
        );
        save_knife_config(&root, &mut config).unwrap();

        let mut app_config = AppConfig::default();
        enter_online_safety(&root, &mut app_config).unwrap();

        let saved = read_knife_config(&root).unwrap();
        assert!(!saved.enabled);
        assert_eq!(saved.loadouts.ct.knife_presets.len(), 1);
        assert_eq!(saved.loadouts.ct.knife_presets["515"].paint, 568);
        assert_eq!(saved.loadouts.ct.knife_presets["515"].stattrak_count, 99);
        leave_online_safety(&root, &mut app_config).unwrap();
        let restored = read_knife_config(&root).unwrap();
        assert!(restored.enabled);
        assert!(app_config.cosmetics_enabled_before_online.is_none());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn preview_safety_temporarily_enables_and_restores_cosmetics() {
        let root = test_root();
        let mut config = KnifeCustomizerConfig::default();
        config.enabled = false;
        save_knife_config(&root, &mut config).unwrap();

        let mut app_config = AppConfig::default();
        enter_preview_safety(&root, &mut app_config).unwrap();
        assert!(read_knife_config(&root).unwrap().enabled);
        assert_eq!(app_config.cosmetics_enabled_before_preview, Some(false));

        leave_preview_safety(&root, &mut app_config).unwrap();
        assert!(!read_knife_config(&root).unwrap().enabled);
        assert!(app_config.cosmetics_enabled_before_preview.is_none());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn canonical_operation_restores_preview_layout() {
        let base = test_root();
        let root = base.join("game/csgo");
        let state = base.join("state");
        let managed = root.join("addons/counterstrikesharp/plugins/BotAI/BotAI.dll");
        fs::create_dir_all(managed.parent().unwrap()).unwrap();
        fs::write(&managed, b"managed").unwrap();
        mode_layout::set_preview(&state, &root, true).unwrap();

        with_canonical_layout(&state, &root, true, || {
            assert!(managed.is_file());
            assert!(!mode_layout::disabled_path(&managed).exists());
            Ok(())
        })
        .unwrap();

        assert!(!managed.exists());
        assert!(mode_layout::disabled_path(&managed).is_file());
        assert!(mode_layout::layout_healthy(&root, true));
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn canonical_operation_uses_configured_non_preview_layout() {
        let base = test_root();
        let root = base.join("game/csgo");
        let state = base.join("state");
        let managed = root.join("addons/counterstrikesharp/plugins/BotAI/BotAI.dll");
        fs::create_dir_all(managed.parent().unwrap()).unwrap();
        fs::write(&managed, b"managed").unwrap();
        mode_layout::set_preview(&state, &root, true).unwrap();

        with_canonical_layout(&state, &root, false, || Ok(())).unwrap();

        assert!(managed.is_file());
        assert!(!mode_layout::disabled_path(&managed).exists());
        assert!(mode_layout::layout_healthy(&root, false));
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn preview_disabled_cfg_files_remain_available_to_presets() {
        let root = test_root();
        let aim_plugin = root.join("addons/counterstrikesharp/plugins/BotAimImprover/BotAimImprover.dll");
        let disabled_aim_plugin = mode_layout::disabled_path(&aim_plugin);
        fs::create_dir_all(disabled_aim_plugin.parent().unwrap()).unwrap();
        fs::write(disabled_aim_plugin, b"managed").unwrap();
        fs::create_dir_all(root.join(".csbip")).unwrap();
        fs::write(
            root.join(".csbip/aim-runtime.json"),
            br#"{"schema_version":1,"transport":"managed_ccsbot_schema","active":true,"mode":"head","override_count":42,"head_point_count":40,"body_point_count":2,"error_count":0,"updated_at_unix_ms":1}"#,
        ).unwrap();
        for path in cfg_paths(&root) {
            let disabled = mode_layout::disabled_path(&path);
            fs::create_dir_all(disabled.parent().unwrap()).unwrap();
            fs::write(disabled, b"bot_aim mixed\nbot_nades normal\n").unwrap();
        }

        let state = presets_at(&root, &AppConfig::default(), false);

        assert!(state.cfg_present);
        assert!(state.aim_supported);
        assert_eq!(state.aim_active, Some(true));
        assert_eq!(state.aim_transport.as_deref(), Some("managed_ccsbot_schema"));
        assert_eq!(state.aim_override_count, Some(42));
        assert_eq!(state.aim_error_count, Some(0));
        replace_managed_cfg_command(&root, "bot_aim", "bot_aim head").unwrap();
        for canonical in cfg_paths(&root) {
            assert!(!canonical.exists());
            let disabled = mode_layout::disabled_path(&canonical);
            assert!(
                fs::read_to_string(disabled)
                    .unwrap()
                    .contains("bot_aim head")
            );
        }
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn bot_randomizer_options_follow_panel_item_settings() {
        let root = test_root();
        let options = BotItems {
            skins: true,
            profiles: false,
            agents: false,
            music: true,
        };

        write_bot_randomizer_options(&root, &options).unwrap();

        let saved: BotItems =
            serde_json::from_slice(&fs::read(bot_randomizer_options_path(&root)).unwrap()).unwrap();
        assert!(saved.skins);
        assert!(!saved.profiles);
        assert!(!saved.agents);
        assert!(saved.music);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn legacy_file_validation_accepts_preview_disabled_files() {
        let root = test_root();
        let active_required = [
            "gameinfo.gi",
            "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/PlayerKnifeCustomizer.dll",
            "addons/MetaMod/bin/win64/server.dll",
            "addons/counterstrikesharp/bin/win64/counterstrikesharp.dll",
        ];
        let preview_required = [
            "cfg/my_bot_normal_config.cfg",
            "cfg/my_bot_ffa_config.cfg",
            "addons/counterstrikesharp/plugins/BotAI/BotAI.dll",
            "addons/counterstrikesharp/plugins/BotRandomizer/BotRandomizer.dll",
        ];
        for relative in active_required {
            let path = root.join(relative);
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(path, b"active").unwrap();
        }
        for relative in preview_required {
            let path = mode_layout::disabled_path(&root.join(relative));
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(path, b"disabled").unwrap();
        }

        let report = validate_files_at(None, &root, false).unwrap();

        assert!(
            report.ok,
            "preview-disabled files were reported missing: {:?}",
            report.missing
        );
        assert!(report.missing.is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn difficulty_change_stays_disabled_in_preview_mode() {
        let base = test_root();
        let root = base.join("game/csgo");
        let state = base.join("state");
        let profile = root.join("overrides/High/botprofile.vpk");
        let active = root.join("overrides/botprofile.vpk");
        fs::create_dir_all(profile.parent().unwrap()).unwrap();
        fs::write(&profile, b"high").unwrap();
        fs::write(&active, b"medium").unwrap();
        mode_layout::set_preview(&state, &root, true).unwrap();

        let info = set_difficulty_at(&root, &state, "High", false).unwrap();

        assert_eq!(info.current.as_deref(), Some("High"));
        assert!(!active.exists());
        assert_eq!(
            fs::read(mode_layout::disabled_path(&active)).unwrap(),
            b"high"
        );
        assert!(mode_layout::layout_healthy(&root, true));
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn panel_round_trip_preserves_gun_presets() {
        let root = test_root();
        let mut config = KnifeCustomizerConfig::default();
        config.music_kit_id = 36;
        config.loadouts.t.gun_presets.insert(
            "7".into(),
            KnifePreset {
                paint: 661,
                seed: 321,
                wear: 0.08,
                name_tag: String::new(),
                stattrak_enabled: false,
                stattrak_count: 12,
                souvenir_enabled: true,
                stickers: vec![],
            },
        );

        save_knife_config(&root, &mut config).unwrap();
        let saved = read_knife_config(&root).unwrap();
        assert_eq!(saved.music_kit_id, 36);
        let ak = &saved.loadouts.t.gun_presets["7"];
        assert_eq!(ak.paint, 661);
        assert_eq!(ak.seed, 321);
        assert!(ak.souvenir_enabled);
        assert!(!ak.stattrak_enabled);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn v2_configs_migrate_with_empty_stickers_and_versioned_backups() {
        let root = test_root();
        let knife_path = knife_config_path(&root);
        let gun_path = gun_config_path(&root);
        fs::create_dir_all(knife_path.parent().unwrap()).unwrap();
        fs::write(&knife_path, r#"{
            "schema_version":2,"enabled":true,"apply_to_human_players":true,"apply_on_pickup":true,
            "music_kit_id":36,"loadouts":{"ct":{"default_knife_defindex":0,"knife_presets":{},"glove":{"enabled":false,"defindex":5030,"paint":10048,"seed":0,"wear":0.01},"gun_presets":{}},"t":{"default_knife_defindex":0,"knife_presets":{},"glove":{"enabled":false,"defindex":5030,"paint":10048,"seed":0,"wear":0.01},"gun_presets":{}}},
            "shared_weapon_links":{}
        }"#).unwrap();
        fs::write(&gun_path, r#"{
            "schema_version":2,"ct":{},"t":{"7":{"paint":661,"seed":123,"wear":0.08,"name_tag":"AK","stattrak_enabled":false,"stattrak_count":0,"souvenir_enabled":false}},"shared_weapon_links":{}
        }"#).unwrap();

        let config = read_knife_config(&root).unwrap();

        assert_eq!(config.schema_version, 3);
        assert!(!config.stickers_enabled);
        assert_eq!(config.music_kit_id, 36);
        assert_eq!(config.loadouts.t.gun_presets["7"].paint, 661);
        assert!(config.loadouts.t.gun_presets["7"].stickers.is_empty());
        assert!(versioned_backup_path(&knife_path, 2).is_file());
        assert!(versioned_backup_path(&gun_path, 2).is_file());
        assert!(fs::read_to_string(versioned_backup_path(&gun_path, 2)).unwrap().contains("\"schema_version\":2"));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn malformed_sticker_fields_are_dropped_without_losing_base_skins() {
        let root = test_root();
        let knife_path = knife_config_path(&root);
        let gun_path = gun_config_path(&root);
        fs::create_dir_all(knife_path.parent().unwrap()).unwrap();
        fs::write(&knife_path, r#"{
            "schema_version":3,"enabled":true,"apply_to_human_players":true,"apply_on_pickup":true,"music_kit_id":0,"stickers_enabled":true,
            "loadouts":{"ct":{"default_knife_defindex":515,"knife_presets":{"515":{"paint":568,"seed":0,"wear":0.01,"name_tag":"","stattrak_enabled":false,"stattrak_count":0,"stickers":[{"slot":0,"id":1,"wear":0,"scale":1,"rotation":0,"offset_x":0,"offset_y":0,"custom_position":false}]}},"glove":{"enabled":false,"defindex":5030,"paint":10048,"seed":0,"wear":0.01},"gun_presets":{}},"t":{"default_knife_defindex":0,"knife_presets":{},"glove":{"enabled":false,"defindex":5030,"paint":10048,"seed":0,"wear":0.01},"gun_presets":{}}},"shared_weapon_links":{}
        }"#).unwrap();
        fs::write(&gun_path, r#"{
            "schema_version":3,"ct":{},"t":{"7":{"paint":661,"seed":321,"wear":0.08,"name_tag":"AK","stattrak_enabled":false,"stattrak_count":0,"souvenir_enabled":false,"stickers":[{"slot":0,"id":4294967295,"wear":0,"scale":1,"rotation":0,"offset_x":0,"offset_y":0,"custom_position":false}]}},"shared_weapon_links":{}
        }"#).unwrap();

        let config = read_knife_config(&root).unwrap();

        assert_eq!(config.loadouts.ct.knife_presets["515"].paint, 568);
        assert!(config.loadouts.ct.knife_presets["515"].stickers.is_empty());
        assert_eq!(config.loadouts.t.gun_presets["7"].paint, 661);
        assert_eq!(config.loadouts.t.gun_presets["7"].seed, 321);
        assert!(config.loadouts.t.gun_presets["7"].stickers.is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn sticker_validation_rejects_duplicates_unknown_ids_non_finite_values_and_knives() {
        let mut config = KnifeCustomizerConfig::default();
        let mut gun = test_preset(661);
        gun.stickers = vec![test_sticker(0, 1), test_sticker(0, 1)];
        config.loadouts.t.gun_presets.insert("7".into(), gun.clone());
        assert!(normalize_knife_config(&mut config).unwrap_err().detail.contains("unique"));

        gun.stickers = vec![test_sticker(0, u32::MAX)];
        config.loadouts.t.gun_presets.insert("7".into(), gun.clone());
        assert!(normalize_knife_config(&mut config).unwrap_err().detail.contains("Unknown"));

        gun.stickers = vec![StickerPreset { scale: f32::NAN, ..test_sticker(0, 1) }];
        config.loadouts.t.gun_presets.insert("7".into(), gun);
        assert!(normalize_knife_config(&mut config).unwrap_err().detail.contains("range"));

        let mut unsupported = test_preset(661);
        unsupported.stickers = vec![test_sticker(0, 1)];
        let mut config = KnifeCustomizerConfig::default();
        config.loadouts.ct.gun_presets.insert("42".into(), unsupported);
        assert!(normalize_knife_config(&mut config).unwrap_err().detail.contains("not supported"));

        let mut knife = test_preset(568);
        knife.stickers = vec![test_sticker(0, 1)];
        let mut config = KnifeCustomizerConfig::default();
        config.loadouts.ct.knife_presets.insert("515".into(), knife);
        config.loadouts.ct.default_knife_defindex = 515;
        assert!(normalize_knife_config(&mut config).unwrap_err().detail.contains("Knife stickers"));
    }

    #[test]
    fn sticker_change_detection_ignores_base_skin_edits_but_tracks_gate_and_slots() {
        let mut current = KnifeCustomizerConfig::default();
        current.loadouts.t.gun_presets.insert("7".into(), test_preset(661));
        let mut edited = current.clone();
        edited.loadouts.t.gun_presets.get_mut("7").unwrap().paint = 302;
        assert!(!sticker_configuration_changed(&current, &edited));

        edited.stickers_enabled = true;
        assert!(sticker_configuration_changed(&current, &edited));
        edited.stickers_enabled = false;
        edited.loadouts.t.gun_presets.get_mut("7").unwrap().stickers = vec![test_sticker(0, 1)];
        assert!(sticker_configuration_changed(&current, &edited));
    }

    #[test]
    fn release_gate_disables_stickers_without_discarding_saved_slots() {
        let mut app_config = AppConfig::default();
        app_config.experimental_stickers_enabled = true;
        apply_release_feature_gates(&mut app_config);
        assert!(!app_config.experimental_stickers_enabled);

        let mut cosmetics = KnifeCustomizerConfig::default();
        cosmetics.stickers_enabled = true;
        let mut preset = test_preset(661);
        preset.stickers = vec![test_sticker(0, 1)];
        cosmetics.loadouts.t.gun_presets.insert("7".into(), preset);
        normalize_knife_config(&mut cosmetics).unwrap();

        assert!(!cosmetics.stickers_enabled);
        assert_eq!(cosmetics.loadouts.t.gun_presets["7"].stickers.len(), 1);
    }

    #[test]
    fn schema_one_cosmetics_exports_remain_importable() {
        let root = test_root();
        fs::create_dir_all(&root).unwrap();
        let source = root.join("legacy-export.json");
        let mut config = KnifeCustomizerConfig::default();
        config.schema_version = 2;
        fs::write(&source, serde_json::to_vec_pretty(&CosmeticsPresetBundle {
            schema_version: LEGACY_COSMETICS_EXPORT_SCHEMA_VERSION,
            kind: COSMETICS_EXPORT_KIND.into(),
            exported_at_unix: 1,
            config,
        }).unwrap()).unwrap();

        let imported = read_cosmetics_preset(&source).unwrap();

        assert_eq!(imported.schema_version, 3);
        assert!(!imported.stickers_enabled);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn legacy_configs_migrate_by_weapon_side_and_are_backed_up_once() {
        let root = test_root();
        let knife_path = knife_config_path(&root);
        let gun_path = gun_config_path(&root);
        fs::create_dir_all(knife_path.parent().unwrap()).unwrap();
        fs::write(&knife_path, r#"{
            "enabled": true,
            "apply_to_human_players": true,
            "apply_on_pickup": true,
            "default_knife_defindex": 515,
            "presets": {"515":{"paint":568,"seed":0,"wear":0.01,"name_tag":"","stattrak_enabled":false,"stattrak_count":0}},
            "glove":{"enabled":false,"defindex":5030,"paint":10048,"seed":0,"wear":0.01}
        }"#).unwrap();
        fs::write(&gun_path, r#"{
            "7":{"paint":661,"seed":0,"wear":0.01,"name_tag":"","stattrak_enabled":false,"stattrak_count":0},
            "9":{"paint":344,"seed":0,"wear":0.01,"name_tag":"","stattrak_enabled":false,"stattrak_count":0},
            "16":{"paint":309,"seed":0,"wear":0.01,"name_tag":"","stattrak_enabled":false,"stattrak_count":0}
        }"#).unwrap();

        let mut config = read_knife_config(&root).unwrap();
        assert_eq!(config.loadouts.ct.default_knife_defindex, 515);
        assert_eq!(config.loadouts.t.default_knife_defindex, 515);
        assert!(config.loadouts.t.gun_presets.contains_key("7"));
        assert!(!config.loadouts.ct.gun_presets.contains_key("7"));
        assert!(config.loadouts.ct.gun_presets.contains_key("16"));
        assert!(!config.loadouts.t.gun_presets.contains_key("16"));
        assert_eq!(
            config.loadouts.ct.gun_presets["9"],
            config.loadouts.t.gun_presets["9"]
        );
        assert_eq!(config.shared_weapon_links["9"], true);
        assert!(legacy_backup_path(&knife_path).exists());
        assert!(legacy_backup_path(&gun_path).exists());
        let backup = fs::read(&legacy_backup_path(&knife_path)).unwrap();
        save_knife_config(&root, &mut config).unwrap();
        assert_eq!(fs::read(legacy_backup_path(&knife_path)).unwrap(), backup);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn linked_shared_weapon_presets_must_match() {
        let mut config = KnifeCustomizerConfig::default();
        let mut left = KnifePreset {
            paint: 344,
            seed: 0,
            wear: 0.01,
            name_tag: String::new(),
            stattrak_enabled: false,
            stattrak_count: 0,
            souvenir_enabled: false,
            stickers: vec![],
        };
        let mut right = left.clone();
        right.paint = 279;
        config
            .loadouts
            .ct
            .gun_presets
            .insert("9".into(), left.clone());
        config.loadouts.t.gun_presets.insert("9".into(), right);
        let error = normalize_knife_config(&mut config).unwrap_err();
        assert!(error.detail.contains("must match"));

        config.shared_weapon_links.insert("9".into(), false);
        normalize_knife_config(&mut config).unwrap();
        left.paint = config.loadouts.ct.gun_presets["9"].paint;
        assert_eq!(left.paint, 344);
    }
}

pub fn run() {
    let previous_hook = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        if let Ok(root) = app_storage::root() {
            logging::append(&root, "FATAL", "panel.panic", &info.to_string());
        }
        previous_hook(info);
    }));
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _, _| { if let Some(w) = app.get_webview_window("main") { let _ = w.set_focus(); } }))
        .plugin(tauri_plugin_clipboard_manager::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            if let Ok(root) = app_storage::root() {
                let removed = logging::cleanup(&root).unwrap_or(0);
                let archives_removed = diagnostics::cleanup_archives(&root).unwrap_or(0);
                let update_cache_removed = online_update::cleanup_cache(None).unwrap_or(0);
                logging::append(&root, "INFO", "panel.started", &format!("version={}, logs_collected={removed}, archives_collected={archives_removed}, update_cache_collected={update_cache_removed}", app_version::display()));
            }
            let update_app = app.handle().clone();
            tauri::async_runtime::spawn_blocking(move || {
                let plugin_version = installed_plugin_version(&update_app);
                if let Err(error) = online_update::check(false, plugin_version.as_deref()) {
                    online_update::record_check_error(&error);
                    if let Ok(root) = app_storage::root() {
                        logging::append(&root, "WARN", "update.startup_check_failed", &format!("host=github.com, {}", error.detail));
                    }
                }
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![get_config, save_config, detect_directories, select_directory,
            cleanup_backups, validate_files, get_difficulty, set_difficulty, get_mode, set_mode,
            reconcile_launch_options, launch_cs2, reconcile_core_json, get_bot_items, set_bot_item,
            get_presets, set_aim, set_nades, get_drop_knives, set_drop_knives,
            get_knife_customizer, save_knife_customizer, export_cosmetics_preset,
            import_cosmetics_preset, get_runtime_snapshot,
            inspect_installation, get_install_plan, install_payload, repair_payload,
            restore_payload, restore_pristine_cs2, export_diagnostics, get_panel_memory, save_panel_memory,
            appearance::get_appearance, appearance::save_appearance,
            appearance::export_appearance, appearance::import_appearance,
            record_panel_error, get_update_snapshot, check_online_updates,
            install_panel_update, install_plugin_update, install_all_updates, cancel_update,
            get_match_catalog, prepare_and_launch_match, finish_active_match, get_active_match, list_match_history,
            get_match_result, delete_match, run_install_checks, play_demo, open_demo_folder])
        .run(tauri::generate_context!())
        .expect("error while running CS2BotImproverPlus");
}

pub fn maybe_run_update_helper() -> bool {
    online_update::maybe_apply_panel_update()
}
