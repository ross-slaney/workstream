import { BarChart3, Boxes, LogOut, Plus, ShieldCheck, SquareSplitHorizontal, Users } from "lucide-react-native";
import type { ReactNode } from "react";
import { ReadState } from "@/components/kit/status";
import { useTheme } from "@/components/theme/theme";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { TASK_DRAG_TYPE } from "@/lib/workspace/drag";
import { Mark } from "@/src/landing/landing-page";
import type { AppShellProps, ShellPlace, ShellTask } from "./props";

const CREATE_TEAM = "__create__";

/** Everything down the side: the team, the tasks, those shared with the person, the team's other pages and the person. The same on every surface. */
export function Sidebar(p: AppShellProps) {
  const t = useT("workspace");
  const common = useT("common");
  const { colors } = useTheme();
  return (
    <nav aria-label={common("brand.name")} className="flex h-full w-full flex-col bg-canvas">
      <div className="flex items-center gap-2 px-3 pb-2 pt-3">
        <Mark size={20} />
        <label className="sr-only" htmlFor="team-switch">
          {t("sidebar.teams")}
        </label>
        <select
          id="team-switch"
          value={p.team?.id ?? ""}
          onChange={(event) => (event.target.value === CREATE_TEAM ? p.onCreateTeam() : p.onSwitchTeam(event.target.value))}
          className="h-8 min-w-0 flex-1 cursor-pointer truncate rounded-md border border-transparent bg-transparent px-1.5 text-[13.5px] font-semibold text-foreground outline-none hover:bg-muted focus-visible:border-ring"
        >
          {p.teams.map((team) => (
            <option key={team.id} value={team.id}>
              {team.name}
            </option>
          ))}
          <option value={CREATE_TEAM}>{t("sidebar.createTeam")}</option>
        </select>
      </div>

      <div className="px-3 pb-2">
        <button
          type="button"
          onClick={p.onNewTask}
          className="flex h-9 w-full items-center justify-center gap-2 rounded-md bg-primary text-[13.5px] font-semibold text-primary-foreground outline-none hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1 focus-visible:ring-offset-canvas"
        >
          <Plus size={16} color={colors.primaryForeground} />
          {t("sidebar.newTask")}
        </button>
      </div>

      <div className="flex min-h-0 flex-1 flex-col">
        <h2 className="px-4 pb-1 pt-2 text-[11.5px] font-semibold text-muted-foreground">{t("sidebar.tasks")}</h2>
        <div className="min-h-0 flex-1 overflow-y-auto px-2 pb-2">
          {p.tasks.kind === "ready" ? (
            p.tasks.value.length === 0 ? (
              <p className="px-2 py-2 text-[13px] leading-relaxed text-muted-foreground">{t("sidebar.noTasks")}</p>
            ) : (
              <ul className="flex flex-col gap-0.5">
                {p.tasks.value.map((task) => (
                  <TaskRow key={task.id} task={task} onOpen={() => p.onOpenTask(task.id)} onOpenBeside={() => p.onOpenBeside(task.id)} />
                ))}
              </ul>
            )
          ) : (
            <div className="px-2">
              <ReadState read={p.tasks} onRetry={p.onRetryTasks} />
            </div>
          )}
          {p.shared.length > 0 ? (
            <>
              <h2 className="px-2 pb-1 pt-4 text-[11.5px] font-semibold text-muted-foreground">{t("sidebar.shared")}</h2>
              <ul aria-label={t("sidebar.shared")} className="flex flex-col gap-0.5">
                {p.shared.map((task) => (
                  <TaskRow key={task.id} task={task} onOpen={() => p.onOpenTask(task.id)} onOpenBeside={() => p.onOpenBeside(task.id)} />
                ))}
              </ul>
            </>
          ) : null}
        </div>
      </div>

      <div className="flex flex-col gap-0.5 border-t border-border px-2 py-2">
        <PlaceLink place="environments" current={p.place} onGo={p.onGo} label={t("sidebar.environments")} icon={<Boxes size={16} color={colors.mutedForeground} />} />
        <PlaceLink place="team" current={p.place} onGo={p.onGo} label={t("sidebar.team")} icon={<Users size={16} color={colors.mutedForeground} />} />
        <PlaceLink place="usage" current={p.place} onGo={p.onGo} label={t("sidebar.usage")} icon={<BarChart3 size={16} color={colors.mutedForeground} />} />
        {p.person.systemAdmin ? <RowButton onClick={p.onAdmin} label={t("sidebar.admin")} icon={<ShieldCheck size={16} color={colors.mutedForeground} />} /> : null}
      </div>

      <div className="flex items-center gap-2 border-t border-border px-3 py-2.5">
        <div className="flex min-w-0 flex-1 flex-col">
          <span className="truncate text-[13px] font-medium text-foreground">{p.person.name}</span>
          {p.person.email ? <span className="truncate text-[12px] text-muted-foreground">{p.person.email}</span> : null}
        </div>
        <button
          type="button"
          onClick={p.onSignOut}
          aria-label={common("actions.signOut")}
          title={common("actions.signOut")}
          className="inline-flex size-8 shrink-0 items-center justify-center rounded-md outline-none hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring"
        >
          <LogOut size={16} color={colors.mutedForeground} />
        </button>
      </div>
    </nav>
  );
}

