import { pick } from "@/lib/surface";
import { EnvironmentsScreen as WebDesktop } from "./web-desktop";
import { EnvironmentsScreen as WebPhone } from "./web-phone";

export type { EnvironmentDraft, EnvironmentsScreenProps, RepositoryFileOffer, RepositoryPicker, RepositoryReading, VariableDraft } from "./props";

export const EnvironmentsScreen = pick({ phone: WebPhone, desktop: WebDesktop });
