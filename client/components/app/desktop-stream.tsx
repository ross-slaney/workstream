import { useEffect, useRef, useState } from "react";
import { Spinner } from "@/components/kit/status";
import { useTheme } from "@/components/theme/theme";
import { resolveAccessToken } from "@/lib/auth";
import { getApiUrl } from "@/lib/config";
import { useT } from "@/lib/i18n";

/*
 * A task's live desktop: the picture of its screen, and the mouse and keyboard when the person clicks into
 * it, for whoever may use them. The browser's remote-desktop client (noVNC) speaks to the desktop's display through the API's relay,
 * `GET /api/tasks/{id}/desktop` as a WebSocket. A browser cannot put a bearer token on a WebSocket, so the
 * token rides in the requested subprotocols beside `binary` (api/Desktops/DesktopRelay.cs), fresh each time
 * the stream connects. The picture is scaled to fit and keeps its shape, which it reports so the screen can
 * give it exactly the room it needs; a dropped stream reconnects. Someone a task is shared with to watch gets
 * the picture with the mouse and keyboard off (`viewOnly`); the relay drops what they would send anyway.
 *
 * While the browser tab is hidden nobody is looking, so the stream is closed, and it connects again when the
 * tab is shown. An open stream does not keep a desktop running either way: only a person's mouse or keyboard,
 * a message, or the agent at work does (api/Desktops/DesktopRelay.cs).
 *
 * This is a browser component: it draws into a DOM element and loads its client only in the browser.
 */

type Status = "connecting" | "connected" | "lost";

type RemoteDesktop = {
  viewOnly: boolean;
  scaleViewport: boolean;
  resizeSession: boolean;
  focusOnClick: boolean;
  background: string;
  addEventListener: (event: string, listener: () => void) => void;
  disconnect: () => void;
};

export function DesktopStream({ taskId, label, viewOnly = false, onShape }: { taskId: string; label: string; viewOnly?: boolean; onShape?: (widthOverHeight: number) => void }) {
  const t = useT("workspace");
  const { colors } = useTheme();
  const screen = useRef<HTMLDivElement>(null);
  const [status, setStatus] = useState<Status>("connecting");
  const stage = colors.stage;
  // Kept in a ref so a new callback from the controller does not reconnect the stream.
  const shape = useRef(onShape);
  shape.current = onShape;
  // Kept in a ref too: a share that changes while the stream is open changes the client, not the connection.
  const watching = useRef(viewOnly);
  watching.current = viewOnly;
  const live = useRef<RemoteDesktop | null>(null);
  useEffect(() => {
    if (live.current) {
      live.current.viewOnly = viewOnly;
      live.current.focusOnClick = !viewOnly;
    }
  }, [viewOnly]);

  useEffect(() => {
    let remote: RemoteDesktop | null = null;
    let closed = false;
    let attempts = 0;
    let connected = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    let hidden = document.visibilityState === "hidden";
    // Each try to connect has a number, so one that was overtaken while it waited makes no second client.
    let attempt = 0;

    const again = () => {
      if (closed || hidden) return;
      // A stream that never opened is still connecting; only one that was open has been lost.
      setStatus(connected ? "lost" : "connecting");
      retry = setTimeout(connect, Math.min(500 * 2 ** attempts++, 10_000));
    };

    async function connect() {
      const mine = ++attempt;
      try {
        const [{ default: RFB }, token] = await Promise.all([import("@novnc/novnc/lib/rfb"), resolveAccessToken()]);
        if (closed || hidden || mine !== attempt || !screen.current) return;
        const url = `${getApiUrl().replace(/^http/, "ws")}/api/tasks/${encodeURIComponent(taskId)}/desktop`;
        const client: RemoteDesktop = new RFB(screen.current, url, { wsProtocols: ["binary", `bearer.${token}`], shared: true });
        remote = client;
        live.current = client;
        client.viewOnly = watching.current;
        client.scaleViewport = true;
        client.resizeSession = false;
        client.focusOnClick = !watching.current;
        client.background = stage;
        client.addEventListener("connect", () => {
          attempts = 0;
          connected = true;
          setStatus("connected");
          // The picture is drawn at the desktop's own size: that is its shape, whatever it is scaled to.
          const picture = screen.current?.querySelector("canvas");
          if (picture && picture.width > 0 && picture.height > 0) shape.current?.(picture.width / picture.height);
        });
        client.addEventListener("disconnect", () => {
          if (remote === client) remote = null;
          if (live.current === client) live.current = null;
          again();
        });
      } catch {
        again();
      }
    }

    const onVisibility = () => {
      if (closed) return;
      hidden = document.visibilityState === "hidden";
      clearTimeout(retry);
      if (hidden) {
        // Nobody is looking: close the stream. Its "disconnect" finds the tab hidden and does not try again.
        attempt++;
        remote?.disconnect();
        return;
      }
      // Shown again: a new stream, drawn as connecting rather than lost. One still closing connects when it has closed.
      attempts = 0;
      connected = false;
      setStatus("connecting");
      if (!remote) void connect();
    };
    document.addEventListener("visibilitychange", onVisibility);

    if (!hidden) void connect();
    return () => {
      closed = true;
      document.removeEventListener("visibilitychange", onVisibility);
      clearTimeout(retry);
      remote?.disconnect();
    };
  }, [taskId, stage]);

  return (
    <div className="absolute inset-0 overflow-hidden bg-stage">
      <div ref={screen} role="application" aria-label={label} className="size-full" />
      {status === "connected" ? null : (
        <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center gap-3 bg-stage/80 text-[13px] text-stage-foreground">
          <Spinner label={t("desktop.connecting")} className="size-5" />
          <span>{status === "lost" ? t("desktop.lost") : t("desktop.connecting")}</span>
        </div>
      )}
    </div>
  );
}
