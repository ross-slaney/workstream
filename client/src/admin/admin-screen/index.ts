import { pick } from "@/lib/surface";
import { AdminScreen as WebDesktop } from "./web-desktop";
import { AdminScreen as WebPhone } from "./web-phone";

export type { AdminScreenProps } from "./props";

export const AdminScreen = pick({ phone: WebPhone, desktop: WebDesktop });
