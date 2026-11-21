import { pick } from "@/lib/surface";
import { WorkspaceScreen as WebDesktop } from "./web-desktop";
import { WorkspaceScreen as WebPhone } from "./web-phone";

export type { WorkspaceScreenProps } from "./props";

/** The web: up to four screens side by side from 768px; one at a time in a narrow window. */
export const WorkspaceScreen = pick({ phone: WebPhone, desktop: WebDesktop });
