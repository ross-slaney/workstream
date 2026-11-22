import fs from "node:fs";

import type { DocsHeading, DocsNavItem, DocsNavSection } from "@agenetix/docs/source";
import { slugifyHeading } from "@agenetix/docs/source";
import type { CompiledMDX } from "@bacons/mdx";
import { compileMDX } from "@bacons/mdx/server";
import matter from "gray-matter";
import remarkGfm from "remark-gfm";

import { translator } from "../i18n";
import { docsSource } from "./source";

/*
 * What a docs page's loader hands the view: the page compiled to a JSON tree (no code, so it can be embedded
 * in the HTML response and hydrated), the list of pages, the table of contents and the previous and next
 * pages. Compiling happens here on the server; the browser only renders the tree.
 */

export type DocsPageMeta = {
  title: string;
  /** The browser tab's title: the page's name and the site's, "Desktops · Workstream Docs". */
  metaTitle: string;
  description: string;
  href: string;
  isHome: boolean;
  /** "Updated November 21, 2026", from the page's `updated` frontmatter. */
  updated: string | null;
};

export type DocsPageData = {
  entry: DocsPageMeta;
  compiled: CompiledMDX;
  headings: DocsHeading[];
  navigation: DocsNavSection[];
  previous: DocsNavItem | null;
  next: DocsNavItem | null;
};

export type DocsPageResolution =
  | { type: "page"; data: DocsPageData }
  | { type: "redirect"; href: string }
  | { type: "notFound" };

/** The slug segments of a request: none for /docs, ["desktops"] for /docs/desktops. */
export function slugsOf(params: Record<string, string | string[] | undefined> = {}): string[] {
  const slug = params.slug;
  if (slug === undefined) return [];
  return Array.isArray(slug) ? slug : slug.split("/").filter(Boolean);
}

export async function resolveDocsPage(slugs: string[]): Promise<DocsPageResolution> {
  const route = docsSource.resolveRoute(slugs);
  if (route.type === "redirect" && route.href) return { type: "redirect", href: route.href };
  if (route.type !== "entry" || !route.entry) return { type: "notFound" };
  const entry = route.entry;
  const compiled = await compileMDX(entry.content, { remarkPlugins: [remarkGfm], rehypePlugins: [rehypeHeadingIds] });
  return {
    type: "page",
    data: {
      entry: {
        title: entry.title,
        metaTitle: metaTitleOf(entry),
        description: entry.description,
        href: entry.href,
        isHome: entry.isHome,
        updated: formatUpdated(frontmatterOf(entry.filePath).updated),
      },
      compiled,
      headings: entry.headings.filter((heading) => heading.level === 2),
      navigation: docsSource.getNavigation(),
      previous: route.previousEntry ?? null,
      next: route.nextEntry ?? null,
    },
  };
}

export type DocsMetadata = { title: string; description: string; href: string };

/** Title and description for a page's <head>, or null for an address with no page. */
export function docsMetadata(slugs: string[]): DocsMetadata | null {
  const route = docsSource.resolveRoute(slugs);
  if (route.type !== "entry" || !route.entry) return null;
  return { title: metaTitleOf(route.entry), description: route.entry.description, href: route.entry.href };
}

function metaTitleOf(entry: { title: string; isHome: boolean }): string {
  return entry.isHome ? entry.title : translator("docs")("metaTitle", { title: entry.title });
}

/** Every docs page's path, the front page first, for the sitemap. */
export function docsPaths(): string[] {
  return docsSource.getAllEntries().map((entry) => entry.href);
}

function frontmatterOf(filePath: string): Record<string, unknown> {
  try {
    return matter(fs.readFileSync(filePath, "utf8")).data as Record<string, unknown>;
  } catch {
    return {};
  }
}

function formatUpdated(value: unknown): string | null {
  const date = value instanceof Date ? value : typeof value === "string" ? new Date(value) : null;
  if (!date || Number.isNaN(date.getTime())) return null;
  const words = new Intl.DateTimeFormat("en", { month: "long", day: "numeric", year: "numeric", timeZone: "UTC" }).format(date);
  return translator("docs")("updated", { date: words });
}

type HastNode = { type: string; tagName?: string; value?: string; properties?: Record<string, unknown>; children?: HastNode[] };

/*
 * Gives each heading the id the table of contents links to: the same slugifyHeading the resolver used for
 * entry.headings, so the two agree.
 */
function rehypeHeadingIds() {
  return (tree: HastNode) => {
    const visit = (node: HastNode) => {
      // @bacons/mdx has already renamed the tags "html.h2" by the time our plugin runs.
      if (node.type === "element" && node.tagName && /^(html\.)?h[1-6]$/.test(node.tagName)) {
        node.properties = { ...node.properties, id: slugifyHeading(textOf(node)) };
      }
      node.children?.forEach(visit);
    };
    visit(tree);
  };
}

function textOf(node: HastNode): string {
  if (node.type === "text") return node.value ?? "";
  return (node.children ?? []).map(textOf).join("");
}
