use std::sync::OnceLock;

pub const SEMVER: &str = env!("CARGO_PKG_VERSION");

pub fn display() -> &'static str {
    static DISPLAY: OnceLock<String> = OnceLock::new();
    DISPLAY.get_or_init(|| {
        if let Some((core, suffix)) = SEMVER.split_once("-preview.") {
            let Some((preview, revision)) = suffix.split_once('+') else {
                return SEMVER.to_string();
            };
            return format!("{core}.{revision}-Preview.{preview}");
        }
        if let Some((core, revision)) = SEMVER.split_once('+') {
            return format!("{core}.{revision}");
        }
        SEMVER.to_string()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preview_semver_maps_to_the_four_part_display_version() {
        assert_eq!(display(), "1.4.2.6-Preview.2");
    }
}
