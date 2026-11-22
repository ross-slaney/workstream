import { Redirect, useLoaderData, type Href } from "expo-router";
import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";
import { StatusError } from "expo-server";

import { translator } from "@/lib/i18n";
import { DocsPage } from "@/src/docs/docs-page";
import { docsLinks } from "@/src/docs/docs-links";

/*
 * /docs/<slug>: one docs page, rendered on the server for each request from content/docs/<slug>/en.mdx. An
 * unknown address is a real 404. The reader (lib/docs) is loaded inside the server-only functions, so Node's
 * file system never reaches the browser bundle. Public: anyone reads it signed out.
 */

type Params = Record<string, string | string[] | undefined>;

export async function generateMetadata(_request: unknown, params: Params): Promise<Metadata | null> {
  const { docsMetadata, slugsOf } = await import("@/lib/docs/page");
  const page = docsMetadata(slugsOf(params));
  if (!page) return null;
  return { title: page.title, description: page.description, openGraph: { title: page.title, description: page.description, type: "article" } };
}

/** Runs on the server and is stripped from the client; the page reads it with useLoaderData. */
export async function loader(_request: unknown, params: Params) {
  const { resolveDocsPage, slugsOf } = await import("@/lib/docs/page");
  const resolved = await resolveDocsPage(slugsOf(params));
  if (resolved.type === "notFound") throw new StatusError(404, translator("docs")("notFound"));
  if (resolved.type === "redirect") return { page: null, redirect: resolved.href };
  return { page: resolved.data, redirect: null };
}

export default function DocsRoute() {
  const { page, redirect } = useLoaderData<typeof loader>();
  if (redirect || !page) return <Redirect href={(redirect ?? docsLinks.docs) as Href} />;
  return (
    <>
      <Head>
        <title>{page.entry.metaTitle}</title>
      </Head>
      <DocsPage links={docsLinks} page={page} />
    </>
  );
}
