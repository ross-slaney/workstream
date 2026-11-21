import { EnvironmentsPage } from "./parts";
import type { EnvironmentsScreenProps } from "./props";

/** The desktop: the list in one readable column under the sidebar, and the editor in a dialog over it. */
export function EnvironmentsScreen(p: EnvironmentsScreenProps) {
  return <EnvironmentsPage {...p} />;
}
