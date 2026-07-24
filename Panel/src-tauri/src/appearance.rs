use crate::{AppError, Result, app_storage, atomic_fs};
use base64::Engine;
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

const APPEARANCE_SCHEMA_VERSION: u32 = 1;
const THEME_SCHEMA_VERSION: u32 = 1;
const THEME_KIND: &str = "local-arena-theme";
const MAX_THEME_BYTES: u64 = 64 * 1024 * 1024;
const MAX_BACKGROUND_BYTES: usize = 8 * 1024 * 1024;
const MAX_LOGO_BYTES: usize = 2 * 1024 * 1024;
const MAX_FONT_BYTES: usize = 24 * 1024 * 1024;

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AppearanceStyle {
    #[default]
    Paper,
    Clean,
    Compact,
    Immersive,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AppearancePalette {
    #[default]
    Terracotta,
    Sky,
    Monochrome,
    Grass,
    Mist,
    Berry,
    Custom,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AppearanceFont {
    #[default]
    Humanist,
    Modern,
    Clear,
    Classic,
    Technical,
    Custom,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AppearanceDensity {
    Compact,
    #[default]
    Standard,
    Relaxed,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AppearanceLevel {
    None,
    Subtle,
    #[default]
    Soft,
    Strong,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AppearanceMotion {
    Off,
    Reduced,
    #[default]
    Full,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct AppearanceBackground {
    pub data_url: String,
    pub fit: String,
    pub position_x: u8,
    pub position_y: u8,
    pub dim: u8,
    pub blur: u8,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct AppearanceLogo {
    pub data_url: String,
    pub fit: String,
    pub shape: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct AppearanceCustomFont {
    pub data_url: String,
    pub file_name: String,
    pub format: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct AppearanceConfig {
    pub schema_version: u32,
    #[serde(default)]
    pub team_theme: Option<String>,
    #[serde(default = "default_brand_name")]
    pub brand_name: String,
    pub style: AppearanceStyle,
    pub palette: AppearancePalette,
    pub accent_color: String,
    pub font: AppearanceFont,
    pub density: AppearanceDensity,
    pub radius: AppearanceLevel,
    pub shadow: AppearanceLevel,
    pub motion: AppearanceMotion,
    #[serde(default)]
    pub custom_font: Option<AppearanceCustomFont>,
    pub background: Option<AppearanceBackground>,
    pub logo: Option<AppearanceLogo>,
}

impl Default for AppearanceConfig {
    fn default() -> Self {
        Self {
            schema_version: APPEARANCE_SCHEMA_VERSION,
            team_theme: None,
            brand_name: default_brand_name(),
            style: AppearanceStyle::Paper,
            palette: AppearancePalette::Terracotta,
            accent_color: "#d97757".into(),
            font: AppearanceFont::Humanist,
            density: AppearanceDensity::Standard,
            radius: AppearanceLevel::Soft,
            shadow: AppearanceLevel::Soft,
            motion: AppearanceMotion::Full,
            custom_font: None,
            background: None,
            logo: None,
        }
    }
}

fn default_brand_name() -> String {
    "Local Arena".into()
}

#[derive(Serialize, Deserialize)]
struct AppearanceBundle {
    schema_version: u32,
    kind: String,
    exported_at_unix: u64,
    appearance: AppearanceConfig,
}

#[derive(Serialize)]
pub struct AppearanceExportResult {
    path: String,
    size_bytes: u64,
}

fn appearance_path() -> Result<PathBuf> {
    Ok(app_storage::root()?.join("personalization").join("appearance.json"))
}

fn is_hex_color(value: &str) -> bool {
    value.len() == 7
        && value.starts_with('#')
        && value[1..].bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn validate_image(data_url: &str, max_bytes: usize, label: &str) -> Result<()> {
    let (prefix, encoded) = data_url
        .split_once(',')
        .ok_or_else(|| AppError::invalid(format!("{label} must be a base64 image")))?;
    let mime = prefix
        .strip_prefix("data:")
        .and_then(|value| value.strip_suffix(";base64"))
        .ok_or_else(|| AppError::invalid(format!("{label} must be a base64 image")))?;
    if !matches!(mime, "image/png" | "image/jpeg" | "image/webp") {
        return Err(AppError::invalid(format!("{label} must be PNG, JPEG, or WebP")));
    }
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(encoded)
        .map_err(|_| AppError::invalid(format!("{label} image data is invalid")))?;
    if bytes.is_empty() || bytes.len() > max_bytes {
        return Err(AppError::invalid(format!("{label} image exceeds the size limit")));
    }
    let valid_magic = match mime {
        "image/png" => bytes.starts_with(b"\x89PNG\r\n\x1a\n"),
        "image/jpeg" => bytes.starts_with(b"\xff\xd8\xff"),
        "image/webp" => bytes.len() >= 12 && &bytes[..4] == b"RIFF" && &bytes[8..12] == b"WEBP",
        _ => false,
    };
    if !valid_magic {
        return Err(AppError::invalid(format!("{label} content does not match its image type")));
    }
    Ok(())
}

fn validate_font(font: &AppearanceCustomFont) -> Result<()> {
    let (prefix, encoded) = font
        .data_url
        .split_once(',')
        .ok_or_else(|| AppError::invalid("Custom font must be a base64 font"))?;
    let expected_mime = format!("font/{}", font.format);
    if !matches!(font.format.as_str(), "ttf" | "otf" | "woff" | "woff2")
        || prefix != format!("data:{expected_mime};base64")
    {
        return Err(AppError::invalid("Custom font must be TTF, OTF, WOFF, or WOFF2"));
    }
    let name = font.file_name.trim();
    if name.is_empty()
        || name.chars().count() > 128
        || name.chars().any(char::is_control)
        || name.contains(['/', '\\', ':'])
    {
        return Err(AppError::invalid("Custom font file name is invalid"));
    }
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(encoded)
        .map_err(|_| AppError::invalid("Custom font data is invalid"))?;
    if bytes.is_empty() || bytes.len() > MAX_FONT_BYTES {
        return Err(AppError::invalid("Custom font exceeds the 24 MiB size limit"));
    }
    let valid_magic = match font.format.as_str() {
        "ttf" => bytes.starts_with(&[0x00, 0x01, 0x00, 0x00]) || bytes.starts_with(b"true"),
        "otf" => bytes.starts_with(b"OTTO"),
        "woff" => bytes.starts_with(b"wOFF"),
        "woff2" => bytes.starts_with(b"wOF2"),
        _ => false,
    };
    if !valid_magic {
        return Err(AppError::invalid("Custom font content does not match its file type"));
    }
    Ok(())
}

fn validate(config: &AppearanceConfig) -> Result<()> {
    if config.schema_version != APPEARANCE_SCHEMA_VERSION {
        return Err(AppError::invalid("Unsupported appearance schema"));
    }
    if !is_hex_color(&config.accent_color) {
        return Err(AppError::invalid("Appearance accent must use #RRGGBB"));
    }
    if let Some(team_theme) = config.team_theme.as_deref()
        && !matches!(team_theme, "falcons" | "vitality" | "furia" | "spirit" | "navi" | "g2" | "mouz" | "faze" | "tyloo")
    {
        return Err(AppError::invalid("Unknown team appearance theme"));
    }
    let brand_name = config.brand_name.trim();
    if brand_name.is_empty()
        || brand_name.chars().count() > 32
        || brand_name.chars().any(char::is_control)
    {
        return Err(AppError::invalid("Appearance name must contain 1 to 32 visible characters"));
    }
    if let Some(background) = &config.background {
        if !matches!(background.fit.as_str(), "cover" | "contain")
            || background.position_x > 100
            || background.position_y > 100
            || background.dim > 85
            || background.blur > 12
        {
            return Err(AppError::invalid("Background appearance values are out of range"));
        }
        validate_image(&background.data_url, MAX_BACKGROUND_BYTES, "Background")?;
    }
    if let Some(logo) = &config.logo {
        if !matches!(logo.fit.as_str(), "cover" | "contain")
            || !matches!(logo.shape.as_str(), "rounded" | "square" | "circle")
        {
            return Err(AppError::invalid("Logo appearance values are invalid"));
        }
        validate_image(&logo.data_url, MAX_LOGO_BYTES, "Logo")?;
    }
    if matches!(config.font, AppearanceFont::Custom) && config.custom_font.is_none() {
        return Err(AppError::invalid("Custom font selection requires an uploaded font"));
    }
    if let Some(font) = &config.custom_font {
        validate_font(font)?;
    }
    Ok(())
}

fn read() -> Result<AppearanceConfig> {
    let path = appearance_path()?;
    if !path.is_file() {
        return Ok(AppearanceConfig::default());
    }
    let config: AppearanceConfig = serde_json::from_slice(&fs::read(path)?)?;
    validate(&config)?;
    Ok(config)
}

fn write(config: &AppearanceConfig) -> Result<()> {
    validate(config)?;
    let bytes = serde_json::to_vec_pretty(config)?;
    atomic_fs::write_replace(&appearance_path()?, &bytes).map_err(AppError::transaction_io)
}

fn theme_path(value: &str, operation: &str) -> Result<PathBuf> {
    let path = PathBuf::from(value);
    if !path.is_absolute() || path.extension().and_then(|value| value.to_str()) != Some("latheme") {
        return Err(AppError::invalid(format!("{operation} requires an absolute .latheme path")));
    }
    let parent = path.parent().ok_or_else(|| AppError::invalid(format!("{operation} path has no parent")))?;
    if !parent.is_dir() {
        return Err(AppError::invalid(format!("{operation} directory does not exist")));
    }
    Ok(path)
}

#[tauri::command]
pub fn get_appearance() -> Result<AppearanceConfig> {
    read()
}

#[tauri::command]
pub fn save_appearance(config: AppearanceConfig) -> Result<AppearanceConfig> {
    write(&config)?;
    Ok(config)
}

#[tauri::command]
pub fn export_appearance(destination: String) -> Result<AppearanceExportResult> {
    let destination = theme_path(&destination, "Theme export")?;
    let bundle = AppearanceBundle {
        schema_version: THEME_SCHEMA_VERSION,
        kind: THEME_KIND.into(),
        exported_at_unix: SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs(),
        appearance: read()?,
    };
    let bytes = serde_json::to_vec_pretty(&bundle)?;
    atomic_fs::write_replace(&destination, &bytes).map_err(AppError::transaction_io)?;
    Ok(AppearanceExportResult {
        path: destination.to_string_lossy().into_owned(),
        size_bytes: bytes.len() as u64,
    })
}

#[tauri::command]
pub fn import_appearance(source: String) -> Result<AppearanceConfig> {
    let source = theme_path(&source, "Theme import")?;
    let metadata = fs::metadata(&source)?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > MAX_THEME_BYTES {
        return Err(AppError::invalid("Theme must be a non-empty .latheme file no larger than 64 MiB"));
    }
    let bundle: AppearanceBundle = serde_json::from_slice(&fs::read(source)?)?;
    if bundle.schema_version != THEME_SCHEMA_VERSION || bundle.kind != THEME_KIND {
        return Err(AppError::invalid("Unsupported Local Arena theme"));
    }
    validate(&bundle.appearance)?;
    let path = appearance_path()?;
    let backup = path.with_extension("v1.bak");
    if path.is_file() && !backup.exists() {
        fs::copy(&path, backup)?;
    }
    write(&bundle.appearance)?;
    Ok(bundle.appearance)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_is_terracotta() {
        let config = AppearanceConfig::default();
        assert!(matches!(config.palette, AppearancePalette::Terracotta));
        assert_eq!(config.accent_color, "#d97757");
        assert_eq!(config.brand_name, "Local Arena");
        assert!(config.team_theme.is_none());
        validate(&config).unwrap();
    }

    #[test]
    fn team_theme_is_allowlisted() {
        let mut config = AppearanceConfig::default();
        config.team_theme = Some("furia".into());
        validate(&config).unwrap();
        config.team_theme = Some("unknown-team".into());
        assert!(validate(&config).is_err());
    }

    #[test]
    fn invalid_accent_is_rejected() {
        let mut config = AppearanceConfig::default();
        config.accent_color = "orange".into();
        assert!(validate(&config).is_err());
    }

    #[test]
    fn custom_font_requires_matching_font_bytes() {
        let encoded = base64::engine::general_purpose::STANDARD.encode(b"wOF2test-font");
        let mut config = AppearanceConfig::default();
        config.font = AppearanceFont::Custom;
        config.custom_font = Some(AppearanceCustomFont {
            data_url: format!("data:font/woff2;base64,{encoded}"),
            file_name: "preview.woff2".into(),
            format: "woff2".into(),
        });
        validate(&config).unwrap();
        config.custom_font.as_mut().unwrap().format = "ttf".into();
        assert!(validate(&config).is_err());
    }
}
