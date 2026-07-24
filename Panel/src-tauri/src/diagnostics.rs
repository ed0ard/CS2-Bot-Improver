use crate::{AppError, Result, atomic_fs, mode_files, steam};
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
#[cfg(all(windows, not(test)))]
use std::process::Command;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use zip::write::SimpleFileOptions;
use zip::{CompressionMethod, ZipWriter};

fn match_metadata(csgo: &Path) -> serde_json::Value {
    let root = csgo.join(".csbip/matches");
    let mut entries = Vec::new();
    for directory in fs::read_dir(root).into_iter().flatten().flatten() {
        let path = directory.path();
        if !path.is_dir() { continue; }
        for name in ["request.json", "result.json"] {
            let file = path.join(name);
            if let Ok(metadata) = fs::metadata(&file) {
                entries.push(serde_json::json!({
                    "session_id": directory.file_name().to_string_lossy(),
                    "kind": name,
                    "path": file.to_string_lossy(),
                    "size": metadata.len(),
                    "modified_unix": metadata.modified().ok().and_then(|time| time.duration_since(UNIX_EPOCH).ok()).map(|value| value.as_secs()),
                }));
            }
        }
    }
    serde_json::Value::Array(entries)
}

fn runtime_mount_metadata(csgo: &Path) -> serde_json::Value {
    let gameinfo = csgo.join("gameinfo.gi");
    let gameinfo_bytes = fs::read(&gameinfo).ok();
    let vpks = [
        ("active", csgo.join("overrides/botprofile.vpk")),
        ("low", csgo.join("overrides/Low/botprofile.vpk")),
        ("medium", csgo.join("overrides/Medium/botprofile.vpk")),
        ("high", csgo.join("overrides/High/botprofile.vpk")),
    ]
    .into_iter()
    .map(|(name, path)| {
        let metadata = fs::metadata(&path).ok();
        let sha256 = fs::read(&path)
            .ok()
            .map(|bytes| format!("{:x}", Sha256::digest(bytes)));
        serde_json::json!({
            "name": name,
            "path": path.to_string_lossy(),
            "present": metadata.is_some(),
            "size": metadata.map(|value| value.len()),
            "sha256": sha256,
        })
    })
    .collect::<Vec<_>>();
    serde_json::json!({
        "gameinfo_path": gameinfo.to_string_lossy(),
        "gameinfo_present": gameinfo_bytes.is_some(),
        "metamod_search_path": gameinfo_bytes.as_deref().is_some_and(mode_files::contains_metamod_search_path),
        "botprofile_search_path": gameinfo_bytes.as_deref().is_some_and(mode_files::contains_botprofile_search_path),
        "botprofile_vpks": vpks,
    })
}

const MAX_SOURCE_BYTES: usize = 2 * 1024 * 1024;
const MAX_ARCHIVE_INPUT_BYTES: usize = 32 * 1024 * 1024;
const ARCHIVE_RETENTION: Duration = Duration::from_secs(14 * 24 * 60 * 60);
const MAX_ARCHIVES: usize = 10;
const EVENT_LOG_WINDOW_MS: u64 = 7 * 24 * 60 * 60 * 1000;
const EVENT_LOG_LIMIT: u32 = 250;

#[derive(Debug, Serialize)]
pub struct DiagnosticArchive {
    pub path: String,
    pub files_collected: usize,
}

struct Collector {
    writer: ZipWriter<File>,
    files: usize,
    input_bytes: usize,
}

impl Collector {
    fn add_bytes(&mut self, name: &str, bytes: &[u8]) -> Result<bool> {
        if self.input_bytes >= MAX_ARCHIVE_INPUT_BYTES { return Ok(false); }
        let start = bytes.len().saturating_sub(MAX_SOURCE_BYTES);
        let content = &bytes[start..];
        if self.input_bytes + content.len() > MAX_ARCHIVE_INPUT_BYTES { return Ok(false); }
        let options = SimpleFileOptions::default()
            .compression_method(CompressionMethod::Deflated)
            .unix_permissions(0o600);
        self.writer.start_file(name.replace('\\', "/"), options)
            .map_err(|error| AppError::transaction(format!("Cannot create diagnostic ZIP entry: {error}")))?;
        self.writer.write_all(content).map_err(AppError::transaction_io)?;
        self.files += 1;
        self.input_bytes += content.len();
        Ok(true)
    }

