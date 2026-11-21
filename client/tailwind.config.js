const { tailwindColors } = require("./components/theme/palette.ts");
const { WEB_SCREENS } = require("./lib/surface-breakpoints.ts");

/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ["./app/**/*.{ts,tsx}", "./src/**/*.{ts,tsx}", "./components/**/*.{ts,tsx}", "./lib/**/*.{ts,tsx}"],
  // No dark: variants are used: colors come from the palette's CSS variables, switched by ThemeProvider.
  darkMode: "class",
  presets: [require("nativewind/preset")],
  theme: {
    screens: WEB_SCREENS,
    extend: {
      colors: tailwindColors(),
      borderRadius: { sm: "6px", md: "8px", lg: "12px", xl: "16px" },
      fontFamily: {
        sans: ["system-ui", "-apple-system", "Segoe UI", "Roboto", "Helvetica Neue", "sans-serif"],
        mono: ["ui-monospace", "SFMono-Regular", "Menlo", "Consolas", "monospace"],
      },
    },
  },
  plugins: [],
};
