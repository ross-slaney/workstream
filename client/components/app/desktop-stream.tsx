import { useEffect, useRef, useState } from "react";
import { Spinner } from "@/components/kit/status";
import { useTheme } from "@/components/theme/theme";
import { resolveAccessToken } from "@/lib/auth";
import { getApiUrl } from "@/lib/config";
import { useT } from "@/lib/i18n";

/*
 * A task's live desktop: the picture of its screen, and the mouse and keyboard when the person clicks into
 * it. The browser's remote-desktop client (noVNC) speaks to the desktop's display through the API's relay,
 * `GET /api/tasks/{id}/desktop` as a WebSocket. A browser cannot put a bearer token on a WebSocket, so the
 * token rides in the requested subprotocols beside `binary` (api/Desktops/DesktopRelay.cs), fresh each time
 * the stream connects. The picture is scaled to fit and keeps its shape, which it reports so the screen can
 * give it exactly the room it needs; a dropped stream reconnects.
 *
 * This is a browser component: it draws into a DOM element and loads its client only in the browser.
 */

type Status = "connecting" | "connected" | "lost";

type RemoteDesktop = {
  scaleViewport: boolean;
  resizeSession: boolean;
  focusOnClick: boolean;
  background: string;
  addEventListener: (event: string, listener: () => void) => void;
  disconnect: () => void;
};

export function DesktopStream({ taskId, label, onShape }: { taskId: string; label: string; onShape?: (widthOverHeight: number) => void }) {
  const t = useT("workspace");
  const { colors } = useTheme();
  const screen = useRef<HTMLDivElement>(null);
  const [status, setStatus] = useState<Status>("connecting");
  const stage = colors.stage;
  // Kept in a ref so a new callback from the controller does not reconnect the stream.
  const shape = useRef(onShape);
  shape.current = onShape;

  useEffect(() => {
    let remote: RemoteDesktop | null = null;
    let closed = false;
    let attempts = 0;
    let retry: ReturnType<typeof setTimeout> | undefined;

    const again = () => {
      if (closed) return;
      setStatus("lost");
      retry = setTimeout(connect, Math.min(1000 * 2 ** attempts++, 10_000));
    };

    async function connect() {
      try {
        const [{ default: RFB }, token] = await Promise.all([import("@novnc/novnc/lib/rfb"), resolveAccessToken()]);
        if (closed || !screen.current) return;
        const url = `${getApiUrl().replace(/^http/, "ws")}/api/tasks/${encodeURIComponent(taskId)}/desktop`;
        const client: RemoteDesktop = new RFB(screen.current, url, { wsProtocols: ["binary", `bearer.${token}`], shared: true });
        remote = client;
        client.scaleViewport = true;
        client.resizeSession = false;
        client.focusOnClick = true;
        client.background = stage;
        client.addEventListener("connect", () => {
          attempts = 0;
          setStatus("connected");
          // The picture is drawn at the desktop's own size: that is its shape, whatever it is scaled to.
          const picture = screen.current?.querySelector("canvas");
          if (picture && picture.width > 0 && picture.height > 0) shape.current?.(picture.width / picture.height);
        });
        client.addEventListener("disconnect", () => {
          if (remote === client) remote = null;
          again();
        });
      } catch {
        again();
      }
    }

    void connect();
    return () => {
      closed = true;
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
