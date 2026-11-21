import { useGlobalSearchParams, usePathname, useRouter } from "expo-router";
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useAction } from "@/hooks/use-action";
import { useOnlineRead } from "@/hooks/use-online-read";
import { signOut } from "@/lib/auth";
import { rememberTeam } from "@/lib/last-team";
import type { Online } from "@/lib/online";
import { useSession } from "@/lib/session";
import { newId } from "@/lib/utils";
import { parseOpen, withTask } from "@/lib/workspace/layout";
import { workstream, type Task, type Team } from "@/lib/workstream/api";
import { AppShell, type ShellPlace, type ShellTask } from "@/src/shell/app-shell";
import { CreateTeamDialog, NewTaskDialog } from "@/src/workspace/dialogs";

/** How often the sidebar's tasks are read again, in milliseconds. */
const TASKS_EVERY = 4000;
/** And those shared with the person, which change less often. */
const SHARED_EVERY = 15000;

type TeamShell = {
  teamId: string;
  team: Team | null;
  tasks: Online<Task[]>;
  /** The tasks on screen, from the address. */
  open: string[];
  /** Put these tasks on screen. */
  show: (taskIds: string[]) => void;
  refreshTasks: () => void;
  newTask: () => void;
};

const Context = createContext<TeamShell | null>(null);

/** What the team's frame knows, for the page inside it. */
export function useTeamShell(): TeamShell {
  const shell = useContext(Context);
  if (!shell) throw new Error("This page is drawn outside a team.");
  return shell;
}

const PLACES: Record<ShellPlace, string> = { tasks: "", environments: "/environments", team: "/team", usage: "/usage" };

/**
 * The controller for the frame around a team's pages (app/(app)/t/[team]/_layout.tsx): it reads the person's
 * teams and this team's tasks, keeps which tasks are on screen in the address (`?open=a,b`), and owns the two
 * things that can be started from anywhere, a task and a team.
 */
export function TeamShellController({ teamId, children }: { teamId: string; children: ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const params = useGlobalSearchParams<{ open?: string }>();
  const session = useSession();
  const teams = useOnlineRead(() => workstream.teams(), "teams");
  const tasks = useOnlineRead(async () => (await workstream.tasks(teamId)).data, `tasks:${teamId}`, TASKS_EVERY);
  const shared = useOnlineRead(async () => (await workstream.sharedTasks()).data, "shared-tasks", SHARED_EVERY);
  const [dialog, setDialog] = useState<"task" | "team" | null>(null);
  const environments = useOnlineRead(async () => (dialog === "task" ? (await workstream.environments(teamId)).data : []), `environments:${teamId}:${dialog === "task"}`);
  const starting = useAction();
  const creating = useAction();

  useEffect(() => rememberTeam(teamId), [teamId]);

  // Being here, and opening the dialog for a new task, are when a task is likely: the API gets a desktop
  // running ahead of it. Whether that worked is nothing the person needs to hear about.
  useEffect(() => {
    void workstream.wantSpare(teamId).catch(() => {});
  }, [teamId, dialog === "task"]);

  const base = `/t/${encodeURIComponent(teamId)}`;
  const place: ShellPlace = pathname.endsWith("/environments") ? "environments" : pathname.endsWith("/team") ? "team" : pathname.endsWith("/usage") ? "usage" : "tasks";
  const open = useMemo(() => parseOpen(params.open), [params.open]);
  const show = useCallback(
    (taskIds: string[]) => {
      const query = taskIds.length > 0 ? `?open=${taskIds.map(encodeURIComponent).join(",")}` : "";
      if (place === "tasks") router.setParams({ open: taskIds.join(",") });
      else router.navigate(`${base}${query}`);
    },
    [router, base, place],
  );

  const all = teams.kind === "ready" ? teams.value.data : [];
  const team = all.find((candidate) => candidate.id === teamId) ?? null;
  const toShell = (task: Task): ShellTask => ({
    id: task.id, title: task.title, state: task.state, working: task.agentWorking,
    by: task.createdBy === session?.userId ? null : task.createdByName,
    open: place === "tasks" && open.includes(task.id),
  });
  const shellTasks: Online<ShellTask[]> = tasks.kind === "ready" ? { kind: "ready", value: tasks.value.map(toShell) } : tasks;
  // Other teams' tasks only: the API leaves out every team the person is on.
  const sharedTasks = shared.kind === "ready" ? shared.value.map(toShell) : [];

  const shell: TeamShell = { teamId, team, tasks, open, show, refreshTasks: tasks.refresh, newTask: () => setDialog("task") };

  return (
    <Context.Provider value={shell}>
      <AppShell
        team={team}
        teams={all}
        person={{ name: session?.name ?? "", email: session?.email ?? null, systemAdmin: session?.systemAdmin ?? false }}
        place={place}
        tasks={shellTasks}
        shared={sharedTasks}
        onRetryTasks={tasks.reload}
        onOpenTask={(taskId) => show([taskId])}
        onOpenBeside={(taskId) => show(withTask(place === "tasks" ? open : [], taskId, null))}
        onNewTask={() => {
          starting.clearError();
          setDialog("task");
        }}
        onSwitchTeam={(next) => router.navigate(`/t/${encodeURIComponent(next)}`)}
        onCreateTeam={() => {
          creating.clearError();
          setDialog("team");
        }}
        onGo={(next) => router.navigate(`${base}${PLACES[next]}`)}
        onAdmin={() => router.navigate("/admin")}
        onSignOut={() => void signOut().then(() => router.replace("/"))}
      >
        {children}
      </AppShell>
      {dialog === "task" && environments.kind === "ready" ? (
        <NewTaskDialog
          environments={environments.value}
          busy={starting.busy}
          error={starting.error}
          onClose={() => setDialog(null)}
          onStart={(prompt, environmentId) =>
            void starting.run(async () => {
              const task = await workstream.startTask(teamId, { id: newId(), environmentId, prompt, promptKey: newId() });
              setDialog(null);
              tasks.refresh();
              show(withTask(place === "tasks" ? open : [], task.id, null));
            })
          }
        />
      ) : null}
      {dialog === "team" ? (
        <CreateTeamDialog
          busy={creating.busy}
          error={creating.error}
          onClose={() => setDialog(null)}
          onCreate={(name) =>
            void creating.run(async () => {
              const created = await workstream.createTeam(name);
              setDialog(null);
              teams.reload();
              router.navigate(`/t/${encodeURIComponent(created.id)}`);
            })
          }
        />
      ) : null}
    </Context.Provider>
  );
}
