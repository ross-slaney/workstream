import { pick } from "@/lib/surface";
import { AppShell as WebDesktop } from "./web-desktop";
import { AppShell as WebPhone } from "./web-phone";

export type { AppShellProps, ShellPlace, ShellTask } from "./props";

/** The web: the sidebar beside the page from 768px; behind a button in a narrow window. */
export const AppShell = pick({ phone: WebPhone, desktop: WebDesktop });
