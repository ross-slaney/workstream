import { UsagePage } from "./parts";
import type { UsageScreenProps } from "./props";

/** The desktop: the two cards side by side. */
export function UsageScreen(p: UsageScreenProps) {
  return <UsagePage {...p} />;
}