function TaskRow({ task, onOpen, onOpenBeside }: { task: ShellTask; onOpen: () => void; onOpenBeside: () => void }) {
  const t = useT("workspace");
  const { colors } = useTheme();
  const dot =
    task.state === "ready" ? "bg-positive"
    : task.state === "starting" || task.state === "stopping" ? "bg-warning"
    : task.state === "failed" ? "bg-destructive"
    : "bg-input";
  const state = task.state === "ready" && task.working ? t("state.working") : t(`state.${task.state}`);
  return (
    <li
      draggable
      onDragStart={(event) => {
        event.dataTransfer.setData(TASK_DRAG_TYPE, task.id);
        event.dataTransfer.setData("text/plain", task.title);
        event.dataTransfer.effectAllowed = "copy";
      }}
      className={cn("group flex items-center rounded-md", task.open ? "bg-primary-soft" : "hover:bg-muted")}
    >
      <button type="button" onClick={onOpen} aria-current={task.open ? "true" : undefined} className="flex min-w-0 flex-1 items-center gap-2.5 rounded-md px-2 py-1.5 text-left outline-none focus-visible:ring-2 focus-visible:ring-ring">
        <span aria-hidden="true" className={cn("size-2 shrink-0 rounded-full", dot, task.state === "ready" && task.working && "animate-pulse motion-reduce:animate-none")} />
        <span className="flex min-w-0 flex-1 flex-col">
          <span className={cn("truncate text-[13.5px]", task.open ? "font-semibold text-primary" : "text-foreground")}>{task.title}</span>
          <span className="truncate text-[11.5px] text-muted-foreground">{task.by ? `${state} · ${t("sidebar.by", { name: task.by })}` : state}</span>
        </span>
      </button>
      {task.open ? null : (
        <button
          type="button"
          onClick={onOpenBeside}
          aria-label={t("sidebar.openBeside", { title: task.title })}
          title={t("sidebar.openBeside", { title: task.title })}
          className="mr-1 inline-flex size-7 shrink-0 items-center justify-center rounded-md opacity-0 outline-none hover:bg-background focus-visible:opacity-100 focus-visible:ring-2 focus-visible:ring-ring group-hover:opacity-100"
        >
          <SquareSplitHorizontal size={15} color={colors.mutedForeground} />
        </button>
      )}
    </li>
  );
}

function PlaceLink({ place, current, onGo, label, icon }: { place: ShellPlace; current: ShellPlace; onGo: (place: ShellPlace) => void; label: string; icon: ReactNode }) {
  return <RowButton onClick={() => onGo(place)} label={label} icon={icon} current={place === current} />;
}

function RowButton({ onClick, label, icon, current }: { onClick: () => void; label: string; icon: ReactNode; current?: boolean }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={current ? "page" : undefined}
      className={cn("flex h-8 items-center gap-2.5 rounded-md px-2 text-left text-[13.5px] outline-none focus-visible:ring-2 focus-visible:ring-ring", current ? "bg-muted font-medium text-foreground" : "text-foreground hover:bg-muted")}
    >
      {icon}
      {label}
    </button>
  );
}
