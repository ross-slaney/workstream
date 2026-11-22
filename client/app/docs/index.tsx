import { Redirect, useLoaderData, type Href } from "expo-router";
import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";

import { DocsPage } from "@/src/docs/docs-page";
import { docsLinks } from "@/src/docs/docs-links";

/*
 * /docs: the docs pages' front page, content/docs/docs-index/en.mdx, rendered on the server for each request
 * so a search engine reads real HTML. The same loader shape as app/docs/[...slug].tsx with no slug. The
 * reader (lib/docs) is loaded inside the server-only functions, so Node's file system never reaches the
 * browser bundle. Public: anyone reads it signed out.
 */

export async function generateMetadata(): Promise<Metadata | null> {
  const { docsMetadata } = await import("@/lib/docs/page");
  const page = docsMetadata([]);
  if (!page) return null;
  return { title: page.title, description: page.description, openGraph: { title: page.title, description: page.description, type: "website" } };
}

/** Runs on the server and is stripped from the client; the page reads it with useLoaderData. */
export async function loader() {
  const { resolveDocsPage } = await import("@/lib/docs/page");
  const resolved = await resolveDocsPage([]);
  if (resolved.type === "redirect") return { page: null, redirect: resolved.href };
  if (resolved.type === "notFound") return { page: null, redirect: docsLinks.home };
  return { page: resolved.data, redirect: null };
}

export default function DocsHomeRoute() {
  const { page, redirect } = useLoaderData<typeof loader>();
  if (redirect || !page) return <Redirect href={(redirect ?? docsLinks.home) as Href} />;
  return (
    <>
      <Head>
        <title>{page.entry.metaTitle}</title>
      </Head>
      <DocsPage links={docsLinks} page={page} />
    </>
  );
}
