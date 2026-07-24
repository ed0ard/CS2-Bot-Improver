import React from "react";
import ReactDOM from "react-dom/client";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import {
  getCurrentWindow,
  currentMonitor,
  LogicalSize,
} from "@tauri-apps/api/window";
import App from "./App";
import { ToastProvider } from "./components/Toast";
import { AppStateProvider } from "./state/store";
import { AppearanceProvider } from "./state/appearance";
import { api } from "./lib/api";
import { applyAppearance, DEFAULT_APPEARANCE } from "./lib/appearance";
import { translate } from "./i18n";
import "./styles/global.css";

// Desktop Local Arena layout: fixed logical canvas with persistent navigation
// rail and a wide work area. Smaller displays scale the complete tool while
// preserving the two-column information architecture.
const DESIGN_W = 1300;
const DESIGN_H = 800;
const MAX_ZOOM = 1;
const MIN_ZOOM = 0.7;
const SCREEN_FILL = 0.88;
const TASKBAR_RESERVE = 48;

// The window is created hidden (visible:false). We size + centre it while still
// hidden, then add the `app-ready` class (kicks off the liquid entrance) and
// reveal it — so it appears already at its final size/position with no resize or
// recenter jump. `reveal` is idempotent and guarded by a timeout safety net so a
// hung/denied sizing call can never leave the window invisible.
let revealed = false;
function reveal() {
  if (revealed) return;
  revealed = true;
  document.documentElement.classList.add("app-ready");
  getCurrentWindow().show().catch(() => {});
}

async function fitWindowToScreen() {
  const win = getCurrentWindow();
  await win.setFullscreen(false).catch(() => {});
  await win.unmaximize().catch(() => {});

  let zoom = MAX_ZOOM;
  try {
    const mon = await currentMonitor();
    if (mon) {
      const sf = mon.scaleFactor || 1;
      const logicalW = mon.size.width / sf;
      const logicalH = Math.max(0, mon.size.height / sf - TASKBAR_RESERVE);
      // Preserve visible desktop margins even on high-DPI displays where the
      // 1300 x 800 logical canvas would otherwise look almost maximized.
      const targetW = logicalW * SCREEN_FILL;
      const targetH = logicalH * SCREEN_FILL;
      zoom = Math.min(MAX_ZOOM, targetH / DESIGN_H, targetW / DESIGN_W);
      zoom = Math.max(MIN_ZOOM, zoom);
    }
  } catch {
    /* monitor unavailable — keep MAX_ZOOM and the default window size */
  }
  await getCurrentWebview()
    .setZoom(zoom)
    .catch(() => {});
  try {
    await win.setSize(
      new LogicalSize(Math.round(DESIGN_W * zoom), Math.round(DESIGN_H * zoom))
    );
    await win.center();
  } catch {
    /* sizing not permitted — fall back to the configured window size */
  }
  reveal();
}

// Disable every right-click menu (native and custom). On some machines the
// custom menu's scrim could trap pointer events ("window looks frozen until you
// left-click"); suppressing the context menu entirely removes that class of bug.
document.addEventListener("contextmenu", (e) => e.preventDefault());

// Disable the in-page browser find (Ctrl/⌘+F and F3) — it has no place in a
// desktop control panel and confused users. Nothing else uses these keys.
document.addEventListener(
  "keydown",
  (e) => {
    const find =
      ((e.ctrlKey || e.metaKey) && (e.key === "f" || e.key === "F")) ||
      e.key === "F3";
    if (find) e.preventDefault();
  },
  { capture: true }
);

async function bootstrap() {
  applyAppearance(DEFAULT_APPEARANCE);
  try {
    const memory = await api.getPanelMemory();
    for (const [key, value] of Object.entries(memory.entries)) {
      if (key.startsWith("cs2bi.")) localStorage.setItem(key, value);
    }
  } catch {
    // A read-only application directory must not prevent the recovery UI from opening.
  }

  ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
    <React.StrictMode>
      <ToastProvider>
        <AppStateProvider>
          <AppearanceProvider>
            <App />
          </AppearanceProvider>
        </AppStateProvider>
      </ToastProvider>
    </React.StrictMode>
  );
}

function renderStartupError(error: unknown) {
  reveal();
  const root = document.getElementById("root");
  if (!root) return;
  const panel = document.createElement("section");
  panel.setAttribute("role", "alert");
  panel.style.cssText =
    "margin:32px;padding:24px;border:1px solid #ff3b30;border-radius:14px;background:#fff;color:#1c1c1e;font:14px/1.5 'Segoe UI',sans-serif";
  const title = document.createElement("strong");
  title.textContent = translate(localStorage.getItem("cs2bi.language"), "startup.failed");
  const detail = document.createElement("pre");
  detail.style.cssText = "margin:12px 0 0;white-space:pre-wrap;overflow-wrap:anywhere";
  detail.textContent = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
  panel.append(title, detail);
  root.replaceChildren(panel);
}

async function start() {
  void fitWindowToScreen();
  // Safety net: never leave the window hidden if sizing hangs or is denied.
  setTimeout(reveal, 1200);
  await bootstrap();
}

void start().catch((error) => {
  console.error("[panel-startup]", error);
  renderStartupError(error);
});
