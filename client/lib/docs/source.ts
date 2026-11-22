import path from "node:path";

import { createDocsSource } from "@agenetix/docs/source";

import { translator } from "../i18n";

/*
 * The docs pages' content: content/docs/<slug>/en.mdx, read from disk by the resolver the company's other
 * docs sites use (@agenetix/docs/source, which has no framework in it). The folder is the configuration: a
 * new page is a new folder with an en.mdx in it, placed in the list by its `order`, and this file never
 * changes for it. Server only: it reads with Node, so routes reach it from their loader and generateMetadata,
 * which never ship to the browser. The Dockerfile copies content/ beside the server for the same reason.
 *
 * English is the only language today. Another language is a <language>.mdx beside each en.mdx and one more
 * entry in `locales`, which the resolver already understands.
 */

const t = translator("docs");

export const docsSource = createDocsSource({
  contentDir: path.join(process.cwd(), "content/docs"),
  basePath: "/docs",
  defaultLocale: "en",
  locales: ["en"],
  hideDefaultLocaleInUrl: true,
  siteTitle: t("siteTitle"),
  sectionLabels: { "": t("sections.guide") },
  sectionOrder: [""],
});
