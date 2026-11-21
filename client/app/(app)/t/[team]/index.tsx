import Head from "expo-router/head";
import { TaskScreenController } from "@/components/app/task-screen";
import { useTeamShell } from "@/components/app/team-shell";
import { useT } from "@/lib/i18n";
import { withTask } from "@/lib/workspace/layout";
import { WorkspaceScreen } from "@/src/workspace/workspace-screen";

/**
 * /t/{team}: the screens. Which tasks are open is the address's `?open=a,b`, so a reload, a shared link and
 * the back button all bring the same screens back. Each screen has a controller of its own.
 */
export default function WorkspaceRoute() {
  const t = useT("workspace");
  const shell = useTeamShell();
  return (
    <>
      <Head>
        <title>{t("meta.title")}</title>
      </Head>
      <WorkspaceScreen
        open={shell.open}
        onNewTask={shell.newTask}
        onDropTask={(taskId, onto) => shell.show(withTask(shell.open, taskId, onto))}
        renderScreen={(taskId) => (
          <TaskScreenController taskId={taskId} onChanged={shell.refreshTasks} onClose={() => shell.show(shell.open.filter((id) => id !== taskId))} />
        )}
      />
    </>
  );
}
