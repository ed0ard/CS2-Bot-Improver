use crate::{AppError, Result, app_storage, app_version, atomic_fs, installer, logging, update_core};
use serde::{Deserialize, Serialize};
use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Emitter};

pub const MANIFEST_URL: &str =
    "https://github.com/numakkiyu/Local-Arena/releases/latest/download/latest.json";
const SIGNATURE_URL: &str =
    "https://github.com/numakkiyu/Local-Arena/releases/latest/download/latest.json.sig";
const UPDATE_PUBLIC_KEY: &str = "RbIjlfASpYVu740SsmQMLuLO7ExxiDBYTdnYThfqU/4=";
const CACHE_SECONDS: u64 = 6 * 60 * 60;
const REQUEST_TIMEOUT: Duration = Duration::from_secs(30);
const DOWNLOAD_TIMEOUT: Duration = Duration::from_secs(15 * 60);
const PANEL_EXECUTABLE_NAME: &str = "LocalArena.exe";
const LEGACY_PANEL_EXECUTABLE_NAME: &str = "CS2BotImproverPlus.exe";

static CANCELLED: AtomicBool = AtomicBool::new(false);
static RUNTIME: OnceLock<Mutex<RuntimeState>> = OnceLock::new();

#[derive(Clone, Debug, Default)]
struct RuntimeState {
    busy: bool,
    progress: Option<UpdateProgress>,
    last_error: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
pub struct UpdateComponentState {
    pub current_version: String,
    pub latest_version: Option<String>,
    pub update_available: bool,
    pub compatible: bool,
    pub status: String,
    pub downloaded_bytes: u64,
    pub total_bytes: u64,
    pub error: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
pub struct OnlineUpdateSnapshot {
    pub checked_at: Option<u64>,
    pub release_version: Option<String>,
    pub release_notes_url: Option<String>,
    pub panel: UpdateComponentState,
    pub plugin: UpdateComponentState,
    pub busy: bool,
    pub error: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct UpdateProgress {
    pub component: String,
    pub stage: String,
    pub downloaded_bytes: u64,
    pub total_bytes: u64,
}

#[derive(Clone, Debug, Serialize)]
pub struct UpdateResult {
    pub component: String,
    pub version: String,
    pub installed: bool,
    pub restart_required: bool,
    pub rollback_succeeded: Option<bool>,
    pub detail: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct UpdateBatchResult {
    pub panel: Option<UpdateResult>,
    pub plugin: Option<UpdateResult>,
    pub restart_required: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct CacheMetadata {
    schema_version: u8,
    checked_at: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct ActivePayload {
    schema_version: u8,
    version: String,
    path: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct PanelUpdatePlan {
    schema_version: u8,
    old_pid: u32,
    current: String,
    target: String,
    staged: String,
    backup: String,
}

fn runtime() -> &'static Mutex<RuntimeState> {
    RUNTIME.get_or_init(|| Mutex::new(RuntimeState::default()))
}

fn unix_time() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

fn update_root() -> Result<PathBuf> {
    let root = app_storage::root()?.join("updates");
    fs::create_dir_all(&root).map_err(AppError::transaction_io)?;
    Ok(root)
}

fn client(timeout: Duration) -> Result<reqwest::blocking::Client> {
    reqwest::blocking::Client::builder()
        .timeout(timeout)
        .user_agent(format!("LocalArena/{}", app_version::display()))
        .https_only(true)
        .build()
        .map_err(|error| AppError::update(format!("Cannot initialize update client: {error}")))
}

fn read_limited_response(mut response: reqwest::blocking::Response, limit: u64) -> Result<Vec<u8>> {
    if !response.status().is_success() {
        return Err(AppError::update(format!(
            "GitHub update request failed with HTTP {}",
            response.status()
        )));
    }
    update_core::validate_https_github_url(response.url().as_str()).map_err(AppError::update)?;
    if response.content_length().is_some_and(|size| size > limit) {
        return Err(AppError::update(
            "GitHub update response exceeded the size limit",
        ));
    }
    let mut bytes = Vec::new();
    response
        .by_ref()
        .take(limit + 1)
        .read_to_end(&mut bytes)
        .map_err(|error| {
            AppError::update(format!("Cannot read GitHub update response: {error}"))
        })?;
    if bytes.len() as u64 > limit {
        return Err(AppError::update(
            "GitHub update response exceeded the size limit",
        ));
    }
    Ok(bytes)
}

fn verify_manifest(manifest: &[u8], signature: &[u8]) -> Result<update_core::RemoteUpdateManifest> {
    let signature = std::str::from_utf8(signature)
        .map_err(|_| AppError::update("Update signature is not valid UTF-8"))?;
    update_core::verify_manifest_signature(manifest, signature, UPDATE_PUBLIC_KEY)
        .map_err(AppError::update)?;
    let parsed: update_core::RemoteUpdateManifest = serde_json::from_slice(manifest)
        .map_err(|error| AppError::update(format!("Invalid signed update manifest: {error}")))?;
    parsed.validate().map_err(AppError::update)?;
    Ok(parsed)
}

fn cached_manifest(allow_stale: bool) -> Result<Option<(update_core::RemoteUpdateManifest, u64)>> {
    let root = update_root()?;
    let manifest_path = root.join("latest.json");
    let signature_path = root.join("latest.json.sig");
    let metadata_path = root.join("cache.json");
    if !manifest_path.is_file() || !signature_path.is_file() || !metadata_path.is_file() {
        return Ok(None);
    }
    let metadata: CacheMetadata =
        serde_json::from_slice(&fs::read(metadata_path).map_err(AppError::transaction_io)?)
            .map_err(|error| AppError::update(format!("Invalid update cache metadata: {error}")))?;
    if metadata.schema_version != 1 {
        return Ok(None);
    }
    if !allow_stale && unix_time().saturating_sub(metadata.checked_at) >= CACHE_SECONDS {
        return Ok(None);
    }
    let manifest = fs::read(manifest_path).map_err(AppError::transaction_io)?;
    let signature = fs::read(signature_path).map_err(AppError::transaction_io)?;
    Ok(Some((
        verify_manifest(&manifest, &signature)?,
        metadata.checked_at,
    )))
}

fn write_cache(manifest: &[u8], signature: &[u8], checked_at: u64) -> Result<()> {
    let root = update_root()?;
    atomic_fs::write_replace(&root.join("latest.json"), manifest)
        .map_err(AppError::transaction_io)?;
    atomic_fs::write_replace(&root.join("latest.json.sig"), signature)
        .map_err(AppError::transaction_io)?;
    let metadata = serde_json::to_vec_pretty(&CacheMetadata {
        schema_version: 1,
        checked_at,
    })
    .map_err(|error| AppError::update(error.to_string()))?;
    atomic_fs::write_replace(&root.join("cache.json"), &metadata).map_err(AppError::transaction_io)
}

fn component_state(
    current: &str,
    component: Option<&update_core::RemoteComponent>,
    selected_progress: Option<&UpdateProgress>,
    error: Option<String>,
) -> UpdateComponentState {
    let latest = component.map(|value| value.version.clone());
    let update_available = component
        .and_then(|value| {
            Some(
                update_core::DisplayVersion::parse(&value.version).ok()?
                    > update_core::DisplayVersion::parse(current).ok()?,
            )
        })
        .unwrap_or(false);
    let compatible = component
        .and_then(|value| {
            Some(
                update_core::DisplayVersion::parse(app_version::display()).ok()?
                    >= update_core::DisplayVersion::parse(&value.min_panel_version).ok()?,
            )
        })
        .unwrap_or(true);
    let (status, downloaded_bytes, total_bytes) = if let Some(progress) = selected_progress {
        (
            progress.stage.clone(),
            progress.downloaded_bytes,
            progress.total_bytes,
        )
    } else if error.is_some() {
        (
            "error".into(),
            0,
            component.map(|value| value.size).unwrap_or(0),
        )
    } else if update_available {
        (
            if compatible {
                "available"
            } else {
                "panel-required"
            }
            .into(),
            0,
            component.map(|value| value.size).unwrap_or(0),
        )
    } else {
        (
            "current".into(),
            0,
            component.map(|value| value.size).unwrap_or(0),
        )
    };
    UpdateComponentState {
        current_version: current.into(),
        latest_version: latest,
        update_available,
        compatible,
        status,
        downloaded_bytes,
        total_bytes,
        error,
    }
}

pub fn snapshot(plugin_version: Option<&str>) -> Result<OnlineUpdateSnapshot> {
    let cached = cached_manifest(true).ok().flatten();
    let state = runtime()
        .lock()
        .map_err(|_| AppError::update("Update state lock is poisoned"))?
        .clone();
    let manifest = cached.as_ref().map(|value| &value.0);
    let panel_progress = state
        .progress
        .as_ref()
        .filter(|value| value.component == "panel");
    let plugin_progress = state
        .progress
        .as_ref()
        .filter(|value| value.component == "plugin");
    let plugin_current_version = plugin_version
        .map(str::to_owned)
        .unwrap_or_else(|| app_version::display().to_owned());
    Ok(OnlineUpdateSnapshot {
        checked_at: cached.as_ref().map(|value| value.1),
        release_version: manifest.map(|value| value.release_version.clone()),
        release_notes_url: manifest.map(|value| value.release_notes_url.clone()),
        panel: component_state(
            app_version::display(),
            manifest.map(|value| &value.components.panel),
            panel_progress,
            state.last_error.clone(),
        ),
        plugin: component_state(
            &plugin_current_version,
            manifest.map(|value| &value.components.plugin),
            plugin_progress,
            state.last_error.clone(),
        ),
        busy: state.busy,
        error: state.last_error,
    })
}

pub fn check(force: bool, plugin_version: Option<&str>) -> Result<OnlineUpdateSnapshot> {
    if !force && cached_manifest(false)?.is_some() {
        return snapshot(plugin_version);
    }
    let checked_at = unix_time();
    let client = client(REQUEST_TIMEOUT)?;
    let manifest = read_limited_response(
        client.get(MANIFEST_URL).send().map_err(|error| {
            AppError::update(format!("Cannot contact GitHub update service: {error}"))
        })?,
        update_core::MAX_MANIFEST_BYTES,
    )?;
    let signature = read_limited_response(
        client.get(SIGNATURE_URL).send().map_err(|error| {
            AppError::update(format!("Cannot download update signature: {error}"))
        })?,
        4096,
    )?;
    verify_manifest(&manifest, &signature)?;
    write_cache(&manifest, &signature, checked_at)?;
    if let Ok(mut state) = runtime().lock() {
        state.last_error = None;
    }
    snapshot(plugin_version)
}

pub fn record_check_error(error: &AppError) {
    if let Ok(mut state) = runtime().lock() {
        state.last_error = Some(error.detail.clone());
    }
}

fn set_progress(app: &AppHandle, value: UpdateProgress) {
    if let Ok(mut state) = runtime().lock() {
        state.progress = Some(value.clone());
    }
    let _ = app.emit("update-progress", value);
}

pub(crate) struct OperationGuard;
impl OperationGuard {
    pub(crate) fn acquire() -> Result<Self> {
        let mut state = runtime()
            .lock()
            .map_err(|_| AppError::update("Update state lock is poisoned"))?;
        if state.busy {
            return Err(AppError::update(
                "Another install or update operation is already running",
            ));
        }
        state.busy = true;
        state.last_error = None;
        CANCELLED.store(false, Ordering::Release);
        Ok(Self)
    }
}
impl Drop for OperationGuard {
    fn drop(&mut self) {
        if let Ok(mut state) = runtime().lock() {
            state.busy = false;
            state.progress = None;
        }
    }
}

fn selected_component(name: &str) -> Result<update_core::RemoteComponent> {
    let Some((manifest, _)) = cached_manifest(true)? else {
        return Err(AppError::update("Check for updates before installing"));
    };
    Ok(match name {
        "panel" => manifest.components.panel,
        "plugin" => manifest.components.plugin,
        _ => return Err(AppError::update("Unknown update component")),
    })
}

fn download_component(
    app: &AppHandle,
    name: &str,
) -> Result<(update_core::RemoteComponent, PathBuf)> {
    let component = selected_component(name)?;
    if update_core::DisplayVersion::parse(app_version::display()).map_err(AppError::update)?
        < update_core::DisplayVersion::parse(&component.min_panel_version)
            .map_err(AppError::update)?
    {
        return Err(AppError::update(
            "Update the Panel before installing this plugin version",
        ));
    }
    let directory = update_root()?.join("downloads").join(&component.version);
    fs::create_dir_all(&directory).map_err(AppError::transaction_io)?;
    let archive = directory.join(format!("{name}.zip"));
    if archive.is_file()
        && update_core::verify_component_file(&archive, component.size, &component.sha256).is_ok()
    {
        return Ok((component, archive));
    }
    let temporary = archive.with_extension("zip.download");
    let result = (|| -> Result<()> {
        update_core::validate_https_github_url(&component.url).map_err(AppError::update)?;
        let mut response = client(DOWNLOAD_TIMEOUT)?
            .get(&component.url)
            .send()
            .map_err(|error| {
                AppError::update(format!(
                    "Cannot download {name} update from GitHub: {error}"
                ))
            })?;
        if !response.status().is_success() {
            return Err(AppError::update(format!(
                "GitHub update download failed with HTTP {}",
                response.status()
            )));
        }
        update_core::validate_https_github_url(response.url().as_str())
            .map_err(AppError::update)?;
        if response
            .content_length()
            .is_some_and(|size| size != component.size)
        {
            return Err(AppError::update(
                "GitHub update Content-Length did not match the signed manifest",
            ));
        }
        let mut output = File::create(&temporary).map_err(AppError::transaction_io)?;
        let mut downloaded = 0_u64;
        let mut buffer = [0_u8; 128 * 1024];
        loop {
            if CANCELLED.load(Ordering::Acquire) {
                return Err(AppError::update("Update download was cancelled"));
            }
            let count = response
                .read(&mut buffer)
                .map_err(|error| AppError::update(format!("Update download failed: {error}")))?;
            if count == 0 {
                break;
            }
            downloaded = downloaded.saturating_add(count as u64);
            if downloaded > component.size || downloaded > update_core::MAX_ARCHIVE_BYTES {
                return Err(AppError::update(
                    "Downloaded update exceeded the signed size",
                ));
            }
            output
                .write_all(&buffer[..count])
                .map_err(AppError::transaction_io)?;
            set_progress(
                app,
                UpdateProgress {
                    component: name.into(),
                    stage: "downloading".into(),
                    downloaded_bytes: downloaded,
                    total_bytes: component.size,
                },
            );
        }
        output.sync_all().map_err(AppError::transaction_io)?;
        update_core::verify_component_file(&temporary, component.size, &component.sha256)
            .map_err(AppError::update)?;
        atomic_fs::replace(&temporary, &archive).map_err(AppError::transaction_io)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result?;
    Ok((component, archive))
}

fn clear_directory(path: &Path) -> Result<()> {
    if path.exists() {
        fs::remove_dir_all(path).map_err(AppError::transaction_io)?;
    }
    fs::create_dir_all(path).map_err(AppError::transaction_io)
}

fn find_payload_root(extracted: &Path) -> Option<PathBuf> {
    if extracted.join(installer::MANIFEST_FILE).is_file() {
        return Some(extracted.to_path_buf());
    }
    fs::read_dir(extracted)
        .ok()?
        .flatten()
        .filter(|entry| entry.path().is_dir())
        .map(|entry| entry.path())
        .find(|path| path.join(installer::MANIFEST_FILE).is_file())
}

pub fn prepare_plugin(app: &AppHandle) -> Result<(String, PathBuf)> {
    let (component, archive) = download_component(app, "plugin")?;
    set_progress(
        app,
        UpdateProgress {
            component: "plugin".into(),
            stage: "extracting".into(),
            downloaded_bytes: component.size,
            total_bytes: component.size,
        },
    );
    let directory = update_root()?.join("payloads").join(&component.version);
    clear_directory(&directory)?;
    let file = File::open(archive).map_err(AppError::transaction_io)?;
    if let Err(error) = update_core::extract_zip_safely(file, &directory) {
        let _ = fs::remove_dir_all(&directory);
        return Err(AppError::update(error));
    }
    let root = find_payload_root(&directory)
        .ok_or_else(|| AppError::payload("Plugin update ZIP has no payload manifest"))?;
    let manifest = installer::verify_payload(&root)?;
    if manifest.package_version != component.version {
        return Err(AppError::payload(
            "Plugin payload version does not match the signed update manifest",
        ));
    }
    Ok((component.version, root))
}

pub fn activate_payload(version: &str, path: &Path) -> Result<()> {
    let updates = update_root()?;
    let canonical = fs::canonicalize(path).map_err(AppError::transaction_io)?;
    let payloads = fs::canonicalize(updates.join("payloads")).map_err(AppError::transaction_io)?;
    if !canonical.starts_with(&payloads) {
        return Err(AppError::update(
            "Active payload is outside the update cache",
        ));
    }
    installer::verify_payload(&canonical)?;
    let value = serde_json::to_vec_pretty(&ActivePayload {
        schema_version: 1,
        version: version.into(),
        path: canonical.to_string_lossy().into_owned(),
    })
    .map_err(|error| AppError::update(error.to_string()))?;
    atomic_fs::write_replace(&updates.join("active-payload.json"), &value)
        .map_err(AppError::transaction_io)?;
    cleanup_cache(Some(version))?;
    Ok(())
}

pub fn active_payload_root() -> Option<PathBuf> {
    let updates = update_root().ok()?;
    let pointer: ActivePayload =
        serde_json::from_slice(&fs::read(updates.join("active-payload.json")).ok()?).ok()?;
    if pointer.schema_version != 1 {
        return None;
    }
    let path = fs::canonicalize(pointer.path).ok()?;
    let payloads = fs::canonicalize(updates.join("payloads")).ok()?;
    if !path.starts_with(payloads) || installer::verify_payload(&path).is_err() {
        return None;
    }
    Some(path)
}

pub fn prepare_panel(app: &AppHandle) -> Result<UpdateResult> {
    let (component, archive) = download_component(app, "panel")?;
    set_progress(
        app,
        UpdateProgress {
            component: "panel".into(),
            stage: "extracting".into(),
            downloaded_bytes: component.size,
            total_bytes: component.size,
        },
    );
    let directory = update_root()?.join("panel").join(&component.version);
    clear_directory(&directory)?;
    update_core::extract_zip_safely(
        File::open(archive).map_err(AppError::transaction_io)?,
        &directory,
    )
    .map_err(AppError::update)?;
    let staged = if directory.join(PANEL_EXECUTABLE_NAME).is_file() {
        directory.join(PANEL_EXECUTABLE_NAME)
    } else {
        fs::read_dir(&directory)
            .map_err(AppError::transaction_io)?
            .flatten()
            .map(|entry| entry.path().join(PANEL_EXECUTABLE_NAME))
            .find(|path| path.is_file())
            .ok_or_else(|| {
                AppError::payload(format!(
                    "Panel update ZIP has no {PANEL_EXECUTABLE_NAME}"
                ))
            })?
    };
    schedule_panel_replace(&component.version, &staged)?;
    Ok(UpdateResult {
        component: "panel".into(),
        version: component.version,
        installed: true,
        restart_required: true,
        rollback_succeeded: None,
        detail: "Panel update is staged and will be applied after restart".into(),
    })
}

fn schedule_panel_replace(version: &str, staged: &Path) -> Result<()> {
    let current = std::env::current_exe().map_err(AppError::transaction_io)?;
    let parent = current
        .parent()
        .ok_or_else(|| AppError::update("Panel executable has no parent directory"))?;
    let current_name = current.file_name().and_then(|name| name.to_str());
    if !matches!(
        current_name,
        Some(name)
            if name.eq_ignore_ascii_case(PANEL_EXECUTABLE_NAME)
                || name.eq_ignore_ascii_case(LEGACY_PANEL_EXECUTABLE_NAME)
    ) {
        return Err(AppError::update(
            format!(
                "Online Panel updates require {PANEL_EXECUTABLE_NAME} or the legacy filename {LEGACY_PANEL_EXECUTABLE_NAME}"
            ),
        ));
    }
    let target = parent.join(PANEL_EXECUTABLE_NAME);
    let helper_dir = update_root()?.join("helper");
    fs::create_dir_all(&helper_dir).map_err(AppError::transaction_io)?;
    let helper = helper_dir.join("LocalArena-update-helper.exe");
    fs::copy(&current, &helper).map_err(AppError::transaction_io)?;
    let plan_path = helper_dir.join("panel-update-plan.json");
    let backup = update_root()?
        .join("panel-backups")
        .join(format!("{version}-previous.exe"));
    let plan = serde_json::to_vec_pretty(&PanelUpdatePlan {
        schema_version: 1,
        old_pid: std::process::id(),
        current: current.to_string_lossy().into_owned(),
        target: target.to_string_lossy().into_owned(),
        staged: staged.to_string_lossy().into_owned(),
        backup: backup.to_string_lossy().into_owned(),
    })
    .map_err(|error| AppError::update(error.to_string()))?;
    atomic_fs::write_replace(&plan_path, &plan).map_err(AppError::transaction_io)?;
    Command::new(helper)
        .arg("--apply-panel-update")
        .arg(plan_path)
        .spawn()
        .map_err(|error| AppError::update(format!("Cannot start Panel update helper: {error}")))?;
    Ok(())
}

pub fn cancel() {
    CANCELLED.store(true, Ordering::Release);
}

pub fn cleanup_cache(active_version: Option<&str>) -> Result<usize> {
    let root = update_root()?;
    let mut versions = fs::read_dir(root.join("payloads"))
        .ok()
        .into_iter()
        .flatten()
        .flatten()
        .filter(|entry| entry.path().is_dir())
        .map(|entry| {
            (
                entry
                    .metadata()
                    .and_then(|value| value.modified())
                    .unwrap_or(UNIX_EPOCH),
                entry.file_name().to_string_lossy().into_owned(),
                entry.path(),
            )
        })
        .collect::<Vec<_>>();
    versions.sort_by_key(|value| std::cmp::Reverse(value.0));
    let mut keep = versions
        .iter()
        .find(|value| active_version == Some(value.1.as_str()))
        .map(|value| vec![value.2.clone()])
        .unwrap_or_default();
    for (_, _, path) in &versions {
        if keep.len() >= 2 { break; }
        if !keep.contains(path) { keep.push(path.clone()); }
    }
    let mut removed = 0;
    for (_, _, path) in versions {
        if !keep.contains(&path) && fs::remove_dir_all(path).is_ok() {
            removed += 1;
        }
    }
    Ok(removed)
}

#[cfg(windows)]
fn process_exists(pid: u32) -> bool {
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};
    let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if handle.is_null() {
        false
    } else {
        unsafe {
            CloseHandle(handle);
        }
        true
    }
}

#[cfg(not(windows))]
fn process_exists(_pid: u32) -> bool {
    false
}

pub fn maybe_apply_panel_update() -> bool {
    let mut arguments = std::env::args_os();
    let _ = arguments.next();
    if arguments.next().as_deref() != Some(std::ffi::OsStr::new("--apply-panel-update")) {
        return false;
    }
    let Some(plan_path) = arguments.next() else {
        return true;
    };
    let outcome = (|| -> std::result::Result<(), String> {
        let plan: PanelUpdatePlan =
            serde_json::from_slice(&fs::read(&plan_path).map_err(|error| error.to_string())?)
                .map_err(|error| error.to_string())?;
        if plan.schema_version != 1 {
            return Err("Unsupported Panel update plan".into());
        }
        for _ in 0..300 {
            if !process_exists(plan.old_pid) {
                break;
            }
            std::thread::sleep(Duration::from_millis(100));
        }
        if process_exists(plan.old_pid) {
            return Err("Timed out waiting for the old Panel process".into());
        }
        let target = PathBuf::from(&plan.target);
        let current = PathBuf::from(&plan.current);
        let staged = PathBuf::from(&plan.staged);
        let backup = PathBuf::from(&plan.backup);
        update_core::install_file_with_backup(&staged, &current, &target, &backup)?;
        if let Err(error) = Command::new(&target).spawn() {
            let migrated = !current.eq_ignore_ascii_case(&target);
            let rollback = if migrated {
                let target_removed = !target.exists() || fs::remove_file(&target).is_ok();
                target_removed && fs::copy(&backup, &current).is_ok()
            } else {
                fs::copy(&backup, &target).is_ok()
            };
            return Err(format!(
                "Panel updated but could not restart; rollback_succeeded={rollback}: {error}"
            ));
        }
        if !current.eq_ignore_ascii_case(&target) {
            let _ = fs::remove_file(current);
        }
        let _ = fs::remove_file(plan_path);
        Ok(())
    })();
    if let Err(error) = outcome {
        if let Ok(root) = app_storage::root() {
            logging::append(&root, "ERROR", "update.panel_helper_failed", &error);
        }
    }
    true
}

trait EqIgnoreAsciiCasePath {
    fn eq_ignore_ascii_case(&self, other: &Path) -> bool;
}
impl EqIgnoreAsciiCasePath for PathBuf {
    fn eq_ignore_ascii_case(&self, other: &Path) -> bool {
        self.to_string_lossy()
            .eq_ignore_ascii_case(&other.to_string_lossy())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn test_root(prefix: &str) -> PathBuf {
        std::env::temp_dir().join(format!("{prefix}-{}-{}", std::process::id(), unix_time()))
    }

    #[test]
    fn active_payload_rejects_paths_outside_cache() {
        let _guard = app_storage::TEST_STATE_ENV_LOCK.lock().unwrap_or_else(|error| error.into_inner());
        let base = std::env::temp_dir().join(format!("cs2bi-active-payload-{}", unix_time()));
        unsafe {
            std::env::set_var("CS2BI_STATE_ROOT", &base);
        }
        fs::create_dir_all(base.join("updates/payloads")).unwrap();
        let value = serde_json::to_vec(&ActivePayload {
            schema_version: 1,
            version: "9.9.9.9".into(),
            path: base.parent().unwrap().to_string_lossy().into_owned(),
        })
        .unwrap();
        fs::write(base.join("updates/active-payload.json"), value).unwrap();
        assert!(active_payload_root().is_none());
        fs::remove_dir_all(&base).unwrap();
        unsafe {
            std::env::remove_var("CS2BI_STATE_ROOT");
        }
    }

    #[test]
    fn operation_guard_rejects_overlap_and_releases_on_drop() {
        let first = OperationGuard::acquire().unwrap();
        assert!(OperationGuard::acquire().is_err());
        drop(first);
        assert!(OperationGuard::acquire().is_ok());
    }

    #[test]
    fn stale_cache_is_not_reused() {
        let _guard = app_storage::TEST_STATE_ENV_LOCK.lock().unwrap_or_else(|error| error.into_inner());
        let base = test_root("cs2bi-stale-update-cache");
        unsafe { std::env::set_var("CS2BI_STATE_ROOT", &base); }
        let updates = base.join("updates");
        fs::create_dir_all(&updates).unwrap();
        fs::write(updates.join("latest.json"), b"invalid").unwrap();
        fs::write(updates.join("latest.json.sig"), b"invalid").unwrap();
        fs::write(updates.join("cache.json"), serde_json::to_vec(&CacheMetadata {
            schema_version: 1,
            checked_at: 0,
        }).unwrap()).unwrap();
        assert!(cached_manifest(false).unwrap().is_none());
        fs::remove_dir_all(&base).unwrap();
        unsafe { std::env::remove_var("CS2BI_STATE_ROOT"); }
    }

    #[test]
    fn active_payload_switches_to_a_verified_payload() {
        let _guard = app_storage::TEST_STATE_ENV_LOCK.lock().unwrap_or_else(|error| error.into_inner());
        let base = test_root("cs2bi-active-payload-switch");
        unsafe { std::env::set_var("CS2BI_STATE_ROOT", &base); }
        let payload = base.join("updates/payloads/1.4.2.4");
        let file = payload.join("addons/test.bin");
        fs::create_dir_all(file.parent().unwrap()).unwrap();
        fs::write(&file, b"payload").unwrap();
        let manifest = installer::PayloadManifest {
            schema_version: 1,
            package_version: "1.4.2.4".into(),
            entries: vec![installer::PayloadEntry {
                path: "addons/test.bin".into(),
                size: 7,
                sha256: update_core::sha256_file(&file).unwrap(),
                component: "plugin".into(),
                ownership: "plus".into(),
                restore_policy: "restore".into(),
            }],
        };
        fs::write(payload.join(installer::MANIFEST_FILE), serde_json::to_vec(&manifest).unwrap()).unwrap();
        activate_payload("1.4.2.4", &payload).unwrap();
        assert_eq!(active_payload_root().unwrap(), fs::canonicalize(&payload).unwrap());
        fs::remove_dir_all(&base).unwrap();
        unsafe { std::env::remove_var("CS2BI_STATE_ROOT"); }
    }

    #[test]
    fn cleanup_keeps_only_active_and_one_previous_payload() {
        let _guard = app_storage::TEST_STATE_ENV_LOCK.lock().unwrap_or_else(|error| error.into_inner());
        let base = test_root("cs2bi-update-cleanup");
        unsafe { std::env::set_var("CS2BI_STATE_ROOT", &base); }
        let payloads = base.join("updates/payloads");
        for version in ["1.4.2.2", "1.4.2.3", "1.4.2.4"] {
            fs::create_dir_all(payloads.join(version)).unwrap();
        }
        assert_eq!(cleanup_cache(Some("1.4.2.2")).unwrap(), 1);
        let remaining = fs::read_dir(&payloads).unwrap().flatten().count();
        assert_eq!(remaining, 2);
        assert!(payloads.join("1.4.2.2").is_dir());
        fs::remove_dir_all(&base).unwrap();
        unsafe { std::env::remove_var("CS2BI_STATE_ROOT"); }
    }
}
