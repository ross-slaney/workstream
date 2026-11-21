import { Redirect, Slot, useGlobalSearchParams, usePathname } from "expo-router";
import { useSessionStatus } from "@/lib/session";

/*
 * Everything a person must be signed in for. The server knows no session, so it sends an empty frame; the
 * browser reads the session and either draws the page or goes to sign in, coming back here afterwards, with
 * the tasks that were to be on screen (a shared task's link is `/home?open=`).
 */
export default function SignedInLayout() {
  const status = useSessionStatus();
  const pathname = usePathname();
  const { open } = useGlobalSearchParams<{ open?: string }>();
  const back = typeof open === "string" && open ? `${pathname}?open=${encodeURIComponent(open)}` : pathname;
  if (status === "loading") return <div className="h-full bg-canvas" />;
  if (status === "anonymous") return <Redirect href={`/sign-in?returnTo=${encodeURIComponent(back)}`} />;
  return <Slot />;
}
