import { ScrollViewStyleReset, useServerDocumentContext } from "expo-router/html";
import type { ReactNode } from "react";
import { palettes } from "@/components/theme/palette";

/*
 * The HTML document around every page. Runs on the server only, for each request (web.output: "server"): no
 * DOM, no browser APIs, no global CSS. Everything from useServerDocumentContext has to be rendered, or
 * hydration breaks. Titles and descriptions come from each route's generateMetadata.
 */
export default function Root({ children }: { children: ReactNode }) {
  const { bodyAttributes, bodyNodes, htmlAttributes, headNodes } = useServerDocumentContext();
  return (
    <html lang="en" {...htmlAttributes}>
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1, shrink-to-fit=no" />
        <meta name="color-scheme" content="light dark" />
        {/* The page itself never scrolls; each page scrolls inside its own frame. */}
        <ScrollViewStyleReset />
        {/* Raw CSS so the background and the typeface are right before the app's styles load. */}
        <style dangerouslySetInnerHTML={{ __html: firstPaint }} />
        {headNodes}
      </head>
      <body {...bodyAttributes}>
        {children}
        {bodyNodes}
      </body>
    </html>
  );
}

/** The system's own interface typeface: nothing to download, and at home on every computer a desktop is watched from. */
const firstPaint = `
body {
  background-color: ${palettes.light.background};
  font-family: system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", sans-serif;
  -webkit-font-smoothing: antialiased;
}
@media (prefers-color-scheme: dark) {
  body { background-color: ${palettes.dark.background}; }
}`;
