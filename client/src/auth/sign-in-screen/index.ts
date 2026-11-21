import { pick } from "@/lib/surface";
import { SignInScreen as WebDesktop } from "./web-desktop";
import { SignInScreen as WebPhone } from "./web-phone";

export type { SignInScreenProps } from "./props";

/** The web: the form on the page in a narrow window, in a card from 768px. */
export const SignInScreen = pick({ phone: WebPhone, desktop: WebDesktop });
