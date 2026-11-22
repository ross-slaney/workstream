import { docsPaths } from "./docs/page";

/*
 * What search engines are told about the public website, and where that list is (robots.txt). The public
 * pages are the landing page and the docs. The signed-in app renders in the browser and has nothing to index,
 * so robots.txt keeps crawlers out of it. Server only: the docs pages are read from disk.
 */

export const sitemapPath = "/sitemap.xml";

/** Every public page's path: the landing page, then the docs. */
export function publicPaths(): string[] {
  return ["/", ...docsPaths()];
}

const escapeXml = (value: string) => value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

export function sitemapXml(origin: string, paths: string[] = publicPaths()): string {
  const urls = paths.map((path) => `  <url><loc>${escapeXml(new URL(path, origin).toString())}</loc></url>`).join("\n");
  return `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls}\n</urlset>\n`;
}

export function robotsTxt(origin: string): string {
  return [
    "User-agent: *",
    "Disallow: /sign-in",
    "Disallow: /auth/",
    "Disallow: /invite",
    "Disallow: /share",
    "Disallow: /device",
    "Disallow: /github",
    "Disallow: /home",
    "Disallow: /t/",
    "Disallow: /admin",
    "Disallow: /_expo/loaders/",
    "",
    `Sitemap: ${new URL(sitemapPath, origin).toString()}`,
    "",
  ].join("\n");
}
