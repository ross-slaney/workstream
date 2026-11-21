import "@/global.css";
import "@/lib/i18n";

import { Slot } from "expo-router";
import Head from "expo-router/head";
import { useEffect } from "react";
import { ThemeProvider } from "@/components/theme/theme";
import { translator } from "@/lib/i18n";
import { hydrateSession } from "@/lib/session";

/*
 * The whole tree renders on the server for every request, where nobody is signed in. No route is guarded
 * here: the public pages render in full, and each signed-in area decides for itself once the browser has
 * read the session (app/(app)/_layout.tsx, app/admin.tsx).
 */
export default function RootLayout() {
  useEffect(() => {
    hydrateSession();
  }, []);
  return (
    <ThemeProvider>
      <Head>
        <title>{translator("common")("brand.name")}</title>
      </Head>
      <Slot />
    </ThemeProvider>
  );
}
