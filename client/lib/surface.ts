import { createContext, createElement, useContext, useSyncExternalStore, type ComponentType } from "react";
import { useWindowDimensions } from "react-native";
import { DESKTOP_MIN_WIDTH } from "./surface-breakpoints";

/*
 * The surface a view is drawn for. Workstream runs in the browser today, so a surface is a form factor: the
 * web phone or the web desktop, decided here from the window, below the controller, so a resized window
 * swaps the view while the controller and its state stay mounted. This is the only place that reads the
 * window to decide a layout. A phone app would add its OS's files to each screen folder and its index file,
 * and nothing above the view would change.
 */

export type FormFactor = "phone" | "desktop";

export function formFactorOf(width: number): FormFactor {
  return width >= DESKTOP_MIN_WIDTH ? "desktop" : "phone";
}

const FormFactorOverride = createContext<FormFactor | null>(null);

/** Forces a form factor, for a test that renders one surface. */
export const FormFactorOverrideProvider = FormFactorOverride.Provider;

// The server has no window. A page it renders is drawn for a phone there and on the browser's first pass
// over that HTML, so the two match; the real window is read from the next render on.
const subscribeToHydration = () => () => {};
const inBrowser = () => true;
const onServer = () => false;

/** The form factor of the window now; re-renders as the window changes. */
export function useFormFactor(): FormFactor {
  const override = useContext(FormFactorOverride);
  const { width } = useWindowDimensions();
  const hydrated = useSyncExternalStore(subscribeToHydration, inBrowser, onServer);
  return override ?? (hydrated ? formFactorOf(width) : "phone");
}

export type SurfaceFiles<P> = {
  phone: ComponentType<P>;
  /** Falls back to `phone`. */
  desktop?: ComponentType<P>;
};

/**
 * One component that draws the file for the form factor of the window. Used by a screen folder's index file
 * only: `export const TeamScreen = pick({ phone: WebPhone, desktop: WebDesktop })`.
 */
export function pick<P extends object>(files: SurfaceFiles<P>): ComponentType<P> {
  function Picked(props: P) {
    const formFactor = useFormFactor();
    return createElement((formFactor === "desktop" ? files.desktop : undefined) ?? files.phone, props);
  }
  Picked.displayName = `Surface(${files.phone.displayName ?? files.phone.name})`;
  return Picked;
}
