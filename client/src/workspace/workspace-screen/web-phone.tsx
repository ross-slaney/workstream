import { Plus } from "lucide-react-native";
import { Button } from "@/components/kit/button";
import { EmptyState } from "@/components/kit/status";
import { useTheme } from "@/components/theme/theme";
import { useT } from "@/lib/i18n";
import type { WorkspaceScreenProps } from "./props";

/** A phone's browser: one task at a time, the first one open. Choosing another in the task list replaces it. */
export function WorkspaceScreen(p: WorkspaceScreenProps) {
  const t = useT("workspace");
  const { colors } = useTheme();
  const taskId = p.open[0];
  return (
    <main aria-label={t("sidebar.tasks")} className="flex min-h-0 flex-1 flex-col">
      {taskId ? (
        p.renderScreen(taskId)
      ) : (
        <div className="flex flex-1 items-center justify-center">
          <EmptyState
            title={t("screens.empty.title")}
            body={t("sidebar.noTasks")}
            action={<Button label={t("screens.empty.cta")} tone="primary" icon={<Plus size={16} color={colors.primaryForeground} />} onClick={p.onNewTask} />}
          />
        </div>
      )}
    </main>
  );
}
