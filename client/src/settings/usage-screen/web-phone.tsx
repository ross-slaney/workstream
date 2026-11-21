import { UsagePage } from "./parts";
import type { UsageScreenProps } from "./props";

/** A phone's browser: the two cards one under the other. */
export function UsageScreen(p: UsageScreenProps) {
  return <UsagePage {...p} />;
}
