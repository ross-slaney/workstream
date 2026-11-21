import { vars } from "nativewind";
import { createContext, useContext, useSyncExternalStore, type ReactNode } from "react";
import { View, useColorScheme } from "react-native";
import { cssVariables, palettes, type ColorScheme, type Palette } from "./palette";

export type Theme = {
  scheme: ColorScheme;
  /** Color values, for props a class cannot reach: an icon's `color`, a canvas's background. */
  colors: Palette;
};

const themes: Record<ColorScheme, Theme & { variables: ReturnType<typeof vars> }> = {
  light: { scheme: "light", colors: palettes.light, variables: vars(cssVariables("light")) },
  dark: { scheme: "dark", colors: palettes.dark, variables: vars(cssVariables("dark")) },
};

// Match the server on the first hydration render, then apply the device's preference. React otherwise leaves
// mismatched server style attributes in place until a later theme change.
const subscribeToHydration = () => () => {};
const clientSnapshot = () => true;
const serverSnapshot = () => false;

const ThemeContext = createContext<Theme>(themes.light);

/*
 * Applies one palette to everything beneath it: the CSS variables behind every Tailwind color class, and
 * `useTheme()` for color props. Follows the system's appearance unless `scheme` is given.
 */
export function ThemeProvider({ scheme, children }: { scheme?: ColorScheme; children: ReactNode }) {
  const system = useColorScheme();
  const hydrated = useSyncExternalStore(subscribeToHydration, clientSnapshot, serverSnapshot);
  const theme = themes[hydrated ? scheme ?? (system === "dark" ? "dark" : "light") : "light"];
  return (
    <ThemeContext.Provider value={theme}>
      <View style={[{ flex: 1, backgroundColor: theme.colors.background }, theme.variables]}>{children}</View>
    </ThemeContext.Provider>
  );
}

export function useTheme(): Theme {
  return useContext(ThemeContext);
}
