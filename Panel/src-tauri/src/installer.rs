use crate::{AppError, Result, atomic_fs, mode_layout};
use fs2::FileExt;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::BTreeMap;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read};
use std::path::{Component, Path, PathBuf};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const MANIFEST_FILE: &str = "plus-payload-manifest.json";
pub const PANEL_UPDATE_MARKER: &str = "csbip-panel-update.json";

const SUPERSEDED_PAYLOAD_PATHS: &[(&str, &str)] = &[
    (
        "addons/counterstrikesharp/plugins/PlusMatchCoordinator/rating-plus-3.0-proxy-v1.json",
        "addons/counterstrikesharp/plugins/PlusMatchCoordinator/open-rating-3.0-proxy-v1.json",
    ),
];

const PLUS_MARKERS: &[&str] = &[
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/PlayerKnifeCustomizer.dll",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/player_knife_presets.json",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/player_gun_presets.json",
];

const UPSTREAM_MARKERS: &[&str] = &[
    "addons/counterstrikesharp/plugins/BotAI/BotAI.dll",
    "addons/counterstrikesharp/plugins/BotAimImprover/BotAimImprover.dll",
    "addons/counterstrikesharp/plugins/BotBuy/BotBuy.dll",
    "addons/counterstrikesharp/plugins/BotRandomizer/BotRandomizer.dll",
    "addons/counterstrikesharp/plugins/BotState/BotState.dll",
    "addons/counterstrikesharp/plugins/NadeSystem/NadeSystem.dll",
    "addons/metamod/BotHider.vdf",
    "addons/metamod/RayTrace.vdf",
];

const SUITE_OWNED_ROOTS: &[&str] = &[
    "addons/BotHider",
    "addons/BotController",
    "addons/RayTrace",
    "addons/counterstrikesharp/api",
    "addons/counterstrikesharp/bin",
    "addons/counterstrikesharp/dotnet",
    "addons/counterstrikesharp/gamedata",
    "addons/counterstrikesharp/lang",
    "addons/counterstrikesharp/shared",
    "addons/counterstrikesharp/source",
    "addons/counterstrikesharp/plugins/BotAI",
    "addons/counterstrikesharp/plugins/BotAimImprover",
    "addons/counterstrikesharp/plugins/BotBuy",
    "addons/counterstrikesharp/plugins/BotControllerImpl",
    "addons/counterstrikesharp/plugins/BotHiderImpl",
    "addons/counterstrikesharp/plugins/BotRandomizer",
    "addons/counterstrikesharp/plugins/BotState",
    "addons/counterstrikesharp/plugins/NadeSystem",
    "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer",
    "addons/counterstrikesharp/plugins/RayTraceImpl",
    "addons/counterstrikesharp/plugins/RoundDamageRecap",
    "addons/counterstrikesharp/plugins/disabled/BotAI_for_Linux",
    "addons/counterstrikesharp/plugins/disabled/BotAimImprover_for_Linux",
    "addons/counterstrikesharp/plugins/disabled/CS2_ExecAfter",
    "addons/metamod/bin",
    "overrides",
];

const SUITE_OWNED_FILES: &[&str] = &[
    "addons/metamod_x64.vdf",
    "addons/metamod/BotController.vdf",
    "addons/metamod/BotHider.vdf",
    "addons/metamod/RayTrace.vdf",
    "addons/metamod/counterstrikesharp.vdf",
    "addons/metamod/metaplugins.ini",
    "cfg/bot_buy.cfg",
    "cfg/my_bot_ffa_config.cfg",
    "cfg/my_bot_ffa_config_rules_unchanged.cfg",
    "cfg/my_bot_normal_config.cfg",
    "cfg/my_bot_normal_config_rules_unchanged.cfg",
];