    fn add_json<T: Serialize>(&mut self, name: &str, value: &T) -> Result<bool> {
        let bytes = serde_json::to_vec_pretty(value)
            .map_err(|error| AppError::transaction(error.to_string()))?;
        self.add_bytes(name, &bytes)
    }

    fn add_bytes_head(&mut self, name: &str, bytes: &[u8]) -> Result<bool> {
        self.add_bytes(name, &bytes[..bytes.len().min(MAX_SOURCE_BYTES)])
    }

    fn add_file(&mut self, name: &str, path: &Path) -> Result<bool> {
        let mut file = match File::open(path) {
            Ok(file) => file,
            Err(_) => return Ok(false),
        };
        let length = file.metadata().map(|metadata| metadata.len()).unwrap_or(0);
        if length > MAX_SOURCE_BYTES as u64 {
            use std::io::{Seek, SeekFrom};
            file.seek(SeekFrom::End(-(MAX_SOURCE_BYTES as i64)))
                .map_err(AppError::transaction_io)?;
        }
        let mut bytes = Vec::new();
        file.read_to_end(&mut bytes).map_err(AppError::transaction_io)?;
        self.add_bytes(name, &bytes)
    }
}

fn unix_time() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

fn recent_files(directory: &Path, limit: usize) -> Vec<PathBuf> {
    let mut files = fs::read_dir(directory).into_iter().flatten().flatten()
        .filter_map(|entry| {
            let metadata = entry.metadata().ok()?;
            metadata.is_file().then(|| (metadata.modified().unwrap_or(UNIX_EPOCH), entry.path()))
        }).collect::<Vec<_>>();
    files.sort_by_key(|(modified, _)| *modified);
    files.into_iter().rev().take(limit).map(|(_, path)| path).collect()
}

fn recent_files_recursive(directory: &Path, limit: usize, max_depth: usize) -> Vec<PathBuf> {
    let mut pending = vec![(directory.to_path_buf(), 0usize)];
    let mut files = Vec::new();
    while let Some((current, depth)) = pending.pop() {
        for entry in fs::read_dir(current).into_iter().flatten().flatten() {
            let Ok(file_type) = entry.file_type() else { continue; };
            if file_type.is_file() {
                let modified = entry.metadata().and_then(|metadata| metadata.modified()).unwrap_or(UNIX_EPOCH);
                files.push((modified, entry.path()));
            } else if file_type.is_dir() && depth < max_depth {
                pending.push((entry.path(), depth + 1));
            }
        }
    }
    files.sort_by_key(|(modified, _)| *modified);
    files.into_iter().rev().take(limit).map(|(_, path)| path).collect()
}

fn add_named_files(collector: &mut Collector, prefix: &str, paths: impl IntoIterator<Item = PathBuf>) -> Result<()> {
    for (index, path) in paths.into_iter().enumerate() {
        let Some(name) = path.file_name().and_then(|name| name.to_str()) else { continue; };
        let _ = collector.add_file(&format!("{prefix}/{index:02}-{name}"), &path)?;
    }
    Ok(())
}

fn recent_match_records(csgo: &Path) -> Vec<PathBuf> {
    recent_files_recursive(&csgo.join(".csbip/matches"), 12, 2)
        .into_iter()
        .filter(|path| matches!(path.file_name().and_then(|name| name.to_str()), Some("request.json" | "result.json")))
        .collect()
}

fn steam_log_files(_csgo: &Path) -> Vec<PathBuf> {
    steam::client_log_files(&[
        "content_log.txt",
        "shader_log.txt",
        "bootstrap_log.txt",
        "connection_log.txt",
    ])
}

fn add_windows_event_logs(collector: &mut Collector) -> Result<()> {
    let mut statuses = Vec::new();
    for channel in ["System", "Application"] {
        match query_windows_event_log(channel) {
            Ok(bytes) => {
                let added = collector.add_bytes_head(
                    &format!("windows-event-logs/{channel}.xml"),
                    &bytes,
                )?;
                statuses.push(WindowsEventLogStatus {
                    channel: channel.into(),
                    collected: added,
                    bytes: bytes.len(),
                    error: (!added).then(|| "Diagnostic archive input limit was reached".into()),
                });
            }
            Err(error) => statuses.push(WindowsEventLogStatus {
                channel: channel.into(),
                collected: false,
                bytes: 0,
                error: Some(error),
            }),
        }
    }
    let _ = collector.add_json("report/windows-event-logs.json", &statuses)?;
    Ok(())
}

