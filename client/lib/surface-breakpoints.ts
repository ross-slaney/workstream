/*
 * Where one surface ends and the next begins. Plain numbers in a file with no
 * imports, so tailwind.config.js (Node, no React Native) and lib/surface.ts
 * (the app) read the same ones.
 */

/** Web: a window at least this wide gets the desktop surface; narrower, the web phone surface. Tailwind's `md:` is the same number. */
export const DESKTOP_MIN_WIDTH = 768;

/** The desktop's breakpoints beyond that, for `lg:` and `xl:` in web surface files. */
export const WEB_SCREENS = { md: `${DESKTOP_MIN_WIDTH}px`, lg: "1024px", xl: "1280px" } as const;
