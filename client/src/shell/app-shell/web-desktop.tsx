import { Sidebar } from "./parts";
import type { AppShellProps } from "./props";

/** The desktop: the sidebar always there on the left, the page in the rest. */
export function AppShell(p: AppShellProps) {
  return (
    <div className="flex h-full bg-canvas text-foreground">
      <aside className="h-full w-[264px] shrink-0 border-r border-border">
        <Sidebar {...p} />
      </aside>
      <div className="flex min-h-0 min-w-0 flex-1 flex-col">{p.children}</div>
    </div>
  );
}
