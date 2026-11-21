import { pick } from "@/lib/surface";
import { GitHubScreen as WebDesktop } from "./web-desktop";
import { GitHubScreen as WebPhone } from "./web-phone";

export type { GitHubScreenProps } from "./props";

export const GitHubScreen = pick({ phone: WebPhone, desktop: WebDesktop });