const FILE_RETRY_DELAYS: &[Duration] = &[
    Duration::from_millis(80),
    Duration::from_millis(200),
    Duration::from_millis(450),
    Duration::from_millis(900),
    Duration::from_millis(1_400),
    Duration::from_millis(2_200),
];

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum InstallationSource {
    Clean,
    ManagedPlus,
    LegacyPlus,
    Upstream,
    MixedUnknown,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MigrationKind {
    FreshInstall,
    ManagedUpgrade,
    AdoptLegacyPlus,
    ReplaceUpstream,
    Blocked,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RestoreBaseline {
    SteamOriginal,
    PreMigration,
    ExistingRecord,
    None,
}

fn existing_record_baseline() -> RestoreBaseline {
    RestoreBaseline::ExistingRecord
}
fn managed_source() -> InstallationSource {
    InstallationSource::ManagedPlus
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PayloadManifest {
    pub schema_version: u32,
    pub package_version: String,
    pub entries: Vec<PayloadEntry>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PayloadEntry {
    pub path: String,
    pub size: u64,
    pub sha256: String,
    pub component: String,
    pub ownership: String,
    pub restore_policy: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct InstallRecordEntry {
    path: String,
    original_existed: bool,
    original_sha256: Option<String>,
    original_backup: Option<String>,
    installed_sha256: String,
    ownership: String,
    restore_policy: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct InstallRecord {
    schema_version: u32,
    installation_id: String,
    target: String,
    package_version: String,
    installed_at: u64,
    #[serde(default = "managed_source")]
    source: InstallationSource,
    #[serde(default = "existing_record_baseline")]
    restore_baseline: RestoreBaseline,
    #[serde(default)]
    migrated_from_version: Option<String>,
    entries: Vec<InstallRecordEntry>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct TransactionChange {
    path: String,
    existed: bool,
    backup: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct TransactionJournal {
    schema_version: u32,
    operation: String,
    target: String,
    transaction_dir: String,
    changes: Vec<TransactionChange>,
    #[serde(default)]
    committed: bool,
    #[serde(default)]
    previous_record: Option<InstallRecord>,
}

#[derive(Clone, Debug, Serialize)]
pub struct InstallationInspection {
    pub installed: bool,
    pub package_version: Option<String>,
    pub manifest_available: bool,
    pub total: usize,
    pub healthy: usize,
    pub missing: Vec<String>,
    pub corrupt: Vec<String>,
    pub restore_available: bool,
    pub backup_path: Option<String>,
    pub interrupted_transaction: bool,
    pub source: InstallationSource,
    pub source_version: Option<String>,
    pub source_evidence: Vec<String>,
    pub migration_kind: MigrationKind,
    pub restore_baseline: RestoreBaseline,
    pub can_install: bool,
}

#[derive(Clone, Debug, Serialize)]
pub struct InstallPlan {
    pub package_version: String,
    pub target: String,
    pub total_files: usize,
    pub new_files: usize,
    pub overwritten_files: usize,
    pub backup_path: String,
    pub required_target_bytes: u64,
    pub available_target_bytes: u64,
    pub required_backup_bytes: u64,
    pub available_backup_bytes: u64,
    pub writable: bool,
    pub source: InstallationSource,
    pub source_version: Option<String>,
    pub source_evidence: Vec<String>,
    pub migration_kind: MigrationKind,
    pub restore_baseline: RestoreBaseline,
    pub can_install: bool,
}

#[derive(Clone, Debug, Serialize)]
pub struct InstallTransactionResult {
    pub package_version: String,
    pub installed_files: usize,
    pub backup_path: String,
    pub repaired: bool,
}

#[derive(Clone, Debug, Serialize)]
pub struct RestoreResult {
    pub restored_files: usize,
    pub removed_files: usize,
    pub preserved_files: usize,
    pub presets_backup: Option<String>,
    pub steam_verify_uri: String,
    pub result_kind: String,
}

#[derive(Clone, Debug)]
struct SourceDetection {
    source: InstallationSource,
    version: Option<String>,
    evidence: Vec<String>,
    migration_kind: MigrationKind,
    restore_baseline: RestoreBaseline,
    can_install: bool,
}

fn unix_time() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

fn sha256(path: &Path) -> Result<String> {
    let mut file = File::open(path).map_err(|error| AppError::payload(error.to_string()))?;
    let mut digest = Sha256::new();
    let mut buffer = [0u8; 64 * 1024];
    loop {
        let read = file
            .read(&mut buffer)
            .map_err(|error| AppError::payload(error.to_string()))?;
        if read == 0 {
            break;
        }
        digest.update(&buffer[..read]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn safe_relative(raw: &str) -> Result<PathBuf> {
    let path = PathBuf::from(raw.replace('/', "\\"));
    if path.as_os_str().is_empty()
        || path.components().any(|component| {
            matches!(
                component,
                Component::ParentDir | Component::RootDir | Component::Prefix(_)
            )
        })
    {
        return Err(AppError::payload(format!("Unsafe payload path: {raw}")));
    }
    Ok(path)
}

fn write_json_atomic<T: Serialize>(path: &Path, value: &T) -> Result<()> {
    let bytes = serde_json::to_vec_pretty(value)
        .map_err(|error| AppError::transaction(error.to_string()))?;
    atomic_fs::write_replace(path, &bytes).map_err(|error| {
        AppError::transaction(format!(
            "Atomic JSON write failed ({}): {error}",
            path.display()
        ))
    })
}

fn copy_file(source: &Path, destination: &Path) -> Result<()> {
    copy_file_for("file copy", source, destination)
}

fn is_retryable_file_error(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::Interrupted
            | io::ErrorKind::WouldBlock
            | io::ErrorKind::PermissionDenied
            | io::ErrorKind::NotFound
    ) || matches!(error.raw_os_error(), Some(2 | 3 | 5 | 32 | 33))
}

fn retry_file_io<T>(mut operation: impl FnMut() -> io::Result<T>) -> io::Result<T> {
    let mut attempt = 0usize;
    loop {
        match operation() {
            Ok(value) => return Ok(value),
            Err(error) if attempt < FILE_RETRY_DELAYS.len() && is_retryable_file_error(&error) => {
                thread::sleep(FILE_RETRY_DELAYS[attempt]);
                attempt += 1;
            }
            Err(error) => return Err(error),
        }
    }
}

fn copy_file_for(stage: &str, source: &Path, destination: &Path) -> Result<()> {
    if let Some(parent) = destination.parent() {
        retry_file_io(|| fs::create_dir_all(parent)).map_err(|error| {
            AppError::transaction(format!(
                "{stage}: cannot create destination directory ({}): {error}",
                parent.display()
            ))
        })?;
    }
    retry_file_io(|| fs::copy(source, destination)).map_err(|error| {
        AppError::transaction(format!(
            "{stage}: cannot copy {} -> {}: {error}",
            source.display(),
            destination.display()
        ))
    })?;
    Ok(())
}

fn installation_id(target: &Path) -> String {
    let normalized = target
        .to_string_lossy()
        .replace('/', "\\")
        .to_ascii_lowercase();
    let digest = Sha256::digest(normalized.as_bytes());
    format!("{:x}", digest)[..16].to_string()
}

fn installation_dir(state_root: &Path, target: &Path) -> PathBuf {
    state_root
        .join("installations")
        .join(installation_id(target))
}

struct TransactionLock {
    file: File,
}

impl Drop for TransactionLock {
    fn drop(&mut self) {
        let _ = FileExt::unlock(&self.file);
    }
}

fn transaction_lock_path(target: &Path) -> PathBuf {
    let normalized = target
        .to_string_lossy()
        .replace('/', "\\")
        .to_ascii_lowercase();
    let digest = Sha256::digest(normalized.as_bytes());
    std::env::temp_dir()
        .join("CS2BotImproverPlus")
        .join("transaction-locks")
        .join(format!("{:x}.lock", digest))
}

fn open_transaction_lock(target: &Path) -> Result<File> {
    let path = transaction_lock_path(target);
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|error| {
            AppError::transaction(format!(
                "Cannot create the installation lock directory ({}): {error}",
                parent.display()
            ))
        })?;
    }
    OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .open(&path)
        .map_err(|error| {
            AppError::transaction(format!(
                "Cannot open the installation lock ({}): {error}",
                path.display()
            ))
        })
}

fn lock_contended(error: &io::Error) -> bool {
    error.kind() == io::ErrorKind::WouldBlock || matches!(error.raw_os_error(), Some(32 | 33))
}

fn acquire_transaction_lock(target: &Path) -> Result<TransactionLock> {
    let file = open_transaction_lock(target)?;
    FileExt::try_lock_exclusive(&file).map_err(|error| {
        if lock_contended(&error) {
            AppError::transaction(
                "Another CS2BotImproverPlus panel is installing, repairing, or restoring this CS2 directory"
            )
        } else {
            AppError::transaction(format!("Cannot lock this CS2 installation: {error}"))
        }
    })?;
    Ok(TransactionLock { file })
}

fn try_transaction_lock(target: &Path) -> Result<Option<TransactionLock>> {
    let file = open_transaction_lock(target)?;
    match FileExt::try_lock_exclusive(&file) {
        Ok(()) => Ok(Some(TransactionLock { file })),
        Err(error) if lock_contended(&error) => Ok(None),
        Err(error) => Err(AppError::transaction(format!(
            "Cannot inspect the installation lock: {error}"
        ))),
    }
}

fn managed_path(target: &Path, raw: &str) -> Option<PathBuf> {
    let canonical = target.join(raw.replace('/', "\\"));
    mode_layout::active_or_disabled(&canonical)
}

fn detect_source(state_root: &Path, target: &Path) -> SourceDetection {
    let directory = installation_dir(state_root, target);
    if let Some(record) = read_record(&directory) {
        return SourceDetection {
            source: InstallationSource::ManagedPlus,
            version: Some(record.package_version),
            evidence: vec![".csbip installation record".to_string()],
            migration_kind: MigrationKind::ManagedUpgrade,
            restore_baseline: record.restore_baseline,
            can_install: true,
        };
    }

    let plus = PLUS_MARKERS
        .iter()
        .filter(|path| managed_path(target, path).is_some())
        .map(|path| (*path).to_string())
        .collect::<Vec<_>>();
    if !plus.is_empty() {
        return SourceDetection {
            source: InstallationSource::LegacyPlus,
            version: None,
            evidence: plus,
            migration_kind: MigrationKind::AdoptLegacyPlus,
            restore_baseline: RestoreBaseline::PreMigration,
            can_install: true,
        };
    }

    let upstream = UPSTREAM_MARKERS
        .iter()
        .filter(|path| managed_path(target, path).is_some())
        .map(|path| (*path).to_string())
        .collect::<Vec<_>>();
    if upstream.len() >= 3 {
        return SourceDetection {
            source: InstallationSource::Upstream,
            version: None,
            evidence: upstream,
            migration_kind: MigrationKind::ReplaceUpstream,
            restore_baseline: RestoreBaseline::PreMigration,
            can_install: true,
        };
    }
    if !upstream.is_empty() {
        return SourceDetection {
            source: InstallationSource::MixedUnknown,
            version: None,
            evidence: upstream,
            migration_kind: MigrationKind::Blocked,
            restore_baseline: RestoreBaseline::None,
            can_install: false,
        };
    }

    SourceDetection {
        source: InstallationSource::Clean,
        version: None,
        evidence: Vec::new(),
        migration_kind: MigrationKind::FreshInstall,
        restore_baseline: RestoreBaseline::SteamOriginal,
        can_install: true,
    }
}

fn read_manifest_document(payload_root: &Path) -> Result<PayloadManifest> {
    let path = payload_root.join(MANIFEST_FILE);
    let bytes = fs::read(&path).map_err(|_| {
        if payload_root.join(PANEL_UPDATE_MARKER).is_file() {
            AppError::payload(format!(
                "This is the Panel-only online-update component, not the complete installer. Download and extract LocalArena-v{}-windows.zip for a first installation.",
                crate::app_version::display()
            ))
        } else {
            AppError::payload(format!(
                "The complete plugin payload is missing. Keep LocalArena.exe beside addons, cfg, overrides, and {}. Expected: {}",
                MANIFEST_FILE,
                path.display()
            ))
        }
    })?;
    let manifest: PayloadManifest = serde_json::from_slice(&bytes)
        .map_err(|error| AppError::payload(format!("Invalid payload manifest: {error}")))?;
    if manifest.schema_version != 1 || manifest.entries.is_empty() {
        return Err(AppError::payload("Unsupported or empty payload manifest"));
    }
    for entry in &manifest.entries {
        safe_relative(&entry.path)?;
        if entry.sha256.len() != 64 || !entry.sha256.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err(AppError::payload(format!(
                "Invalid payload hash: {}",
                entry.path
            )));
        }
    }
    Ok(manifest)
}

fn read_manifest(payload_root: &Path) -> Result<PayloadManifest> {
    let manifest = read_manifest_document(payload_root)?;
    verify_manifest_files(payload_root, &manifest, false)?;
    Ok(manifest)
}

fn same_canonical_directory(left: &Path, right: &Path) -> bool {
    let (Ok(left), Ok(right)) = (fs::canonicalize(left), fs::canonicalize(right)) else {
        return false;
    };
    left.to_string_lossy()
        .eq_ignore_ascii_case(&right.to_string_lossy())
}

fn verify_manifest_files(
    payload_root: &Path,
    manifest: &PayloadManifest,
    allow_modified_preserve_config: bool,
) -> Result<()> {
    for entry in &manifest.entries {
        let relative = safe_relative(&entry.path)?;
        let source = payload_root.join(&relative);
        if !source.is_file() {
            return Err(AppError::payload(format!(
                "Payload verification failed: {}",
                entry.path
            )));
        }
        if allow_modified_preserve_config && entry.restore_policy == "preserve-config" {
            continue;
        }
        if fs::metadata(&source)
            .map_err(AppError::transaction_io)?
            .len()
            != entry.size
            || !sha256(&source)?.eq_ignore_ascii_case(&entry.sha256)
        {
            return Err(AppError::payload(format!(
                "Payload verification failed: {}",
                entry.path
            )));
        }
    }
    Ok(())
}

fn read_manifest_for_target(payload_root: &Path, target: &Path) -> Result<PayloadManifest> {
    let manifest = read_manifest_document(payload_root)?;
    verify_manifest_files(
        payload_root,
        &manifest,
        same_canonical_directory(payload_root, target),
    )?;
    Ok(manifest)
}

pub fn verify_payload(payload_root: &Path) -> Result<PayloadManifest> {
    read_manifest(payload_root)
}

pub fn verify_payload_for_target(
    payload_root: &Path,
    target: &Path,
) -> Result<PayloadManifest> {
    read_manifest_for_target(payload_root, target)
}

fn read_record(directory: &Path) -> Option<InstallRecord> {
    serde_json::from_slice(&fs::read(directory.join("record.json")).ok()?).ok()
}

pub fn inspect(
    payload_root: &Path,
    state_root: &Path,
    target: &Path,
) -> Result<InstallationInspection> {
    inspect_impl(payload_root, state_root, target, true)
}

pub fn inspect_quick(
    payload_root: &Path,
    state_root: &Path,
    target: &Path,
) -> Result<InstallationInspection> {
    inspect_impl(payload_root, state_root, target, false)
}

fn inspect_impl(
    payload_root: &Path,
    state_root: &Path,
    target: &Path,
    verify_hashes: bool,
) -> Result<InstallationInspection> {
    let directory = installation_dir(state_root, target);
    let record = read_record(&directory);
    let detection = detect_source(state_root, target);
    let manifest = if verify_hashes {
        read_manifest_for_target(payload_root, target).ok()
    } else {
        read_manifest_document(payload_root).ok()
    };
    let entries: Vec<(String, String, Option<u64>, String)> = if let Some(manifest) = &manifest {
        manifest
            .entries
            .iter()
            .map(|entry| {
                (
                    entry.path.clone(),
                    entry.sha256.clone(),
                    Some(entry.size),
                    entry.restore_policy.clone(),
                )
            })
            .collect()
    } else if let Some(record) = &record {
        record
            .entries
            .iter()
            .map(|entry| {
                (
                    entry.path.clone(),
                    entry.installed_sha256.clone(),
                    None,
                    entry.restore_policy.clone(),
                )
            })
            .collect()
    } else {
        Vec::new()
    };

    let mut healthy = 0;
    let mut missing = Vec::new();
    let mut corrupt = Vec::new();
    for (raw, expected, expected_size, restore_policy) in &entries {
        let canonical = target.join(safe_relative(raw)?);
        let disabled = mode_layout::disabled_path(&canonical);
        let path = if canonical.is_file() {
            canonical
        } else {
            disabled
        };
        if !path.is_file() {
            missing.push(raw.clone());
        } else if restore_policy == "preserve-config" {
            healthy += 1;
        } else if verify_hashes {
            if sha256(&path)?.eq_ignore_ascii_case(expected) {
                healthy += 1;
            } else {
                corrupt.push(raw.clone());
            }
        } else if expected_size.is_none_or(|size| {
            fs::metadata(&path)
                .map(|metadata| metadata.len() == size)
                .unwrap_or(false)
        }) {
            healthy += 1;
        } else {
            corrupt.push(raw.clone());
        }
    }
    Ok(InstallationInspection {
        installed: record.is_some(),
        package_version: record.as_ref().map(|record| record.package_version.clone()),
        manifest_available: manifest.is_some(),
        total: entries.len(),
        healthy,
        missing,
        corrupt,
        restore_available: record.is_some(),
        backup_path: record
            .as_ref()
            .map(|_| directory.to_string_lossy().into_owned()),
        interrupted_transaction: directory.join("journal.json").is_file(),
        source: detection.source,
        source_version: detection.version,
        source_evidence: detection.evidence,
        migration_kind: detection.migration_kind,
        restore_baseline: detection.restore_baseline,
        can_install: detection.can_install,
    })
}

struct Preflight {
    required_target_bytes: u64,
    available_target_bytes: u64,
    required_backup_bytes: u64,
    available_backup_bytes: u64,
}

#[derive(Clone, Debug)]
pub struct InstallSpaceRequirements {
    pub required_target_bytes: u64,
    pub available_target_bytes: u64,
    pub required_backup_bytes: u64,
    pub available_backup_bytes: u64,
}

fn inspect_space(manifest: &PayloadManifest, state_root: &Path, target: &Path) -> Result<Preflight> {
    if !target.is_dir() {
        return Err(AppError::transaction(format!(
            "Installation target is not a directory: {}",
            target.display()
        )));
    }
    fs::create_dir_all(state_root).map_err(AppError::transaction_io)?;
    let probe = target.join(format!(".cs2bi-write-test-{}", unix_time()));
    fs::write(&probe, b"write-test").map_err(|error| {
        AppError::transaction(format!(
            "The CS2 directory is not writable ({}): {error}",
            target.display()
        ))
    })?;
    fs::remove_file(&probe).map_err(|error| {
        AppError::transaction(format!(
            "The CS2 write test could not be cleaned up ({}): {error}",
            probe.display()
        ))
    })?;

    let mut largest_payload = 0;
    let mut backup_bytes: u64 = 0;
    for entry in &manifest.entries {
        largest_payload = largest_payload.max(entry.size);
        let relative = safe_relative(&entry.path)?;
        let mut cursor = target.to_path_buf();
        let components = relative.components().collect::<Vec<_>>();
        for component in components.iter().take(components.len().saturating_sub(1)) {
            cursor.push(component.as_os_str());
            if cursor.exists() && !cursor.is_dir() {
                return Err(AppError::transaction(format!(
                    "A payload parent path is not a directory: {}",
                    cursor.display()
                )));
            }
        }
        let destination = target.join(&relative);
        let existing = if destination.exists() {
            destination.clone()
        } else {
            mode_layout::disabled_path(&destination)
        };
        if existing.exists() && !existing.is_file() {
            return Err(AppError::transaction(format!(
                "A payload file target is occupied by a directory: {}",
                existing.display()
            )));
        }
        if let Ok(metadata) = fs::metadata(existing) {
            backup_bytes = backup_bytes.saturating_add(metadata.len());
        }
    }

    const MARGIN: u64 = 64 * 1024 * 1024;
    let required_target_bytes = largest_payload.saturating_add(MARGIN);
    let required_backup_bytes = backup_bytes.saturating_mul(2).saturating_add(MARGIN);
    let available_target_bytes = fs2::available_space(target).map_err(AppError::transaction_io)?;
    let available_backup_bytes =
        fs2::available_space(state_root).map_err(AppError::transaction_io)?;
    Ok(Preflight {
        required_target_bytes,
        available_target_bytes,
        required_backup_bytes,
        available_backup_bytes,
    })
}

pub fn inspect_space_requirements(
    payload_root: &Path,
    state_root: &Path,
    target: &Path,
) -> Result<InstallSpaceRequirements> {
    let manifest = read_manifest_for_target(payload_root, target)?;
    let preflight = inspect_space(&manifest, state_root, target)?;
    Ok(InstallSpaceRequirements {
        required_target_bytes: preflight.required_target_bytes,
        available_target_bytes: preflight.available_target_bytes,
        required_backup_bytes: preflight.required_backup_bytes,
        available_backup_bytes: preflight.available_backup_bytes,
    })
}

fn preflight(manifest: &PayloadManifest, state_root: &Path, target: &Path) -> Result<Preflight> {
    let preflight = inspect_space(manifest, state_root, target)?;
    if preflight.available_target_bytes < preflight.required_target_bytes {
        return Err(AppError::transaction(format!(
            "Insufficient space in CS2 directory: need {} bytes, available {}",
            preflight.required_target_bytes, preflight.available_target_bytes
        )));
    }
    if preflight.available_backup_bytes < preflight.required_backup_bytes {
        return Err(AppError::transaction(format!(
            "Insufficient space for backups: need {} bytes, available {}",
            preflight.required_backup_bytes, preflight.available_backup_bytes
        )));
    }
    Ok(preflight)
}

pub fn plan(payload_root: &Path, state_root: &Path, target: &Path) -> Result<InstallPlan> {
    let manifest = read_manifest_for_target(payload_root, target)?;
    let detection = detect_source(state_root, target);
    let preflight = preflight(&manifest, state_root, target)?;
    let mut new_files = 0;
    let mut overwritten_files = 0;
    for entry in &manifest.entries {
        let destination = target.join(safe_relative(&entry.path)?);
        if destination.exists() || mode_layout::disabled_path(&destination).exists() {
            overwritten_files += 1;
        } else {
            new_files += 1;
        }
    }
    Ok(InstallPlan {
        package_version: manifest.package_version,
        target: target.to_string_lossy().into_owned(),
        total_files: manifest.entries.len(),
        new_files,
        overwritten_files,
        backup_path: installation_dir(state_root, target)
            .to_string_lossy()
            .into_owned(),
        required_target_bytes: preflight.required_target_bytes,
        available_target_bytes: preflight.available_target_bytes,
        required_backup_bytes: preflight.required_backup_bytes,
        available_backup_bytes: preflight.available_backup_bytes,
        writable: true,
        source: detection.source,
        source_version: detection.version,
        source_evidence: detection.evidence,
        migration_kind: detection.migration_kind,
        restore_baseline: detection.restore_baseline,
        can_install: detection.can_install,
    })
}

fn rollback(directory: &Path, journal: &TransactionJournal) -> Result<()> {
    for change in journal.changes.iter().rev() {
        let target = PathBuf::from(&journal.target).join(safe_relative(&change.path)?);
        if change.existed {
            let backup = change
                .backup
                .as_ref()
                .ok_or_else(|| AppError::transaction("Rollback backup is missing"))?;
            copy_file_for("transaction rollback", &directory.join(backup), &target)?;
        } else if target.is_file() {
            fs::remove_file(&target).map_err(AppError::transaction_io)?;
        }
    }
    Ok(())
}

fn restore_previous_record(directory: &Path, previous: &Option<InstallRecord>) -> Result<()> {
    let record_path = directory.join("record.json");
    if let Some(record) = previous {
        write_json_atomic(&record_path, record)
    } else if record_path.is_file() {
        fs::remove_file(record_path).map_err(AppError::transaction_io)
    } else {
        Ok(())
    }
}

fn recover_incomplete_locked(state_root: &Path, target: &Path) -> Result<bool> {
    let directory = installation_dir(state_root, target);
    let journal_path = directory.join("journal.json");
    if !journal_path.is_file() {
        return Ok(false);
    }
    let journal: TransactionJournal =
        serde_json::from_slice(&fs::read(&journal_path).map_err(AppError::transaction_io)?)
            .map_err(|error| AppError::transaction(error.to_string()))?;
    if !journal.committed {
        rollback(&directory, &journal)?;
        restore_previous_record(&directory, &journal.previous_record)?;
    }
    let _ = fs::remove_dir_all(directory.join(&journal.transaction_dir));
    fs::remove_file(journal_path).map_err(AppError::transaction_io)?;
    Ok(true)
}

pub fn recover_incomplete(state_root: &Path, target: &Path) -> Result<bool> {
    let Some(_transaction_lock) = try_transaction_lock(target)? else {
        // A live writer owns the journal. It is not an interrupted transaction.
        return Ok(false);
    };
    recover_incomplete_locked(state_root, target)
}

pub fn install(
    payload_root: &Path,
    state_root: &Path,
    target: &Path,
    repaired: bool,
) -> Result<InstallTransactionResult> {
    let _transaction_lock = acquire_transaction_lock(target)?;
    recover_incomplete_locked(state_root, target)?;
    let manifest = read_manifest_for_target(payload_root, target)?;
    let detection = detect_source(state_root, target);
    if !detection.can_install {
        return Err(AppError::transaction(
            "A partial or mixed enhanced-plugin installation was detected. Export diagnostics or restore pristine CS2 before installing.",
        ));
    }
    preflight(&manifest, state_root, target)?;
    let directory = installation_dir(state_root, target);
    fs::create_dir_all(&directory).map_err(AppError::transaction_io)?;
    let old_record = read_record(&directory);
    let old_entries: BTreeMap<String, InstallRecordEntry> = old_record
        .as_ref()
        .map(|record| {
            record
                .entries
                .iter()
                .cloned()
                .map(|entry| (entry.path.clone(), entry))
                .collect()
        })
        .unwrap_or_default();
    let transaction_name = format!("transaction-{}", unix_time());
    let mut journal = TransactionJournal {
        schema_version: 1,
        operation: if repaired { "repair" } else { "install" }.to_string(),
        target: target.to_string_lossy().into_owned(),
        transaction_dir: transaction_name.clone(),
        changes: Vec::new(),
        committed: false,
        previous_record: old_record.clone(),
    };
    let journal_path = directory.join("journal.json");
    write_json_atomic(&journal_path, &journal)?;
    let transaction_root = directory.join(&transaction_name);
    let original_root = directory.join("original");
    let mut record_entries = old_entries.clone();
    let mut installed_files = 0;

    let outcome = (|| -> Result<()> {
        for entry in &manifest.entries {
            let relative = safe_relative(&entry.path)?;
            let destination = target.join(&relative);
            let preserve_config = entry.restore_policy == "preserve-config";
            let existed = destination.is_file();

            if preserve_config && existed {
                let baseline = if let Some(previous) = old_entries.get(&entry.path) {
                    previous.clone()
                } else {
                    let original = original_root.join(&relative);
                    copy_file_for("preserve-config baseline backup", &destination, &original)?;
                    InstallRecordEntry {
                        path: entry.path.clone(),
                        original_existed: true,
                        original_sha256: Some(sha256(&destination)?),
                        original_backup: Some(format!(
                            "original/{}",
                            entry.path.replace('\\', "/")
                        )),
                        installed_sha256: sha256(&destination)?,
                        ownership: entry.ownership.clone(),
                        restore_policy: entry.restore_policy.clone(),
                    }
                };
                record_entries.insert(
                    entry.path.clone(),
                    InstallRecordEntry {
                        ownership: entry.ownership.clone(),
                        restore_policy: entry.restore_policy.clone(),
                        ..baseline
                    },
                );
                continue;
            }

            if repaired
                && old_entries.contains_key(&entry.path)
                && destination.is_file()
                && sha256(&destination)?.eq_ignore_ascii_case(&entry.sha256)
            {
                continue;
            }
            let packaged_source = payload_root.join(&relative);
            let mirror_source = relative
                .file_name()
                .map(|name| state_root.join("presets").join("current").join(name))
                .filter(|path| preserve_config && path.is_file());
            let source = mirror_source.as_deref().unwrap_or(&packaged_source);
            let source_sha256 = sha256(source)?;
            let transaction_backup = transaction_root.join(&relative);
            if existed {
                copy_file_for(
                    "installation rollback backup",
                    &destination,
                    &transaction_backup,
                )?;
            }
            journal.changes.push(TransactionChange {
                path: entry.path.clone(),
                existed,
                backup: existed
                    .then(|| format!("{transaction_name}/{}", entry.path.replace('\\', "/"))),
            });
            write_json_atomic(&journal_path, &journal)?;

            let baseline = if let Some(previous) = old_entries.get(&entry.path) {
                previous.clone()
            } else if existed {
                let original = original_root.join(&relative);
                copy_file_for("pre-install original backup", &destination, &original)?;
                InstallRecordEntry {
                    path: entry.path.clone(),
                    original_existed: true,
                    original_sha256: Some(sha256(&destination)?),
                    original_backup: Some(format!("original/{}", entry.path.replace('\\', "/"))),
                    installed_sha256: entry.sha256.clone(),
                    ownership: entry.ownership.clone(),
                    restore_policy: entry.restore_policy.clone(),
                }
            } else {
                InstallRecordEntry {
                    path: entry.path.clone(),
                    original_existed: false,
                    original_sha256: None,
                    original_backup: None,
                    installed_sha256: entry.sha256.clone(),
                    ownership: entry.ownership.clone(),
                    restore_policy: entry.restore_policy.clone(),
                }
            };

            let temporary =
                atomic_fs::temporary_path(&destination).map_err(AppError::transaction_io)?;
            let replacement = (|| -> Result<()> {
                copy_file_for("payload staging", source, &temporary)?;
                if !sha256(&temporary)?.eq_ignore_ascii_case(&source_sha256) {
                    return Err(AppError::payload(format!(
                        "Copied payload hash mismatch: {}",
                        entry.path
                    )));
                }
                retry_file_io(|| atomic_fs::sync(&temporary)).map_err(|error| {
                    AppError::transaction(format!(
                        "Payload sync failed ({}): {error}",
                        temporary.display()
                    ))
                })?;
                retry_file_io(|| atomic_fs::replace(&temporary, &destination)).map_err(|error| {
                    AppError::transaction(format!(
                        "Atomic payload replace failed ({} -> {}): {error}",
                        temporary.display(),
                        destination.display()
                    ))
                })
            })();
            if replacement.is_err() {
                let _ = fs::remove_file(&temporary);
            }
            replacement?;

            record_entries.insert(
                entry.path.clone(),
                InstallRecordEntry {
                    installed_sha256: source_sha256,
                    ownership: entry.ownership.clone(),
                    restore_policy: entry.restore_policy.clone(),
                    ..baseline
                },
            );
            installed_files += 1;
        }

        for (superseded_path, replacement_path) in SUPERSEDED_PAYLOAD_PATHS {
            if !manifest.entries.iter().any(|entry| entry.path == *replacement_path) {
                continue;
            }
            let Some(previous) = old_entries.get(*superseded_path) else {
                continue;
            };
            let relative = safe_relative(superseded_path)?;
            let destination = target.join(&relative);
            if !destination.is_file() {
                record_entries.remove(*superseded_path);
                continue;
            }
            if !sha256(&destination)?.eq_ignore_ascii_case(&previous.installed_sha256) {
                continue;
            }

            let transaction_backup = transaction_root.join(&relative);
            copy_file_for(
                "superseded payload rollback backup",
                &destination,
                &transaction_backup,
            )?;
            journal.changes.push(TransactionChange {
                path: (*superseded_path).to_string(),
                existed: true,
                backup: Some(format!(
                    "{transaction_name}/{}",
                    superseded_path.replace('\\', "/")
                )),
            });
            write_json_atomic(&journal_path, &journal)?;

            if previous.original_existed {
                let original = previous.original_backup.as_ref().ok_or_else(|| {
                    AppError::transaction(format!(
                        "Original backup is missing for superseded payload: {superseded_path}"
                    ))
                })?;
                copy_file_for(
                    "superseded payload original restore",
                    &directory.join(original),
                    &destination,
                )?;
            } else {
                fs::remove_file(&destination).map_err(AppError::transaction_io)?;
            }
            record_entries.remove(*superseded_path);
        }
        Ok(())
    })();

    if let Err(error) = outcome {
        let rollback_error = rollback(&directory, &journal).err();
        let record_error = restore_previous_record(&directory, &journal.previous_record).err();
        let _ = fs::remove_dir_all(&transaction_root);
        let _ = fs::remove_file(&journal_path);
        return Err(rollback_error.or(record_error).unwrap_or(error));
    }

    let record = InstallRecord {
        schema_version: 2,
        installation_id: installation_id(target),
        target: target.to_string_lossy().into_owned(),
        package_version: manifest.package_version.clone(),
        installed_at: unix_time(),
        source: old_record
            .as_ref()
            .map_or(detection.source, |record| record.source),
        restore_baseline: old_record
            .as_ref()
            .map_or(detection.restore_baseline, |record| record.restore_baseline),
        migrated_from_version: old_record
            .as_ref()
            .and_then(|record| record.migrated_from_version.clone())
            .or(detection.version),
        entries: record_entries.into_values().collect(),
    };
    write_json_atomic(&directory.join("record.json"), &record)?;
    journal.committed = true;
    write_json_atomic(&journal_path, &journal)?;
    let _ = fs::remove_dir_all(transaction_root);
    fs::remove_file(journal_path).map_err(AppError::transaction_io)?;
    Ok(InstallTransactionResult {
        package_version: manifest.package_version,
        installed_files,
        backup_path: directory.to_string_lossy().into_owned(),
        repaired,
    })
}

fn preserve_cosmetics(state_root: &Path, target: &Path, timestamp: u64) -> Result<Option<PathBuf>> {
    let source = target.join("addons/counterstrikesharp/plugins/PlayerKnifeCustomizer");
    let files = ["player_knife_presets.json", "player_gun_presets.json"];
    if !files.iter().any(|name| source.join(name).is_file()) {
        return Ok(None);
    }
    let destination = state_root.join("presets").join(timestamp.to_string());
    for name in files {
        if source.join(name).is_file() {
            copy_file(&source.join(name), &destination.join(name))?;
        }
    }
    Ok(Some(destination))
}

pub fn restore(payload_root: &Path, state_root: &Path, target: &Path) -> Result<RestoreResult> {
    let _transaction_lock = acquire_transaction_lock(target)?;
    recover_incomplete_locked(state_root, target)?;
    let timestamp = unix_time();
    let presets = preserve_cosmetics(state_root, target, timestamp)?;
    let directory = installation_dir(state_root, target);
    fs::create_dir_all(&directory).map_err(AppError::transaction_io)?;
    let previous_record = read_record(&directory);
    let transaction_name = format!("transaction-restore-{timestamp}");
    let transaction_root = directory.join(&transaction_name);
    let journal_path = directory.join("journal.json");
    let mut journal = TransactionJournal {
        schema_version: 1,
        operation: "restore".to_string(),
        target: target.to_string_lossy().into_owned(),
        transaction_dir: transaction_name.clone(),
        changes: Vec::new(),
        committed: false,
        previous_record: previous_record.clone(),
    };
    write_json_atomic(&journal_path, &journal)?;
    let mut restored_files = 0;
    let mut removed_files = 0;
    let mut preserved_files = 0;

    let mut stage = |relative: &Path, raw: &str, destination: &Path| -> Result<()> {
        let existed = destination.is_file();
        let backup = transaction_root.join(relative);
        if existed {
            copy_file(destination, &backup)?;
        }
        journal.changes.push(TransactionChange {
            path: raw.to_string(),
            existed,
            backup: existed.then(|| format!("{transaction_name}/{}", raw.replace('\\', "/"))),
        });
        write_json_atomic(&journal_path, &journal)
    };

    let outcome = (|| -> Result<()> {
        if let Some(record) = &previous_record {
            for entry in record.entries.iter().rev() {
                let relative = safe_relative(&entry.path)?;
                let destination = target.join(&relative);
                if entry.original_existed {
                    stage(&relative, &entry.path, &destination)?;
                    let backup = entry.original_backup.as_ref().ok_or_else(|| {
                        AppError::transaction(format!(
                            "Original backup missing from record: {}",
                            entry.path
                        ))
                    })?;
                    copy_file(&directory.join(backup), &destination)?;
                    restored_files += 1;
                } else if destination.is_file() {
                    stage(&relative, &entry.path, &destination)?;
                    if !sha256(&destination)?.eq_ignore_ascii_case(&entry.installed_sha256) {
                        let preserved = directory
                            .join("preserved-modified")
                            .join(timestamp.to_string())
                            .join(&relative);
                        copy_file(&destination, &preserved)?;
                        preserved_files += 1;
                    }
                    fs::remove_file(destination).map_err(AppError::transaction_io)?;
                    removed_files += 1;
                }
            }
            fs::remove_file(directory.join("record.json")).map_err(AppError::transaction_io)?;
        } else {
            let manifest = read_manifest_for_target(payload_root, target)?;
            for entry in manifest
                .entries
                .iter()
                .filter(|entry| entry.ownership == "plus")
            {
                let destination = target.join(safe_relative(&entry.path)?);
                if destination.is_file()
                    && sha256(&destination)?.eq_ignore_ascii_case(&entry.sha256)
                {
                    let relative = safe_relative(&entry.path)?;
                    stage(&relative, &entry.path, &destination)?;
                    fs::remove_file(destination).map_err(AppError::transaction_io)?;
                    removed_files += 1;
                }
            }
        }
        Ok(())
    })();
    drop(stage);

    if let Err(error) = outcome {
        let rollback_error = rollback(&directory, &journal).err();
        let record_error = restore_previous_record(&directory, &journal.previous_record).err();
        let _ = fs::remove_dir_all(&transaction_root);
        let _ = fs::remove_file(&journal_path);
        return Err(rollback_error.or(record_error).unwrap_or(error));
    }

    journal.committed = true;
    write_json_atomic(&journal_path, &journal)?;
    let _ = fs::remove_dir_all(&transaction_root);
    fs::remove_file(&journal_path).map_err(AppError::transaction_io)?;

    Ok(RestoreResult {
        restored_files,
        removed_files,
        preserved_files,
        presets_backup: presets.map(|path| path.to_string_lossy().into_owned()),
        steam_verify_uri: "steam://validate/730".to_string(),
        result_kind: "restore_previous".to_string(),
    })
}

fn collect_tree_files(
    root: &Path,
    target: &Path,
    files: &mut BTreeMap<String, PathBuf>,
) -> Result<()> {
    if !root.is_dir() {
        return Ok(());
    }
    for entry in fs::read_dir(root).map_err(AppError::transaction_io)? {
        let entry = entry.map_err(AppError::transaction_io)?;
        let path = entry.path();
        let file_type = entry.file_type().map_err(AppError::transaction_io)?;
        if file_type.is_dir() {
            collect_tree_files(&path, target, files)?;
        } else if file_type.is_file() {
            let relative = path.strip_prefix(target).map_err(|_| {
                AppError::transaction(format!(
                    "Cleanup path escaped the selected CS2 directory: {}",
                    path.display()
                ))
            })?;
            let raw = relative.to_string_lossy().replace('\\', "/");
            files.insert(raw, path);
        }
    }
    Ok(())
}

fn remove_empty_tree(path: &Path) -> Result<bool> {
    if !path.is_dir() {
        return Ok(false);
    }
    for entry in fs::read_dir(path).map_err(AppError::transaction_io)? {
        let entry = entry.map_err(AppError::transaction_io)?;
        if entry
            .file_type()
            .map_err(AppError::transaction_io)?
            .is_dir()
        {
            let _ = remove_empty_tree(&entry.path())?;
        }
    }
    if fs::read_dir(path)
        .map_err(AppError::transaction_io)?
        .next()
        .is_none()
    {
        fs::remove_dir(path).map_err(AppError::transaction_io)?;
        Ok(true)
    } else {
        Ok(false)
    }
}

pub fn restore_pristine(
    payload_root: &Path,
    state_root: &Path,
    target: &Path,
) -> Result<RestoreResult> {
    let _transaction_lock = acquire_transaction_lock(target)?;
    recover_incomplete_locked(state_root, target)?;
    let timestamp = unix_time();
    let presets = preserve_cosmetics(state_root, target, timestamp)?;
    let directory = installation_dir(state_root, target);
    fs::create_dir_all(&directory).map_err(AppError::transaction_io)?;
    let previous_record = read_record(&directory);
    let manifest = read_manifest_document(payload_root)?;

    let mut expected = BTreeMap::new();
    for entry in &manifest.entries {
        expected.insert(entry.path.replace('\\', "/"), entry.sha256.clone());
    }
    if let Some(record) = &previous_record {
        for entry in &record.entries {
            expected.insert(
                entry.path.replace('\\', "/"),
                entry.installed_sha256.clone(),
            );
        }
    }

    let mut candidates = BTreeMap::new();
    for raw in expected.keys() {
        let relative = safe_relative(raw)?;
        let canonical = target.join(&relative);
        if canonical.is_file() {
            candidates.insert(
                relative.to_string_lossy().replace('\\', "/"),
                canonical.clone(),
            );
        }
        let disabled = mode_layout::disabled_path(&canonical);
        if disabled.is_file() {
            let disabled_relative = disabled.strip_prefix(target).map_err(|_| {
                AppError::transaction(format!(
                    "Disabled cleanup path escaped the selected CS2 directory: {}",
                    disabled.display()
                ))
            })?;
            candidates.insert(
                disabled_relative.to_string_lossy().replace('\\', "/"),
                disabled,
            );
        }
    }
    for raw in SUITE_OWNED_FILES {
        let relative = safe_relative(raw)?;
        let canonical = target.join(&relative);
        if canonical.is_file() {
            candidates.insert(
                relative.to_string_lossy().replace('\\', "/"),
                canonical.clone(),
            );
        }
        let disabled = mode_layout::disabled_path(&canonical);
        if disabled.is_file() {
            let disabled_relative = disabled.strip_prefix(target).map_err(|_| {
                AppError::transaction(format!(
                    "Disabled cleanup path escaped the selected CS2 directory: {}",
                    disabled.display()
                ))
            })?;
            candidates.insert(
                disabled_relative.to_string_lossy().replace('\\', "/"),
                disabled,
            );
        }
    }
    for raw in SUITE_OWNED_ROOTS {
        collect_tree_files(
            &target.join(raw.replace('/', "\\")),
            target,
            &mut candidates,
        )?;
    }

    let transaction_name = format!("transaction-pristine-{timestamp}");
    let transaction_root = directory.join(&transaction_name);
    let journal_path = directory.join("journal.json");
    let mut journal = TransactionJournal {
        schema_version: 1,
        operation: "restore-pristine".to_string(),
        target: target.to_string_lossy().into_owned(),
        transaction_dir: transaction_name.clone(),
        changes: Vec::new(),
        committed: false,
        previous_record: previous_record.clone(),
    };
    write_json_atomic(&journal_path, &journal)?;
    let preserved_root = directory
        .join("preserved-removed")
        .join(timestamp.to_string());
    let mut removed_files = 0;
    let mut preserved_files = 0;

    let outcome = (|| -> Result<()> {
        for (raw, destination) in &candidates {
            if !destination.is_file() {
                continue;
            }
            let relative = safe_relative(raw)?;
            let backup = transaction_root.join(&relative);
            copy_file(destination, &backup)?;
            journal.changes.push(TransactionChange {
                path: raw.clone(),
                existed: true,
                backup: Some(format!("{transaction_name}/{}", raw.replace('\\', "/"))),
            });
            write_json_atomic(&journal_path, &journal)?;

            let canonical_raw = raw.strip_suffix(".csbip-disabled").unwrap_or(raw);
            let known_unchanged = expected.get(canonical_raw).is_some_and(|hash| {
                sha256(destination)
                    .map(|actual| actual.eq_ignore_ascii_case(hash))
                    .unwrap_or(false)
            });
            if !known_unchanged
                && !raw.ends_with("player_knife_presets.json")
                && !raw.ends_with("player_gun_presets.json")
            {
                copy_file(destination, &preserved_root.join(&relative))?;
                preserved_files += 1;
            }
            fs::remove_file(destination).map_err(AppError::transaction_io)?;
            removed_files += 1;
        }
        Ok(())
    })();

    if let Err(error) = outcome {
        let rollback_error = rollback(&directory, &journal).err();
        let record_error = restore_previous_record(&directory, &journal.previous_record).err();
        let _ = fs::remove_dir_all(&transaction_root);
        let _ = fs::remove_file(&journal_path);
        return Err(rollback_error.or(record_error).unwrap_or(error));
    }

    if directory.join("record.json").is_file() {
        fs::remove_file(directory.join("record.json")).map_err(AppError::transaction_io)?;
    }
    journal.committed = true;
    write_json_atomic(&journal_path, &journal)?;
    let _ = fs::remove_dir_all(&transaction_root);
    fs::remove_file(&journal_path).map_err(AppError::transaction_io)?;
    for raw in SUITE_OWNED_ROOTS.iter().rev() {
        let _ = remove_empty_tree(&target.join(raw.replace('/', "\\")));
    }

    Ok(RestoreResult {
        restored_files: 0,
        removed_files,
        preserved_files,
        presets_backup: presets.map(|path| path.to_string_lossy().into_owned()),
        steam_verify_uri: "steam://validate/730".to_string(),
        result_kind: "pristine".to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn root(name: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!("cs2bi-installer-{name}-{}", unix_time()));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).unwrap();
        path
    }

    fn fixture(payload: &Path, target: &Path) {
        fs::create_dir_all(payload.join("cfg")).unwrap();
        fs::create_dir_all(target.join("cfg")).unwrap();
        fs::write(payload.join("cfg/test.cfg"), b"plus").unwrap();
        fs::write(target.join("cfg/test.cfg"), b"steam").unwrap();
        fs::write(target.join("cfg/foreign.cfg"), b"foreign").unwrap();
        let hash = sha256(&payload.join("cfg/test.cfg")).unwrap();
        write_json_atomic(
            &payload.join(MANIFEST_FILE),
            &PayloadManifest {
                schema_version: 1,
                package_version: "1.4.2.4".to_string(),
                entries: vec![PayloadEntry {
                    path: "cfg/test.cfg".to_string(),
                    size: 4,
                    sha256: hash,
                    component: "config".to_string(),
                    ownership: "plus".to_string(),
                    restore_policy: "restore".to_string(),
                }],
            },
        )
        .unwrap();
    }

    fn cosmetics_fixture(payload: &Path, target: &Path) {
        fixture(payload, target);
        let directory = payload.join("addons/counterstrikesharp/plugins/PlayerKnifeCustomizer");
        fs::create_dir_all(&directory).unwrap();
        let mut manifest = read_manifest_document(payload).unwrap();
        for (name, bytes) in [
            ("player_knife_presets.json", b"packaged-knives".as_slice()),
            ("player_gun_presets.json", b"packaged-guns".as_slice()),
        ] {
            let source = directory.join(name);
            fs::write(&source, bytes).unwrap();
            manifest.entries.push(PayloadEntry {
                path: format!("addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/{name}"),
                size: bytes.len() as u64,
                sha256: sha256(&source).unwrap(),
                component: "player-cosmetics".to_string(),
                ownership: "plus".to_string(),
                restore_policy: "preserve-config".to_string(),
            });
        }
        write_json_atomic(&payload.join(MANIFEST_FILE), &manifest).unwrap();
    }

    #[test]
    fn same_root_allows_modified_preserve_config_but_rejects_other_payload_changes() {
        let base = root("same-payload-target");
        let target = base.join("game/csgo");
        let state = base.join("state");
        cosmetics_fixture(&target, &target);
        let mut manifest = read_manifest_document(&target).unwrap();
        let cfg = manifest
            .entries
            .iter_mut()
            .find(|entry| entry.path == "cfg/test.cfg")
            .unwrap();
        cfg.size = fs::metadata(target.join("cfg/test.cfg")).unwrap().len();
        cfg.sha256 = sha256(&target.join("cfg/test.cfg")).unwrap();
        write_json_atomic(&target.join(MANIFEST_FILE), &manifest).unwrap();
        let gun_presets = target.join(
            "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/player_gun_presets.json",
        );
        fs::write(&gun_presets, b"player-modified-guns").unwrap();

        assert!(verify_payload(&target).is_err());
        verify_payload_for_target(&target, &target).unwrap();
        assert!(plan(&target, &state, &target).is_ok());
        assert!(install(&target, &state, &target, false).is_ok());
        assert_eq!(fs::read(&gun_presets).unwrap(), b"player-modified-guns");

        fs::write(target.join("cfg/test.cfg"), b"damaged").unwrap();
        assert!(verify_payload_for_target(&target, &target).is_err());
        fs::remove_dir_all(base).unwrap();
    }

    fn add_payload_file(payload: &Path, raw: &str, bytes: &[u8]) {
        let path = payload.join(raw.replace('/', "\\"));
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, bytes).unwrap();
        let mut manifest = read_manifest_document(payload).unwrap();
        manifest.entries.push(PayloadEntry {
            path: raw.to_string(),
            size: bytes.len() as u64,
            sha256: sha256(&path).unwrap(),
            component: "match-rating".to_string(),
            ownership: "plus".to_string(),
            restore_policy: "restore".to_string(),
        });
        write_json_atomic(&payload.join(MANIFEST_FILE), &manifest).unwrap();
    }

    fn marker(target: &Path, raw: &str) {
        let path = target.join(raw.replace('/', "\\"));
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, b"marker").unwrap();
    }

    #[test]
    fn distinguishes_clean_legacy_plus_upstream_mixed_and_managed_installs() {
        let base = root("sources");
        let payload = base.join("payload");
        let state = base.join("state");

        let clean = base.join("clean");
        fixture(&payload, &clean);
        assert_eq!(
            inspect(&payload, &state, &clean).unwrap().source,
            InstallationSource::Clean
        );

        let legacy = base.join("legacy");
        fs::create_dir_all(&legacy).unwrap();
        marker(&legacy, PLUS_MARKERS[1]);
        let legacy_inspection = inspect(&payload, &state, &legacy).unwrap();
        assert_eq!(legacy_inspection.source, InstallationSource::LegacyPlus);
        assert_eq!(
            legacy_inspection.migration_kind,
            MigrationKind::AdoptLegacyPlus
        );

        let upstream = base.join("upstream");
        fs::create_dir_all(&upstream).unwrap();
        for raw in UPSTREAM_MARKERS.iter().take(3) {
            marker(&upstream, raw);
        }
        let upstream_inspection = inspect(&payload, &state, &upstream).unwrap();
        assert_eq!(upstream_inspection.source, InstallationSource::Upstream);
        assert_eq!(
            upstream_inspection.migration_kind,
            MigrationKind::ReplaceUpstream
        );

        let mixed = base.join("mixed");
        fs::create_dir_all(&mixed).unwrap();
        marker(&mixed, UPSTREAM_MARKERS[0]);
        let mixed_inspection = inspect(&payload, &state, &mixed).unwrap();
        assert_eq!(mixed_inspection.source, InstallationSource::MixedUnknown);
        assert!(!mixed_inspection.can_install);
        assert!(install(&payload, &state, &mixed, false).is_err());

        install(&payload, &state, &clean, false).unwrap();
        let managed = inspect(&payload, &state, &clean).unwrap();
        assert_eq!(managed.source, InstallationSource::ManagedPlus);
        assert_eq!(managed.source_version.as_deref(), Some("1.4.2.4"));
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn managed_upgrade_replaces_the_unchanged_rating_plus_model_with_open_rating() {
        let base = root("open-rating-upgrade");
        let old_payload = base.join("old-payload");
        let new_payload = base.join("new-payload");
        let target = base.join("target");
        let state = base.join("state");
        fixture(&old_payload, &target);
        add_payload_file(&old_payload, SUPERSEDED_PAYLOAD_PATHS[0].0, b"old-model");
        install(&old_payload, &state, &target, false).unwrap();

        fixture(&new_payload, &base.join("new-fixture-target"));
        add_payload_file(&new_payload, SUPERSEDED_PAYLOAD_PATHS[0].1, b"open-model");
        install(&new_payload, &state, &target, false).unwrap();

        assert!(!target.join(SUPERSEDED_PAYLOAD_PATHS[0].0.replace('/', "\\")).exists());
        assert_eq!(
            fs::read(target.join(SUPERSEDED_PAYLOAD_PATHS[0].1.replace('/', "\\"))).unwrap(),
            b"open-model"
        );
        let record = read_record(&installation_dir(&state, &target)).unwrap();
        assert!(!record.entries.iter().any(|entry| entry.path == SUPERSEDED_PAYLOAD_PATHS[0].0));
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn managed_upgrade_restores_a_pre_plus_file_at_the_superseded_model_path() {
        let base = root("open-rating-original-restore");
        let old_payload = base.join("old-payload");
        let new_payload = base.join("new-payload");
        let target = base.join("target");
        let state = base.join("state");
        fixture(&old_payload, &target);
        let old_model = target.join(SUPERSEDED_PAYLOAD_PATHS[0].0.replace('/', "\\"));
        fs::create_dir_all(old_model.parent().unwrap()).unwrap();
        fs::write(&old_model, b"pre-plus-model").unwrap();
        add_payload_file(&old_payload, SUPERSEDED_PAYLOAD_PATHS[0].0, b"old-model");
        install(&old_payload, &state, &target, false).unwrap();

        fixture(&new_payload, &base.join("new-fixture-target"));
        add_payload_file(&new_payload, SUPERSEDED_PAYLOAD_PATHS[0].1, b"open-model");
        install(&new_payload, &state, &target, false).unwrap();

        assert_eq!(fs::read(&old_model).unwrap(), b"pre-plus-model");
        assert!(target.join(SUPERSEDED_PAYLOAD_PATHS[0].1.replace('/', "\\")).is_file());
        let record = read_record(&installation_dir(&state, &target)).unwrap();
        assert!(!record.entries.iter().any(|entry| entry.path == SUPERSEDED_PAYLOAD_PATHS[0].0));
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn open_rating_install_preserves_an_untracked_legacy_model() {
        let base = root("open-rating-untracked-file");
        let payload = base.join("payload");
        let target = base.join("target");
        let state = base.join("state");
        fixture(&payload, &target);
        let old_model = target.join(SUPERSEDED_PAYLOAD_PATHS[0].0.replace('/', "\\"));
        fs::create_dir_all(old_model.parent().unwrap()).unwrap();
        fs::write(&old_model, b"untracked-model").unwrap();
        add_payload_file(&payload, SUPERSEDED_PAYLOAD_PATHS[0].1, b"open-model");

        install(&payload, &state, &target, false).unwrap();

        assert_eq!(fs::read(old_model).unwrap(), b"untracked-model");
        assert!(target.join(SUPERSEDED_PAYLOAD_PATHS[0].1.replace('/', "\\")).is_file());
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn managed_upgrade_preserves_a_modified_legacy_rating_model() {
        let base = root("open-rating-user-file");
        let old_payload = base.join("old-payload");
        let new_payload = base.join("new-payload");
        let target = base.join("target");
        let state = base.join("state");
        fixture(&old_payload, &target);
        add_payload_file(&old_payload, SUPERSEDED_PAYLOAD_PATHS[0].0, b"old-model");
        install(&old_payload, &state, &target, false).unwrap();
        let old_model = target.join(SUPERSEDED_PAYLOAD_PATHS[0].0.replace('/', "\\"));
        fs::write(&old_model, b"user-modified-model").unwrap();

        fixture(&new_payload, &base.join("new-fixture-target"));
        add_payload_file(&new_payload, SUPERSEDED_PAYLOAD_PATHS[0].1, b"open-model");
        install(&new_payload, &state, &target, false).unwrap();

        assert_eq!(fs::read(old_model).unwrap(), b"user-modified-model");
        assert!(target.join(SUPERSEDED_PAYLOAD_PATHS[0].1.replace('/', "\\")).is_file());
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn legacy_plus_adoption_records_a_pre_migration_restore_baseline() {
        let base = root("legacy-baseline");
        let payload = base.join("payload");
        let target = base.join("target");
        let state = base.join("state");
        cosmetics_fixture(&payload, &target);
        let existing = target.join(PLUS_MARKERS[1].replace('/', "\\"));
        fs::create_dir_all(existing.parent().unwrap()).unwrap();
        fs::write(&existing, b"legacy-player-knives").unwrap();

        install(&payload, &state, &target, false).unwrap();

        let record = read_record(&installation_dir(&state, &target)).unwrap();
        assert_eq!(record.source, InstallationSource::LegacyPlus);
        assert_eq!(record.restore_baseline, RestoreBaseline::PreMigration);
        assert_eq!(fs::read(existing).unwrap(), b"legacy-player-knives");
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn pristine_restore_removes_suite_files_but_keeps_third_party_files() {
        let base = root("pristine");
        let payload = base.join("payload");
        let target = base.join("target");
        let state = base.join("state");
        cosmetics_fixture(&payload, &target);
        install(&payload, &state, &target, false).unwrap();
        let cosmetics = target.join("addons/counterstrikesharp/plugins/PlayerKnifeCustomizer");
        fs::write(
            cosmetics.join("player_knife_presets.json"),
            b"player-knives",
        )
        .unwrap();
        let third_party = target.join("addons/counterstrikesharp/plugins/ThirdParty/keep.dll");
        fs::create_dir_all(third_party.parent().unwrap()).unwrap();
        fs::write(&third_party, b"third-party").unwrap();

        let result = restore_pristine(&payload, &state, &target).unwrap();

        assert!(result.removed_files >= 3);
        assert_eq!(result.result_kind, "pristine");
        assert!(!target.join("cfg/test.cfg").exists());
        assert!(!cosmetics.exists());
        assert_eq!(fs::read(&third_party).unwrap(), b"third-party");
        let presets = PathBuf::from(result.presets_backup.unwrap());
        assert_eq!(
            fs::read(presets.join("player_knife_presets.json")).unwrap(),
            b"player-knives"
        );
        assert!(!inspect(&payload, &state, &target).unwrap().installed);
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn install_repair_and_restore_preserve_originals_and_foreign_files() {
        let base = root("roundtrip");
        let payload = base.join("payload");
        let target = base.join("target");
        let state = base.join("state");
        fixture(&payload, &target);

        install(&payload, &state, &target, false).unwrap();
        assert_eq!(fs::read(target.join("cfg/test.cfg")).unwrap(), b"plus");
        fs::write(target.join("cfg/test.cfg"), b"broken").unwrap();
        assert_eq!(inspect(&payload, &state, &target).unwrap().corrupt.len(), 1);
        let repaired = install(&payload, &state, &target, true).unwrap();
        assert_eq!(repaired.installed_files, 1);
        let unchanged = install(&payload, &state, &target, true).unwrap();
        assert_eq!(unchanged.installed_files, 0);
        restore(&payload, &state, &target).unwrap();
        assert_eq!(fs::read(target.join("cfg/test.cfg")).unwrap(), b"steam");
        assert_eq!(
            fs::read(target.join("cfg/foreign.cfg")).unwrap(),
            b"foreign"
        );
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn modified_preserve_config_is_healthy_and_repair_keeps_player_bytes() {
        let base = root("preserve-modified");
        let payload = base.join("payload");
        let target = base.join("target");
        let state = base.join("state");
        cosmetics_fixture(&payload, &target);
        install(&payload, &state, &target, false).unwrap();

        let directory = target.join("addons/counterstrikesharp/plugins/PlayerKnifeCustomizer");
        let knife = directory.join("player_knife_presets.json");
        let guns = directory.join("player_gun_presets.json");
        fs::write(&knife, b"player-knives").unwrap();
        fs::write(&guns, b"player-guns").unwrap();

        let inspection = inspect(&payload, &state, &target).unwrap();
        assert!(inspection.missing.is_empty());
        assert!(inspection.corrupt.is_empty());
        assert_eq!(inspection.healthy, inspection.total);
        assert_eq!(
            install(&payload, &state, &target, true)
                .unwrap()
                .installed_files,
            0
        );
        assert_eq!(fs::read(&knife).unwrap(), b"player-knives");
        assert_eq!(fs::read(&guns).unwrap(), b"player-guns");
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn first_install_preserves_existing_manual_cosmetics() {
        let base = root("preserve-manual-upgrade");
        let payload = base.join("payload");
        let target = base.join("target");
        let state = base.join("state");
        cosmetics_fixture(&payload, &target);
        let directory = target.join("addons/counterstrikesharp/plugins/PlayerKnifeCustomizer");
        fs::create_dir_all(&directory).unwrap();
        let knife = directory.join("player_knife_presets.json");
        let guns = directory.join("player_gun_presets.json");
        fs::write(&knife, b"legacy-player-knives").unwrap();
        fs::write(&guns, b"legacy-player-guns").unwrap();

        install(&payload, &state, &target, false).unwrap();
        assert_eq!(fs::read(&knife).unwrap(), b"legacy-player-knives");
        assert_eq!(fs::read(&guns).unwrap(), b"legacy-player-guns");
        let inspection = inspect(&payload, &state, &target).unwrap();
        assert!(inspection.corrupt.is_empty());
        assert_eq!(inspection.healthy, inspection.total);
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn repair_restores_missing_preserve_config_from_current_mirror() {
        let base = root("preserve-mirror");
        let payload = base.join("payload");
        let target = base.join("target");
        let state = base.join("state");
        cosmetics_fixture(&payload, &target);
        install(&payload, &state, &target, false).unwrap();

        let relative = safe_relative(
            "addons/counterstrikesharp/plugins/PlayerKnifeCustomizer/player_knife_presets.json",
        )
        .unwrap();
        let destination = target.join(&relative);
        fs::create_dir_all(state.join("presets/current")).unwrap();
        fs::write(
            state.join("presets/current/player_knife_presets.json"),
            b"mirrored-player-knives",
        )
        .unwrap();
        fs::remove_file(&destination).unwrap();

        let inspection = inspect(&payload, &state, &target).unwrap();
        assert_eq!(
            inspection.missing,
            vec![relative.to_string_lossy().replace('\\', "/")]
        );
        assert_eq!(
            install(&payload, &state, &target, true)
                .unwrap()
                .installed_files,
            1
        );
        assert_eq!(fs::read(&destination).unwrap(), b"mirrored-player-knives");
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    #[ignore = "requires CS2BI_REAL_PAYLOAD to point at a packaged payload"]
    fn packaged_payload_install_repair_restore_roundtrip() {
        let payload = PathBuf::from(
            std::env::var("CS2BI_REAL_PAYLOAD").expect("CS2BI_REAL_PAYLOAD is required"),
        );
        let manifest = read_manifest(&payload).unwrap();
        let base = root("packaged-roundtrip");
        let target = base.join("target");
        let state = base.join("state");
        fs::create_dir_all(&target).unwrap();

        let original = manifest
            .entries
            .iter()
            .find(|entry| {
                !entry.path.ends_with("player_knife_presets.json")
                    && !entry.path.ends_with("player_gun_presets.json")
            })
            .expect("payload needs a restorable file");
        let original_path = target.join(safe_relative(&original.path).unwrap());
        fs::create_dir_all(original_path.parent().unwrap()).unwrap();
        fs::write(&original_path, b"steam-original").unwrap();
        let foreign = target.join("addons/third-party/keep.txt");
        fs::create_dir_all(foreign.parent().unwrap()).unwrap();
        fs::write(&foreign, b"foreign").unwrap();

        let planned = plan(&payload, &state, &target).unwrap();
        assert_eq!(planned.total_files, manifest.entries.len());
        assert_eq!(planned.overwritten_files, 1);
        install(&payload, &state, &target, false).unwrap();
        let installed = inspect(&payload, &state, &target).unwrap();
        assert_eq!(installed.healthy, installed.total);

        let knife = manifest
            .entries
            .iter()
            .find(|entry| entry.path.ends_with("player_knife_presets.json"))
            .expect("packaged knife preset is missing");
        let guns = manifest
            .entries
            .iter()
            .find(|entry| entry.path.ends_with("player_gun_presets.json"))
            .expect("packaged gun preset is missing");
        let knife_path = target.join(safe_relative(&knife.path).unwrap());
        let gun_path = target.join(safe_relative(&guns.path).unwrap());
        fs::write(&knife_path, b"player-modified-knives").unwrap();
        fs::write(&gun_path, b"player-modified-guns").unwrap();
        let customized = inspect(&payload, &state, &target).unwrap();
        assert!(customized.corrupt.is_empty());
        assert_eq!(customized.healthy, customized.total);

        let damaged = manifest
            .entries
            .iter()
            .find(|entry| entry.path != original.path && entry.restore_policy != "preserve-config")
            .expect("payload needs at least two restorable files");
        fs::write(
            target.join(safe_relative(&damaged.path).unwrap()),
            b"damaged",
        )
        .unwrap();
        assert_eq!(inspect(&payload, &state, &target).unwrap().corrupt.len(), 1);
        assert_eq!(
            install(&payload, &state, &target, true)
                .unwrap()
                .installed_files,
            1
        );
        assert_eq!(fs::read(&knife_path).unwrap(), b"player-modified-knives");
        assert_eq!(fs::read(&gun_path).unwrap(), b"player-modified-guns");

        let restored = restore(&payload, &state, &target).unwrap();
        assert_eq!(fs::read(&original_path).unwrap(), b"steam-original");
        assert_eq!(fs::read(&foreign).unwrap(), b"foreign");
        assert!(!knife_path.exists());
        let preset_backup =
            PathBuf::from(restored.presets_backup.expect("preset backup is required"));
        assert_eq!(
            fs::read(preset_backup.join("player_knife_presets.json")).unwrap(),
            b"player-modified-knives"
        );
        assert_eq!(
            fs::read(preset_backup.join("player_gun_presets.json")).unwrap(),
            b"player-modified-guns"
        );
        assert!(!inspect(&payload, &state, &target).unwrap().installed);
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn rejects_parent_directory_payload_paths() {
        assert!(safe_relative("../gameinfo.gi").is_err());
        assert!(safe_relative(r"C:\\game\\csgo").is_err());
    }

    #[test]
    fn panel_only_update_package_has_an_actionable_first_install_error() {
        let base = root("panel-only-marker");
        let payload = base.join("panel-only");
        fs::create_dir_all(&payload).unwrap();
        fs::write(payload.join(PANEL_UPDATE_MARKER), b"{}").unwrap();

        let error = read_manifest_document(&payload).unwrap_err();

        assert_eq!(error.code, "E1301");
        assert!(error.detail.contains("Panel-only online-update component"));
        assert!(error.detail.contains(&format!(
            "LocalArena-v{}-windows.zip",
            crate::app_version::display()
        )));
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn active_installation_is_not_rolled_back_by_snapshot_recovery() {
        let base = root("active-install-recovery");
        let payload = base.join("payload");
        let target = base.join("target");
        let state = base.join("state");
        let mut entries = Vec::new();
        for index in 0..512 {
            let relative = format!("addons/runtime/file-{index:04}.bin");
            let source = payload.join(safe_relative(&relative).unwrap());
            let destination = target.join(safe_relative(&relative).unwrap());
            fs::create_dir_all(source.parent().unwrap()).unwrap();
            fs::create_dir_all(destination.parent().unwrap()).unwrap();
            let bytes = vec![(index % 251) as u8; 4096];
            fs::write(&source, &bytes).unwrap();
            fs::write(&destination, b"legacy").unwrap();
            entries.push(PayloadEntry {
                path: relative,
                size: bytes.len() as u64,
                sha256: sha256(&source).unwrap(),
                component: "runtime".to_string(),
                ownership: "shared".to_string(),
                restore_policy: "restore".to_string(),
            });
        }
        write_json_atomic(
            &payload.join(MANIFEST_FILE),
            &PayloadManifest {
                schema_version: 1,
                package_version: "1.4.2.4".to_string(),
                entries,
            },
        )
        .unwrap();

        let install_payload = payload.clone();
        let install_target = target.clone();
        let install_state = state.clone();
        let worker = std::thread::spawn(move || {
            install(&install_payload, &install_state, &install_target, false)
        });

        let journal = installation_dir(&state, &target).join("journal.json");
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
        while !journal.is_file() && std::time::Instant::now() < deadline {
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        assert!(
            journal.is_file(),
            "the installation did not expose an active journal"
        );
        assert!(
            !recover_incomplete(&state, &target).unwrap(),
            "snapshot recovery must not roll back an active installation"
        );

        worker.join().unwrap().unwrap();
        assert!(!journal.exists());
        assert_eq!(inspect(&payload, &state, &target).unwrap().healthy, 512);
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn transient_file_disappearance_is_retried_with_contextual_failures() {
        let base = root("transient-copy");
        let source = base.join("late/source.bin");
        let destination = base.join("destination/copied.bin");
        let late_source = source.clone();
        let creator = std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_millis(90));
            fs::create_dir_all(late_source.parent().unwrap()).unwrap();
            fs::write(late_source, b"available-after-scan").unwrap();
        });

        copy_file_for("payload staging", &source, &destination).unwrap();
        creator.join().unwrap();
        assert_eq!(fs::read(&destination).unwrap(), b"available-after-scan");

        let missing = base.join("permanently-missing.bin");
        let failed_destination = base.join("failed/output.bin");
        let error = copy_file_for("original backup", &missing, &failed_destination).unwrap_err();
        assert!(error.detail.contains("original backup"));
        assert!(
            error
                .detail
                .contains(&missing.to_string_lossy().to_string())
        );
        assert!(
            error
                .detail
                .contains(&failed_destination.to_string_lossy().to_string())
        );
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn interrupted_transaction_restores_files_and_previous_record_state() {
        let base = root("interrupted");
        let payload = base.join("payload");
        let target = base.join("target");
        let state = base.join("state");
        fixture(&payload, &target);
        let directory = installation_dir(&state, &target);
        let transaction = directory.join("transaction-test");
        fs::create_dir_all(transaction.join("cfg")).unwrap();
        fs::copy(
            target.join("cfg/test.cfg"),
            transaction.join("cfg/test.cfg"),
        )
        .unwrap();
        fs::write(target.join("cfg/test.cfg"), b"partial").unwrap();
        fs::create_dir_all(&directory).unwrap();
        write_json_atomic(
            &directory.join("journal.json"),
            &TransactionJournal {
                schema_version: 1,
                operation: "install".to_string(),
                target: target.to_string_lossy().into_owned(),
                transaction_dir: "transaction-test".to_string(),
                changes: vec![TransactionChange {
                    path: "cfg/test.cfg".to_string(),
                    existed: true,
                    backup: Some("transaction-test/cfg/test.cfg".to_string()),
                }],
                committed: false,
                previous_record: None,
            },
        )
        .unwrap();

        assert!(recover_incomplete(&state, &target).unwrap());
        assert_eq!(fs::read(target.join("cfg/test.cfg")).unwrap(), b"steam");
        assert!(!directory.join("record.json").exists());
        assert!(!directory.join("journal.json").exists());
        fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn failed_restore_rolls_back_to_installed_state_and_record() {
        let base = root("restore-rollback");
        let payload = base.join("payload");
        let target = base.join("target");
        let state = base.join("state");
        fixture(&payload, &target);
        install(&payload, &state, &target, false).unwrap();
        let directory = installation_dir(&state, &target);
        fs::remove_file(directory.join("original/cfg/test.cfg")).unwrap();

        assert!(restore(&payload, &state, &target).is_err());
        assert_eq!(fs::read(target.join("cfg/test.cfg")).unwrap(), b"plus");
        assert!(directory.join("record.json").is_file());
        assert!(!directory.join("journal.json").exists());
        fs::remove_dir_all(base).unwrap();
    }
}
