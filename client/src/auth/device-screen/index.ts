import { pick } from "@/lib/surface";
import { DeviceScreen as WebDesktop } from "./web-desktop";
import { DeviceScreen as WebPhone } from "./web-phone";

export type { DeviceScreenMode, DeviceScreenProps } from "./props";

/** The web: the step on the page in a narrow window, in a card from 768px, as sign-in draws it. */
export const DeviceScreen = pick({ phone: WebPhone, desktop: WebDesktop });
