import { Plus } from "lucide-react-native";
import { useEffect, useRef, useState, type DragEvent } from "react";
import { Button } from "@/components/kit/button";
import { EmptyState } from "@/components/kit/status";
import { useTheme } from "@/components/theme/theme";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { TASK_DRAG_TYPE } from "@/lib/workspace/drag";
import { gridFor, MAX_SCREENS } from "@/lib/workspace/layout";
import type { WorkspaceScreenProps } from "./props";

const carriesTask = (event: DragEvent) => event.dataTransfer.types.includes(TASK_DRAG_TYPE);

/**
 * The desktop: the open screens share the whole space, split whichever way gives each desktop the widest
 * picture (lib/workspace/layout.ts). A task dragged from the sidebar can be dropped anywhere to open it
 * beside the others; when four are open, it takes the place of the screen it is dropped on.
 */
export function WorkspaceScreen(p: WorkspaceScreenProps) {
  const t = useT("workspace");
  const { colors } = useTheme();
  const space = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 1280, height: 800 });
  /** A task is being dragged over the screens: -1 over the space, or the screen it is over. */
  const [over, setOver] = useState<number | null>(null);

  useEffect(() => {
    const element = space.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => setSize({ width: entry.contentRect.width, height: entry.contentRect.height }));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const full = p.open.length >= MAX_SCREENS;
  const grid = gridFor(p.open.length, size.width, size.height);
  const drop = (event: DragEvent, onto: number | null) => {
    if (!carriesTask(event)) return;
    event.preventDefault();
    event.stopPropagation();
    setOver(null);
    const taskId = event.dataTransfer.getData(TASK_DRAG_TYPE);
    if (taskId) p.onDropTask(taskId, onto);
  };
  const hover = (event: DragEvent, at: number) => {
    if (!carriesTask(event)) return;
    event.preventDefault();
    event.stopPropagation();
    event.dataTransfer.dropEffect = "copy";
    setOver(at);
  };

  return (
    <main
      ref={space}
      aria-label={t("sidebar.tasks")}
      onDragOver={(event) => hover(event, -1)}
      onDragLeave={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setOver(null);
      }}
      onDrop={(event) => drop(event, null)}
      className="relative min-h-0 flex-1 p-2"
    >
      {p.open.length === 0 ? (
        <div className={cn("flex size-full items-center justify-center rounded-lg border border-dashed border-border", over !== null && "border-primary bg-primary-soft")}>
          <EmptyState
            title={over !== null ? t("screens.drop.add") : t("screens.empty.title")}
            body={over !== null ? undefined : t("screens.empty.body")}
            action={over !== null ? undefined : <Button label={t("screens.empty.cta")} tone="primary" icon={<Plus size={16} color={colors.primaryForeground} />} onClick={p.onNewTask} />}
          />
        </div>
      ) : (
        <div className="grid size-full gap-2" style={{ gridTemplateColumns: `repeat(${grid.columns}, minmax(0, 1fr))`, gridTemplateRows: `repeat(${grid.rows}, minmax(0, 1fr))` }}>
          {p.open.map((taskId, index) => (
            <div
              key={taskId}
              onDragOver={(event) => hover(event, index)}
              onDrop={(event) => drop(event, index)}
              // Three screens in a two-by-two split: the last takes the whole row instead of leaving a hole.
              style={p.open.length === 3 && grid.columns === 2 && index === 2 ? { gridColumn: "span 2" } : undefined}
              className="relative min-h-0 min-w-0"
            >
              {p.renderScreen(taskId)}
              {over !== null && full && over === index ? <DropHint label={t("screens.drop.replace")} /> : null}
            </div>
          ))}
        </div>
      )}
      {over !== null && p.open.length > 0 && !full ? <DropHint label={t("screens.drop.add")} whole /> : null}
      {over !== null && full && over === -1 ? <DropHint label={t("screens.drop.full")} whole quiet /> : null}
    </main>
  );
}

/** Drawn over the screens while a task is dragged across them. It never takes the pointer, so the drop lands on what is beneath. */
function DropHint({ label, whole, quiet }: { label: string; whole?: boolean; quiet?: boolean }) {
  return (
    <div className={cn("pointer-events-none absolute z-10 flex items-center justify-center rounded-lg border-2 border-dashed border-primary", whole ? "inset-2" : "inset-0", quiet ? "bg-background/60" : "bg-primary-soft/85")}>
      <span className="rounded-md bg-card px-3 py-1.5 text-[13px] font-semibold text-foreground shadow">{label}</span>
    </div>
  );
}
