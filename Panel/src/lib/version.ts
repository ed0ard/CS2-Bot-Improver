import packageJson from "../../package.json";

export const APP_SEMVER = packageJson.version;

export function displayVersion(version: string): string {
  const preview = /^(\d+)\.(\d+)\.(\d+)-preview\.(\d+)\+(\d+)$/i.exec(version);
  if (preview) {
    const [, major, minor, patch, previewNumber, releaseRevision] = preview;
    return `${major}.${minor}.${patch}.${releaseRevision}-Preview.${previewNumber}`;
  }
  const release = /^(\d+)\.(\d+)\.(\d+)\+(\d+)$/.exec(version);
  if (release) {
    const [, major, minor, patch, releaseRevision] = release;
    return `${major}.${minor}.${patch}.${releaseRevision}`;
  }
  return version;
}

export const APP_DISPLAY_VERSION = displayVersion(APP_SEMVER);
