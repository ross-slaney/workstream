import { useState } from "react";
import { Chat, DesktopPane, ScreenHeader } from "./parts";
import type { TaskScreenProps } from "./props";

/** The conversation's narrowest readable width, in pixels. */
const CHAT_MIN = 250;

/**
 * The desktop: the conversation on the left and the task's desktop on the right, under one bar. The desktop
 * takes three quarters of the width when it can use them. When the screen is short for its width, as two
 * screens one above the other are, the desktop takes only the width its picture fills at that height and the
 * conversation takes the rest, so nothing is spent on empty space beside the picture. The conversation steps
 * aside when the person asks the desktop to fill the screen.
 */
export function TaskScreen(p: TaskScreenProps) {
  const [filled, setFilled] = useState(false);
  return (
    <article className="flex size-full min-h-0 min-w-0 flex-col overflow-hidden rounded-lg border border-border bg-card">
      <ScreenHeader {...p} filled={filled} onToggleFill={() => setFilled((value) => !value)} />
      {/* Sized by the page, never by what is in it, so the widths below can be worked out from its own size. */}
      <div className="flex min-h-0 flex-1 [container-type:size]">
        {filled ? null : (
          <div className="flex min-w-0 flex-1 flex-col border-r border-border" style={{ minWidth: CHAT_MIN }}>
            <Chat {...p} />
          </div>
        )}
        <div
          className="flex min-w-0 shrink-0"
          style={{ width: filled ? "100%" : `min(75cqw, calc(100cqw - ${CHAT_MIN}px), calc(100cqh * ${p.desktopShape}))` }}
        >
          <DesktopPane {...p} />
        </div>
      </div>
    </article>
  );
}
