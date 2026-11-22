import { describe, expect, it } from "vitest";

import { publicOrigin } from "../site";
import { publicPaths, robotsTxt, sitemapXml } from "../sitemap";

describe("the sitemap", () => {
  it("lists the landing page and every docs page, once each, as full addresses", () => {
    const paths = publicPaths();
    expect(paths[0]).toBe("/");
    expect(paths).toContain("/docs");
    expect(paths).toContain("/docs/security");
    expect(paths).toHaveLength(11);
    expect(new Set(paths).size).toBe(paths.length);

    const xml = sitemapXml("https://workstream.example");
    expect(xml.match(/<url>/g)).toHaveLength(paths.length);
    expect(xml).toContain("<loc>https://workstream.example/</loc>");
    expect(xml).toContain("<loc>https://workstream.example/docs/running-paused-archived</loc>");
    expect(sitemapXml("https://workstream.example", ["/a?b=1&c=2"])).toContain("<loc>https://workstream.example/a?b=1&amp;c=2</loc>");
  });

  it("points robots at the sitemap and keeps them out of the signed-in app", () => {
    const robots = robotsTxt("https://workstream.example");
    expect(robots).toContain("Sitemap: https://workstream.example/sitemap.xml");
    for (const path of ["/sign-in", "/t/", "/home", "/admin"]) expect(robots).toContain(`Disallow: ${path}`);
    expect(robots).not.toContain("Disallow: /docs");
  });

  it("names the site by the address the visitor used, behind a proxy too", () => {
    expect(publicOrigin(new Request("http://10.0.0.4:8080/sitemap.xml"))).toBe("http://10.0.0.4:8080");
    expect(publicOrigin(new Request("http://10.0.0.4:8080/sitemap.xml", { headers: { "x-forwarded-host": "workstream.example, proxy", "x-forwarded-proto": "https" } }))).toBe("https://workstream.example");
  });
});
