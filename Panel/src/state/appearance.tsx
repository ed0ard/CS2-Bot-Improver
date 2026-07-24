import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { api, type AppearanceConfig } from "../lib/api";
import { applyAppearance, DEFAULT_APPEARANCE, normalizeAppearance } from "../lib/appearance";
import { useStore } from "./store";

type AppearanceState = {
  appearance: AppearanceConfig;
  ready: boolean;
  updateAppearance: (next: AppearanceConfig | ((current: AppearanceConfig) => AppearanceConfig)) => void;
  replaceAppearance: (next: AppearanceConfig) => void;
  resetAppearance: () => void;
};

const AppearanceContext = createContext<AppearanceState | null>(null);

export function AppearanceProvider({ children }: { children: ReactNode }) {
  const { reportError } = useStore();
  const [appearance, setAppearance] = useState<AppearanceConfig>(() => structuredClone(DEFAULT_APPEARANCE));
  const [ready, setReady] = useState(false);
  const saveGeneration = useRef(0);

  useEffect(() => {
    let active = true;
    void api.getAppearance()
      .then((saved) => {
        if (!active) return;
        const normalized = normalizeAppearance(saved);
        setAppearance(normalized);
        applyAppearance(normalized);
      })
      .catch(reportError)
      .finally(() => { if (active) setReady(true); });
    return () => { active = false; };
  }, [reportError]);

  useEffect(() => {
    applyAppearance(appearance);
    if (!ready) return;
    const generation = ++saveGeneration.current;
    const timeout = window.setTimeout(() => {
      void api.saveAppearance(appearance).catch((error) => {
        if (generation === saveGeneration.current) reportError(error);
      });
    }, 260);
    return () => window.clearTimeout(timeout);
  }, [appearance, ready, reportError]);

  const updateAppearance = useCallback((next: AppearanceConfig | ((current: AppearanceConfig) => AppearanceConfig)) => {
    setAppearance((current) => normalizeAppearance(typeof next === "function" ? next(current) : next));
  }, []);
  const replaceAppearance = useCallback((next: AppearanceConfig) => setAppearance(normalizeAppearance(next)), []);
  const resetAppearance = useCallback(() => setAppearance(structuredClone(DEFAULT_APPEARANCE)), []);
  const value = useMemo(() => ({ appearance, ready, updateAppearance, replaceAppearance, resetAppearance }), [appearance, ready, updateAppearance, replaceAppearance, resetAppearance]);

  return <AppearanceContext.Provider value={value}>{children}</AppearanceContext.Provider>;
}

export function useAppearance() {
  const value = useContext(AppearanceContext);
  if (!value) throw new Error("useAppearance must be used within AppearanceProvider");
  return value;
}