fn installation_metadata(state_root: &Path) -> Vec<PathBuf> {
    let mut paths = Vec::new();
    let installations = state_root.join("installations");
    for installation in fs::read_dir(installations).into_iter().flatten().flatten() {
        for name in ["record.json", "journal.json"] {
            let path = installation.path().join(name);
            if path.is_file() { paths.push(path); }
        }
    }
    paths
}

fn crash_dump_metadata() -> serde_json::Value {
    let mut dumps = Vec::new();
    let Some(local) = std::env::var_os("LOCALAPPDATA") else { return serde_json::json!([]); };
    let directory = PathBuf::from(local).join("CrashDumps");
    for path in recent_files(&directory, 50) {
        let name = path.file_name().and_then(|name| name.to_str()).unwrap_or_default().to_ascii_lowercase();
        if !name.contains("cs2") && !name.contains("cs2botimprover") { continue; }
        if let Ok(metadata) = fs::metadata(&path) {
            dumps.push(serde_json::json!({
                "path": path.to_string_lossy(),
                "size": metadata.len(),
                "modified_unix": metadata.modified().ok()
                    .and_then(|time| time.duration_since(UNIX_EPOCH).ok()).map(|value| value.as_secs()),
            }));
        }
    }
    serde_json::Value::Array(dumps)
}

fn appearance_metadata(state_root: &Path) -> serde_json::Value {
    let path = state_root.join("personalization/appearance.json");
    let Ok(bytes) = fs::read(&path) else {
        return serde_json::json!({ "present": false });
    };
    let Ok(value) = serde_json::from_slice::<serde_json::Value>(&bytes) else {
        return serde_json::json!({ "present": true, "valid_json": false, "size_bytes": bytes.len() });
    };
    let asset = |name: &str| {
        let entry = value.get(name);
        let data_url = entry.and_then(|item| item.get("data_url")).and_then(serde_json::Value::as_str);
        serde_json::json!({
            "present": entry.is_some_and(|item| !item.is_null()),
            "mime": data_url.and_then(|data| data.strip_prefix("data:")).and_then(|data| data.split_once(';')).map(|(mime, _)| mime),
            "encoded_chars": data_url.map(str::len),
            "fit": entry.and_then(|item| item.get("fit")),
            "position_x": entry.and_then(|item| item.get("position_x")),
            "position_y": entry.and_then(|item| item.get("position_y")),
            "dim": entry.and_then(|item| item.get("dim")),
            "blur": entry.and_then(|item| item.get("blur")),
            "shape": entry.and_then(|item| item.get("shape")),
        })
    };
    serde_json::json!({
        "present": true,
        "valid_json": true,
        "size_bytes": bytes.len(),
        "schema_version": value.get("schema_version"),
        "team_theme": value.get("team_theme"),
        "style": value.get("style"),
        "palette": value.get("palette"),
        "font": value.get("font"),
        "density": value.get("density"),
        "background": asset("background"),
        "logo": asset("logo"),
        "custom_font_present": value.get("custom_font").is_some_and(|item| !item.is_null()),
    })
}

fn wer_reports() -> Vec<PathBuf> {
    let mut reports = Vec::new();
    let mut roots = Vec::new();
    if let Some(program_data) = std::env::var_os("PROGRAMDATA") {
        let root = PathBuf::from(program_data).join("Microsoft/Windows/WER");
        roots.push(root.join("ReportArchive"));
        roots.push(root.join("ReportQueue"));
    }
    if let Some(local) = std::env::var_os("LOCALAPPDATA") {
        let root = PathBuf::from(local).join("Microsoft/Windows/WER");
        roots.push(root.join("ReportArchive"));
        roots.push(root.join("ReportQueue"));
    }
    for root in roots {
        for directory in fs::read_dir(root).into_iter().flatten().flatten() {
            let name = directory.file_name().to_string_lossy().to_ascii_lowercase();
            if !name.contains("cs2") && !name.contains("cs2botimprover") { continue; }
            let report = directory.path().join("Report.wer");
            if report.is_file() { reports.push(report); }
        }
    }
    reports.sort_by_key(|path| fs::metadata(path).and_then(|metadata| metadata.modified()).unwrap_or(UNIX_EPOCH));
    reports.into_iter().rev().take(5).collect()
}

