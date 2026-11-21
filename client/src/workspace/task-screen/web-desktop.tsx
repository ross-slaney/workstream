import { useState } from "react";
import { Chat, DesktopPane, ScreenHeader } from "./parts";
import type { TaskScreenProps } from "./props";

/**
 * The desktop: the conversation in the left quarter and the task's desktop in the rest, under one bar. The
 * conversation keeps a readable width when the screen shares the space with others, and steps aside when the
 * person asks the desktop to fill the screen.
 */
export function TaskScreen(p: TaskScreenProps) {
  const [filled, setFilled] = useState(false);
  return (
    <article className="flex size-full min-h-0 min-w-0 flex-col overflow-hidden rounded-lg border border-border bg-card">
      <ScreenHeader {...p} filled={filled} onToggleFill={() => setFilled((value) => !value)} />
      <div className="flex min-h-0 flex-1">
        {filled ? null : (
          <div className="flex w-1/4 min-w-[250px] max-w-[440px] shrink-0 flex-col border-r border-border">
            <Chat {...p} />
          </div>
        )}
        <DesktopPane {...p} />
      </div>
    </article>
  );
}
