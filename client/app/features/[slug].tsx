import { useLoaderData } from "expo-router";
import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";
import { StatusError } from "expo-server";

import { translator, useT } from "@/lib/i18n";
import { FeaturePage } from "@/src/landing/feature-page";
import { isFeature, type FeatureSlug } from "@/src/landing/features";

/*
 * /features/<slug>: one feature's page, rendered on the server so a search engine reads real HTML. An
 * address that is not a feature is a real 404. Public, and the same signed in: a teammate reads it to
 * remember how a feature is used.
 */

type Params = Record<string, string | string[] | undefined>;

const slugOf = (params: Params) => (Array.isArray(params.slug) ? params.slug[0] : params.slug);

export async function generateMetadata(_request: unknown, params: Params): Promise<Metadata | null> {
  const slug = slugOf(params);
  if (!isFeature(slug)) return null;
  const t = translator("landing");
  const title = t(`features.list.${slug}.metaTitle`);
  const description = t(`features.list.${slug}.summary`);
  return { title, description, openGraph: { title: t(`features.list.${slug}.title`), description, type: "website" } };
}

/** Runs on the server and is stripped from the client; the page reads it with useLoaderData. */
export async function loader(_request: unknown, params: Params): Promise<{ slug: FeatureSlug }> {
  const slug = slugOf(params);
  if (!isFeature(slug)) throw new StatusError(404, translator("landing")("features.notFound"));
  return { slug };
}

export default function FeatureRoute() {
  const { slug } = useLoaderData<typeof loader>();
  const t = useT("landing");
  return (
    <>
      <Head>
        <title>{t(`features.list.${slug}.metaTitle`)}</title>
      </Head>
      <FeaturePage slug={slug} signInHref="/sign-in" startHref="/sign-in?view=signup" docsHref="/docs" />
    </>
  );
}
