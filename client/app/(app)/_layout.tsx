import { Redirect, Slot, usePathname } from "expo-router";
import { useSessionStatus } from "@/lib/session";

/*
 * Everything a person must be signed in for. The server knows no session, so it sends an empty frame; the
 * browser reads the session and either draws the page or goes to sign in, coming back here afterwards.
 */
export default function SignedInLayout() {
  const status = useSessionStatus();
  const pathname = usePathname();
  if (status === "loading") return <div className="h-full bg-canvas" />;
  if (status === "anonymous") return <Redirect href={`/sign-in?returnTo=${encodeURIComponent(pathname)}`} />;
  return <Slot />;
}
