/*
 * Every color in Workstream, for both schemes. Plain data, so tailwind.config.js (Node) and the app read the
 * same file. A color is used through its Tailwind class (`bg-card`, `text-muted-foreground`), which resolves
 * to a CSS variable ThemeProvider sets; `useTheme().colors` is only for a prop a class cannot reach.
 * components/theme/__tests__/palette.test.ts fails if text on any surface drops below WCAG AA.
 */

const light = {
  /** A page you read or fill in: the conversation, a form, the website. */
  background: "#FFFFFF",
  /** The app's frame: the sidebar and the space around screens. */
  canvas: "#F3F5F8",
  foreground: "#14171F",
  card: "#FFFFFF",
  cardForeground: "#14171F",
  /** A quiet fill: a hovered row, an input, a chip. */
  muted: "#EBEEF3",
  mutedForeground: "#566070",
  border: "#DDE1E8",
  input: "#C9CED8",
  /** The one action color: the main button, a link, the selected task. */
  primary: "#1F4FE0",
  primaryForeground: "#FFFFFF",
  /** The selected row's fill, with primary text on it. */
  primarySoft: "#E6ECFD",
  destructive: "#B42318",
  destructiveForeground: "#FFFFFF",
  ring: "#1F4FE0",
  /** A desktop that is up and an agent that is working. */
  positive: "#16794A",
  /** A desktop that is starting or stopping. */
  warning: "#8A5300",
  /** Behind a desktop's picture. Dark in both schemes, so the desktop is the bright thing on screen. */
  stage: "#0C0E12",
  stageForeground: "#C6CCD8",
};

export type ColorToken = keyof typeof light;
export type Palette = Record<ColorToken, string>;

const dark: Palette = {
  background: "#12151B",
  canvas: "#0B0D11",
  foreground: "#ECEFF4",
  card: "#171B22",
  cardForeground: "#ECEFF4",
  muted: "#1F242D",
  mutedForeground: "#9BA4B4",
  border: "#272D38",
  input: "#3A4250",
  primary: "#8AA6FF",
  primaryForeground: "#0A1026",
  primarySoft: "#1B2542",
  destructive: "#FF8A80",
  destructiveForeground: "#2A0A07",
  ring: "#8AA6FF",
  positive: "#5CD69A",
  warning: "#F2B84B",
  stage: "#07080B",
  stageForeground: "#C6CCD8",
};

export type ColorScheme = "light" | "dark";
export const palettes: Record<ColorScheme, Palette> = { light, dark };
export const colorTokens = Object.keys(light) as ColorToken[];

/** `mutedForeground` → `muted-foreground`: the Tailwind color name and CSS variable suffix. */
export function tokenClassName(token: ColorToken): string {
  return token.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`);
}

/** `#1F4FE0` → `31 79 224`, the channel form `rgb(var(--x) / <alpha-value>)` needs. */
export function hexChannels(hex: string): string {
  const value = hex.replace("#", "");
  return [0, 2, 4].map((at) => parseInt(value.slice(at, at + 2), 16)).join(" ");
}

export function cssVariables(scheme: ColorScheme): Record<`--${string}`, string> {
  const palette = palettes[scheme];
  return Object.fromEntries(colorTokens.map((token) => [`--${tokenClassName(token)}`, hexChannels(palette[token])]));
}

/** Tailwind `theme.extend.colors`: every token as a class backed by its variable. */
export function tailwindColors(): Record<string, string> {
  return Object.fromEntries(colorTokens.map((token) => {
    const name = tokenClassName(token);
    return [name, `rgb(var(--${name}) / <alpha-value>)`];
  }));
}

function luminance(hex: string): number {
  const [r, g, b] = hexChannels(hex).split(" ").map((channel) => {
    const value = Number(channel) / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** The WCAG contrast ratio of two colors, 1 to 21. */
export function contrastRatio(a: string, b: string): number {
  const [lighter, darker] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (lighter + 0.05) / (darker + 0.05);
}
