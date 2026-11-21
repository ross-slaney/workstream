import { EnvironmentsPage } from "./parts";
import type { EnvironmentsScreenProps } from "./props";

/** A phone's browser: the same list, full width; the editor's fields stack in the dialog. */
export function EnvironmentsScreen(p: EnvironmentsScreenProps) {
  return <EnvironmentsPage {...p} />;
}
