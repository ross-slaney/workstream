import { useState } from "react";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { Chat, DesktopPane, ScreenHeader } from "./parts";
import type { TaskScreenProps } from "./props";

/** A phone's browser: one of the two at a time, the conversation or the desktop, with a switch between them. */
export function TaskScreen(p: TaskScreenProps) {
  const t = useT("workspace");
  const [showing, setShowing] = useState<"chat" | "desktop">("chat");
  return (
    <article className="flex size-full min-h-0 flex-col overflow-hidden bg-card">
      <ScreenHeader {...p} filled={false} />
      <div role="tablist" className="flex shrink-0 border-b border-border">
        {(["chat", "desktop"] as const).map((tab) => (
          <button
            key={tab}
            type="button"
            role="tab"
            aria-selected={showing === tab}
            onClick={() => setShowing(tab)}
            className={cn("h-10 flex-1 text-[13px] font-medium outline-none focus-visible:bg-muted", showing === tab ? "border-b-2 border-primary text-foreground" : "text-muted-foreground")}
          >
            {t(`tabs.${tab}`)}
          </button>
        ))}
      </div>
      <div className="flex min-h-0 flex-1 flex-col">{showing === "chat" ? <Chat {...p} /> : <DesktopPane {...p} />}</div>
    </article>
  );
}
