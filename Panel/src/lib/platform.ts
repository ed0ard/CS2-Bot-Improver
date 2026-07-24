import { listen as tauriListen } from "@tauri-apps/api/event";
import { writeText as tauriWriteText } from "@tauri-apps/plugin-clipboard-manager";
import { open as tauriOpen, save as tauriSave } from "@tauri-apps/plugin-dialog";
import { openPath as tauriOpenPath, openUrl as tauriOpenUrl } from "@tauri-apps/plugin-opener";

export async function openDialog(options: Parameters<typeof tauriOpen>[0]) {
  return tauriOpen(options);
}

export async function saveDialog(options: Parameters<typeof tauriSave>[0]) {
  return tauriSave(options);
}

export async function writeClipboard(text: string) {
  return tauriWriteText(text);
}

export async function openExternalUrl(url: string) {
  return tauriOpenUrl(url);
}

export async function openExternalPath(path: string) {
  return tauriOpenPath(path);
}

export function listenAppEvent<T>(name: string, handler: (event: { payload: T }) => void) {
  return tauriListen<T>(name, (event) => handler({ payload: event.payload }));
}
