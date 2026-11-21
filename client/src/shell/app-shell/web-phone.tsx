import { Menu, X } from "lucide-react-native";
import { useState } from "react";
import { IconButton } from "@/components/kit/button";
import { useTheme } from "@/components/theme/theme";
import { useT } from "@/lib/i18n";
import { Sidebar } from "./parts";
import type { AppShellProps } from "./props";

/** A phone's browser: the page fills the window; the sidebar slides over it from a button in the top bar and closes when something in it is chosen. */
export function AppShell(p: AppShellProps) {
  const t = useT("workspace");
  const common = useT("common");
  const { colors } = useTheme();
  const [open, setOpen] = useState(false);
  const closing = <T extends unknown[]>(action: (...args: T) => void) => (...args: T) => {
    setOpen(false);
    action(...args);
  };
  return (
    <div className="flex h-full flex-col bg-canvas text-foreground">
      <header className="flex h-12 shrink-0 items-center gap-2 border-b border-border bg-card px-2">
        <IconButton label={t("sidebar.tasks")} icon={<Menu size={18} color={colors.foreground} />} aria-expanded={open} onClick={() => setOpen(true)} />
        <span className="min-w-0 flex-1 truncate text-[14px] font-semibold">{p.team?.name ?? common("brand.name")}</span>
      </header>
      <div className="flex min-h-0 flex-1 flex-col">{p.children}</div>
      {open ? (
        <div className="fixed inset-0 z-30 flex">
          <div className="relative h-full w-[86%] max-w-[320px] border-r border-border shadow-xl">
            <Sidebar
              {...p}
              onOpenTask={closing(p.onOpenTask)}
              onOpenBeside={closing(p.onOpenTask)}
              onNewTask={closing(p.onNewTask)}
              onGo={closing(p.onGo)}
              onSwitchTeam={closing(p.onSwitchTeam)}
              onCreateTeam={closing(p.onCreateTeam)}
              onAdmin={closing(p.onAdmin)}
            />
            <div className="absolute right-1 top-2">
              <IconButton label={common("actions.close")} icon={<X size={18} color={colors.mutedForeground} />} onClick={() => setOpen(false)} />
            </div>
          </div>
          <button type="button" aria-label={common("actions.close")} className="h-full flex-1 bg-black/40" onClick={() => setOpen(false)} />
        </div>
      ) : null}
    </div>
  );
}
