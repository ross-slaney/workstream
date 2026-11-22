import { useHeadlessAuth } from "@sqlos/headless/react-native";
import { useEffect } from "react";
import { forgetSignIn, leaveForProvider } from "@/lib/auth";
import { getAuthServerUrl, getRedirectUri, getSqlOsClientId } from "@/lib/config";
import type { DeviceScreenMode, DeviceScreenProps } from "@/src/auth/device-screen";
import { useSignInSteps } from "./use-sign-in";

/*
 * Approving the command line's sign-in: the engine behind the device page. `workstream login` asks SqlOS for a
 * device code and prints a link; the link opens SqlOS, which hands the browser here with the request it made
 * for the code (api/Workstream.cs SignInUiUrl). The page reads that request. Signed out, the person signs in
 * with the same form and steps as the sign-in page, inside the request; SqlOS then knows who is approving. The
 * page shows the code the terminal printed and what the command line may do, and the person allows it or not.
 * The command line's next poll gets a session of the person's own (docs/core-model.md, "The command line").
 *
 * Signing in with GitHub here is the same button as on the sign-in page, inside the command line's request:
 * SqlOS takes the browser back from GitHub signed in and hands it to this page again, at the approval. If
 * GitHub did not sign the person in, SqlOS hands the browser to the sign-in page, which sends it back here
 * (app/sign-in.tsx) with `failed`, and the form says so.
 *
 * Approving needs SqlOS's own session in this browser, a cookie on the API's address that the sign-in steps
 * set, so this flow, unlike the app's sign-in, sends credentials. Nothing here touches the app's session: the
 * command line's tokens go to the command line, never to this page.
 */

export function useDeviceSignIn(requestId: string | undefined, providerFailed?: boolean): DeviceScreenProps {
  const auth = useHeadlessAuth({
    issuer: getAuthServerUrl(),
    clientId: getSqlOsClientId(),
    redirectUri: getRedirectUri(),
    credentials: "include",
  });
  const { flow, status, view, viewModel, error } = auth;
  // Whatever was kept for a trip to GitHub from this page has done its work once the page is on screen again.
  useEffect(() => forgetSignIn(), []);
  // Always the device step: SqlOS answers it with the approval itself when this browser is already signed in.
  const read = () => (requestId ? flow.resume(`?request=${encodeURIComponent(requestId)}&view=device`) : Promise.resolve());

  const signIn = useSignInSteps(auth, "login", {
    providerFailed,
    begin: read,
    // This page has no code of its own to exchange: it only needs finding again if GitHub does not sign the person in.
    leave: (url) => leaveForProvider(url, { state: null, codeVerifier: null, returnTo: `/device?request=${encodeURIComponent(requestId ?? "")}` }),
    // Signed in: SqlOS points at its approval step. The page reads the request again and shows it.
    finish: async () => {
      // Still at the sign-in step means SqlOS kept no session in this browser: say sign-in did not work.
      if ((await read()) === "error" || flow.viewModel?.view !== "device-approve") throw new Error("The request is not ready to approve.");
    },
  });

  const mode: DeviceScreenMode =
    !requestId || (status === "error" && !viewModel) ? "unusable"
    : view === "device-approve" ? "approve"
    : view === "device-approved" ? "approved"
    : view === "device-denied" ? "denied"
    : !viewModel ? "opening"
    : "signIn";
  const busy = status === "idle" || status === "loading";
  // A person on several teams approves for the first, as sign-in chooses: the command line names its team in every request.
  const organizationId = viewModel?.organizationSelection?.[0]?.id;

  return {
    mode,
    signIn,
    email: viewModel?.email ?? null,
    code: viewModel?.deviceAuthorization?.userCode ?? null,
    busy,
    error: mode === "approve" ? error : null,
    onApprove: () => {
      if (!busy) void flow.device.approve(organizationId ? { organizationId } : undefined);
    },
    onDeny: () => {
      if (!busy) void flow.device.deny();
    },
    homeHref: "/",
  };
}
