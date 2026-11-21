import { Redirect } from "expo-router";
import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";
import { translator, useT } from "@/lib/i18n";
import { useSessionStatus } from "@/lib/session";
import { LandingPage } from "@/src/landing/landing-page";

/*
 * The front door: the website's one page, rendered on the server so a search engine reads real HTML.
 * Someone already signed in goes straight to their tasks.
 */

export function generateMetadata(): Metadata {
  const t = translator("landing");
  return {
    title: t("meta.title"),
    description: t("meta.description"),
    openGraph: { title: t("hero.title"), description: t("meta.description"), type: "website" },
  };
}

export default function FrontDoor() {
  const t = useT("landing");
  const status = useSessionStatus();
  if (status === "authenticated") return <Redirect href="/home" />;
  return (
    <>
      <Head>
        <title>{t("meta.title")}</title>
      </Head>
      <LandingPage signInHref="/sign-in" startHref="/sign-in?view=signup" />
    </>
  );
}
