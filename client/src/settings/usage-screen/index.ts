import { pick } from "@/lib/surface";
import { UsageScreen as WebDesktop } from "./web-desktop";
import { UsageScreen as WebPhone } from "./web-phone";

export type { UsageScreenProps } from "./props";

export const UsageScreen = pick({ phone: WebPhone, desktop: WebDesktop });
