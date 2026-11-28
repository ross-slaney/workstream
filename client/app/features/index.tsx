import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";
import { translator, useT } from "@/lib/i18n";
import { FeaturesPage } from "@/src/landing/feature-page";

/* /features: every feature on one page, rendered on the server. Public, and the same signed in. */

export function generateMetadata(): Metadata {
  const t = translator("landing");
  const title = `${t("features.title")} · ${translator("common")("brand.name")}`;
  return { title, description: t("meta.description"), openGraph: { title, description: t("meta.description"), type: "website" } };
}

export default function FeaturesRoute() {
  const t = useT("landing");
  const brand = useT("common")("brand.name");
  return (
    <>
      <Head>
        <title>{`${t("features.title")} · ${brand}`}</title>
      </Head>
      <FeaturesPage signInHref="/sign-in" startHref="/sign-in?view=signup" docsHref="/docs" />
    </>
  );
}
