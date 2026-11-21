import { useLocalSearchParams } from "expo-router";
import Head from "expo-router/head";
import type { Metadata } from "expo-router/server";
import { useDeviceSignIn } from "@/hooks/use-device-sign-in";
import { translator, useT } from "@/lib/i18n";
import { useSessionStatus } from "@/lib/session";
import { DeviceScreen } from "@/src/auth/device-screen";

/*
 * /device?request=…: where the link `workstream login` prints lands. SqlOS turns the link's code into a request
 * and hands the browser here (api/Workstream.cs SignInUiUrl); the person signs in if they need to and allows the
 * command line or not (hooks/use-device-sign-in.ts). Like sign-in, the server sends an empty frame and the
 * browser runs the flow.
 */

export function generateMetadata(): Metadata {
  return { title: translator("auth")("meta.deviceTitle"), robots: { index: false, follow: false } };
}

export default function DeviceRoute() {
  const t = useT("auth");
  const params = useLocalSearchParams<{ request?: string }>();
  const request = (Array.isArray(params.request) ? params.request[0] : params.request) || undefined;
  const status = useSessionStatus();
  return (
    <>
      <Head>
        <title>{t("meta.deviceTitle")}</title>
      </Head>
      {status === "loading" ? <div className="h-full bg-canvas" /> : <Approve request={request} />}
    </>
  );
}

function Approve({ request }: { request: string | undefined }) {
  return <DeviceScreen {...useDeviceSignIn(request)} />;
}
