import { pick } from "@/lib/surface";
import { TeamScreen as WebDesktop } from "./web-desktop";
import { TeamScreen as WebPhone } from "./web-phone";

export type { TeamScreenProps } from "./props";

export const TeamScreen = pick({ phone: WebPhone, desktop: WebDesktop });
