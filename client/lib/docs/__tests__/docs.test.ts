import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";

import docsWords from "@/locales/docs/en.json";
import landingWords from "@/locales/landing/en.json";

import { docsMetadata, docsPaths, resolveDocsPage, slugsOf } from "../page";
import { docsSource } from "../source";

/*
 * The docs pages are content/docs/<slug>/en.mdx, read by @agenetix/docs/source and compiled by @bacons/mdx on
 * the server. These tests run against the real content, so a page that stops compiling, loses its
 * description, uses a component the pages do not have, or links to a page or heading that no longer exists
 * fails here before it reaches the website.
 */

type Node = { type: string; name?: string; props?: Record<string, unknown>; children?: Node[]; value?: string };

function elements(node: Node, name: string): Node[] {
  const own = node.type === "element" && node.name === name ? [node] : [];
  return [...own, ...(node.children ?? []).flatMap((child) => elements(child, name))];
}

function names(node: Node): string[] {
  const own = node.type === "element" && node.name ? [node.name] : [];
  return [...own, ...(node.children ?? []).flatMap(names)];
}

const pages = [
  "/docs",
  "/docs/how-it-works",
  "/docs/desktops",
  "/docs/ai",
  "/docs/running-paused-archived",
  "/docs/environments",
  "/docs/github",
  "/docs/sharing",
  "/docs/usage-and-limits",
  "/docs/command-line",
  "/docs/security",
];

const slugsOfHref = (href: string) => href.split("/").slice(2);

async function page(href: string) {
  const resolved = await resolveDocsPage(slugsOfHref(href));
  if (resolved.type !== "page") throw new Error(`no page at ${href}`);
  return resolved.data;
}

describe("the docs pages", () => {
  it("are the folders under content/docs, every one with a title and a description", () => {
    const entries = docsSource.getAllEntries();
    expect(entries.map((entry) => entry.href).sort()).toEqual([...pages].sort());
    expect(docsPaths()).toEqual(pages);
    for (const entry of entries) {
      expect(entry.title, entry.href).not.toBe("");
      expect(entry.description, entry.href).not.toBe("");
      expect(entry.filePath.endsWith("/en.mdx"), entry.href).toBe(true);
    }
    // One folder a page, and nothing in content/docs that is not a page.
    expect(readdirSync(path.join(process.cwd(), "content/docs")).sort()).toEqual(
      entries.map((entry) => (entry.isHome ? docsSource.config.docsIndexSlug : entry.slug)).sort(),
    );
  });

  it("reads the slug from the route's params", () => {
    expect(slugsOf({})).toEqual([]);
    expect(slugsOf({ slug: ["desktops"] })).toEqual(["desktops"]);
    expect(slugsOf({ slug: "desktops" })).toEqual(["desktops"]);
  });

  it("lists the pages in reading order, and steps through them with previous and next", async () => {
    const home = await page("/docs");
    expect(home.entry.isHome).toBe(true);
    expect(home.navigation.map((section) => [section.label, section.items.map((item) => item.href)])).toEqual([[docsWords.sections.guide, pages.slice(1)]]);

    for (const [index, href] of pages.slice(1).entries()) {
      const data = await page(href);
      expect(data.previous?.href ?? null, href).toBe(index === 0 ? null : pages[index]);
      expect(data.next?.href ?? null, href).toBe(pages[index + 2] ?? null);
    }
  });

  it("compiles every page to markup with its headings, each carrying the id a link can point at", async () => {
    for (const href of pages) {
      const data = await page(href);
      expect(data.entry.href).toBe(href);
      expect(data.entry.title, href).not.toBe("");
      expect(data.entry.updated, href).toMatch(/^Updated [A-Z][a-z]+ \d{1,2}, \d{4}$/);
      const headings = docsSource.getEntry(slugsOfHref(href))!.headings.filter((heading) => heading.level === 2);
      expect(headings.length, href).toBeGreaterThan(1);
      const headingIds = elements(data.compiled.tree as Node, "html.h2").map((heading) => heading.props?.id);
      for (const heading of headings) expect(headingIds, `${href} ${heading.text}`).toContain(heading.id);
      // Nothing in the tree is code: it is data the browser renders with the website's components.
      expect(JSON.stringify(data.compiled), href).not.toContain("function");
    }
  });

  it("writes pages only with elements and components the website draws", async () => {
    // The map at the foot of src/docs/docs-mdx.tsx, read as text: the file itself needs React Native to load.
    const source = readFileSync(path.join(process.cwd(), "src/docs/docs-mdx.tsx"), "utf8");
    const map = source.slice(source.indexOf("export const docsComponents"));
    const used = new Set<string>();
    for (const href of pages) for (const name of names((await page(href)).compiled.tree as Node)) used.add(name);
    const components = [...used].filter((name) => !name.startsWith("html."));
    expect(components.sort()).toEqual(["Flow", "Stage"]);
    for (const name of components) expect(map, name).toMatch(new RegExp(`\\n  ${name},`));
  });

  it("keeps the notes to whoever edits a page out of what a reader gets", async () => {
    for (const href of pages) expect(JSON.stringify((await page(href)).compiled), href).not.toContain("PENDING");
  });

  it("links only to pages and headings that exist", async () => {
    const headingsOf = new Map<string, string[]>();
    for (const href of pages) headingsOf.set(href, elements((await page(href)).compiled.tree as Node, "html.h2").map((heading) => String(heading.props?.id)));
    for (const href of pages) {
      const data = await page(href);
      const links = elements(data.compiled.tree as Node, "html.a").map((link) => link.props?.href).filter((link): link is string => typeof link === "string");
      for (const link of links.filter((link) => link.startsWith("/") || link.startsWith("#"))) {
        const [target, anchor] = link.startsWith("#") ? [href, link.slice(1)] : link.split("#");
        expect(pages, `${href} links ${link}`).toContain(target);
        if (anchor) expect(headingsOf.get(target), `${href} links ${link}`).toContain(anchor);
      }
    }
    // The front page links every other page.
    const home = elements((await page("/docs")).compiled.tree as Node, "html.a").map((link) => link.props?.href);
    for (const href of pages.slice(1)) expect(home, href).toContain(href);
  });

  it("answers an unknown address with nothing, so the route can 404", async () => {
    expect(await resolveDocsPage(["nothing", "here"])).toEqual({ type: "notFound" });
    expect(docsMetadata(["nothing"])).toBeNull();
  });

  it("gives every page a title and a description for its <head>", () => {
    expect(docsMetadata([])).toMatchObject({ href: "/docs", title: docsWords.siteTitle });
    const titles = new Set<string>();
    for (const href of pages) {
      const meta = docsMetadata(slugsOfHref(href));
      expect(meta, href).not.toBeNull();
      expect(meta!.title, href).toContain("Workstream");
      expect(meta!.description.length, href).toBeGreaterThan(40);
      expect(meta!.description.length, href).toBeLessThanOrEqual(170);
      titles.add(meta!.title);
    }
    expect(titles.size).toBe(pages.length);
  });

  it("has the frame's words, and the landing page's link to the docs", () => {
    const leaves = (value: unknown): string[] => (typeof value === "string" ? [value] : Object.values(value as object).flatMap(leaves));
    for (const word of leaves(docsWords)) expect(word.trim()).not.toBe("");
    expect(landingWords.nav.docs).toBe("Docs");
  });
});
