import { pick } from "@/lib/surface";
import { TaskScreen as WebDesktop } from "./web-desktop";
import { TaskScreen as WebPhone } from "./web-phone";

export type { ChatEntry, TaskScreenProps } from "./props";

/** The web: the conversation beside the desktop from 768px; one at a time in a narrow window. */
export const TaskScreen = pick({ phone: WebPhone, desktop: WebDesktop });