#[derive(Serialize)]
struct WindowsEventLogStatus {
    channel: String,
    collected: bool,
    bytes: usize,
    error: Option<String>,
}

#[cfg(all(windows, not(test)))]
fn query_windows_event_log(channel: &str) -> std::result::Result<Vec<u8>, String> {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let query = format!("/q:*[System[TimeCreated[timediff(@SystemTime) <= {EVENT_LOG_WINDOW_MS}]]]");
    let count = format!("/c:{EVENT_LOG_LIMIT}");
    let output = Command::new("wevtutil.exe")
        .args([
            "qe",
            channel,
            &query,
            "/rd:true",
            "/f:xml",
            &count,
        ])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|error| format!("Cannot start wevtutil for {channel}: {error}"))?;
    if !output.status.success() {
        return Err(format!(
            "wevtutil {channel} exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }
    Ok(output.stdout)
}

#[cfg(any(not(windows), test))]
fn query_windows_event_log(channel: &str) -> std::result::Result<Vec<u8>, String> {
    Err(format!("Windows event channel {channel} is unavailable on this operating system"))
}

pub fn cleanup_archives(state_root: &Path) -> std::io::Result<usize> {
    let directory = state_root.join("diagnostics");
    fs::create_dir_all(&directory)?;
    let now = SystemTime::now();
    let mut removed = 0;
    let mut archives = Vec::new();
    for entry in fs::read_dir(&directory)?.flatten() {
        let path = entry.path();
        let Ok(metadata) = entry.metadata() else { continue; };
        if !metadata.is_file() || path.extension().and_then(|value| value.to_str()) != Some("zip") { continue; }
        let modified = metadata.modified().unwrap_or(UNIX_EPOCH);
        if now.duration_since(modified).unwrap_or_default() > ARCHIVE_RETENTION {
            if fs::remove_file(path).is_ok() { removed += 1; }
        } else { archives.push((modified, path)); }
    }
    archives.sort_by_key(|(modified, _)| *modified);
    while archives.len() > MAX_ARCHIVES {
        let (_, path) = archives.remove(0);
        if fs::remove_file(path).is_ok() { removed += 1; }
    }
    Ok(removed)
}

pub fn export(state_root: &Path, csgo: Option<&Path>, snapshot: &serde_json::Value) -> Result<DiagnosticArchive> {
    cleanup_archives(state_root).map_err(AppError::transaction_io)?;
    let output_dir = state_root.join("diagnostics");
    fs::create_dir_all(&output_dir).map_err(AppError::transaction_io)?;
    let timestamp = unix_time();
    let output = output_dir.join(format!("CS2BotImproverPlus-Diagnostics-{timestamp}.zip"));
    let temporary = atomic_fs::temporary_path(&output).map_err(AppError::transaction_io)?;
    let file = File::create(&temporary).map_err(AppError::transaction_io)?;
    let mut collector = Collector { writer: ZipWriter::new(file), files: 0, input_bytes: 0 };

    collector.add_json("report/runtime-snapshot.json", snapshot)?;
    collector.add_json("report/system.json", &serde_json::json!({
        "panel_version": crate::app_version::display(),
        "generated_at_unix": timestamp,
        "os": std::env::consts::OS,
        "architecture": std::env::consts::ARCH,
        "state_root": state_root.to_string_lossy(),
        "csgo": csgo.map(|path| path.to_string_lossy()),
    }))?;
    collector.add_json("report/crash-dumps.json", &crash_dump_metadata())?;
    collector.add_json("report/appearance.json", &appearance_metadata(state_root))?;
    add_windows_event_logs(&mut collector)?;

    for (name, path) in [
        ("config/panel.json", state_root.join("config/panel.json")),
        ("config/ui-state.json", state_root.join("memory/ui-state.json")),
        ("config/cosmetics-knife.json", state_root.join("presets/current/player_knife_presets.json")),
        ("config/cosmetics-guns.json", state_root.join("presets/current/player_gun_presets.json")),
        ("installation/install-check-latest.json", state_root.join("reports/install-check-latest.json")),
    ] {
        let _ = collector.add_file(name, &path)?;
    }

    add_named_files(&mut collector, "logs/panel", recent_files(&state_root.join("logs/panel"), 10))?;
    for path in installation_metadata(state_root) {
        let installation = path.parent().and_then(|parent| parent.file_name()).and_then(|name| name.to_str()).unwrap_or("unknown");
        let name = path.file_name().and_then(|name| name.to_str()).unwrap_or("metadata.json");
        let _ = collector.add_file(&format!("installation/{installation}/{name}"), &path)?;
    }

    if let Some(csgo) = csgo {
        collector.add_json("report/runtime-mounts.json", &runtime_mount_metadata(csgo))?;
        collector.add_json("matches/metadata.json", &match_metadata(csgo))?;
        add_named_files(&mut collector, "matches/recent", recent_match_records(csgo))?;
        for (name, path) in [
            ("runtime/match-runtime.json", csgo.join(".csbip/match-runtime.json")),
            ("runtime/aim-runtime.json", csgo.join(".csbip/aim-runtime.json")),
            ("runtime/purchase-runtime.json", csgo.join(".csbip/purchase-runtime.json")),
            ("logs/cs2/console.log", csgo.join("console.log")),
            ("logs/cs2/console-history.txt", csgo.join("console_history.txt")),
            ("logs/metamod/metamod-fatal.log", csgo.join("addons/metamod/metamod-fatal.log")),
        ] {
            let _ = collector.add_file(name, &path)?;
        }
        add_named_files(&mut collector, "logs/cs2/recent",
            recent_files_recursive(&csgo.join("logs"), 20, 2))?;
        add_named_files(&mut collector, "logs/counterstrikesharp",
            recent_files_recursive(&csgo.join("addons/counterstrikesharp/logs"), 30, 4))?;
        add_named_files(&mut collector, "logs/steam", steam_log_files(csgo))?;
    }
    add_named_files(&mut collector, "wer", wer_reports())?;

    let files_collected = collector.files;
    let archive = collector.writer.finish()
        .map_err(|error| AppError::transaction(format!("Cannot finish diagnostic ZIP: {error}")))?;
    archive.sync_all().map_err(AppError::transaction_io)?;
    drop(archive);
    if let Err(error) = atomic_fs::replace(&temporary, &output) {
        let _ = fs::remove_file(&temporary);
        return Err(AppError::transaction_io(error));
    }
    Ok(DiagnosticArchive { path: output.to_string_lossy().into_owned(), files_collected })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn diagnostic_export_is_a_readable_zip() {
        let root = std::env::temp_dir().join(format!("cs2bi-diagnostics-{}", unix_time()));
        let csgo = root.join("game/csgo");
        fs::create_dir_all(root.join("logs/panel")).unwrap();
        fs::create_dir_all(csgo.join("logs")).unwrap();
        fs::create_dir_all(csgo.join(".csbip/matches/session-1")).unwrap();
        fs::create_dir_all(csgo.join("overrides/Medium")).unwrap();
        fs::write(root.join("logs/panel/panel-current.jsonl"), b"test").unwrap();
        fs::write(csgo.join("logs/console.log"), b"cs2 test log").unwrap();
        fs::write(
            csgo.join("gameinfo.gi"),
            b"SearchPaths\n{\nGame csgo/addons/metamod\nGame csgo/overrides/botprofile.vpk\nGame csgo\n}\n",
        ).unwrap();
        fs::write(csgo.join("overrides/Medium/botprofile.vpk"), b"vpk").unwrap();
        fs::write(
            csgo.join(".csbip/matches/session-1/request.json"),
            br#"{"schema_version":1,"session_id":"session-1"}"#,
        ).unwrap();
        let archive = export(&root, Some(&csgo), &serde_json::json!({"ok": true})).unwrap();
        let file = File::open(&archive.path).unwrap();
        let mut zip = zip::ZipArchive::new(file).unwrap();
        assert!(zip.by_name("report/runtime-snapshot.json").is_ok());
        assert!(zip.by_name("report/windows-event-logs.json").is_ok());
        assert!(zip.by_name("report/runtime-mounts.json").is_ok());
        assert!(zip.by_name("report/appearance.json").is_ok());
        assert!(zip.by_name("logs/panel/00-panel-current.jsonl").is_ok());
        assert!(zip.by_name("logs/cs2/recent/00-console.log").is_ok());
        assert!(zip.by_name("matches/recent/00-request.json").is_ok());
        fs::remove_dir_all(root).unwrap();
    }
}
