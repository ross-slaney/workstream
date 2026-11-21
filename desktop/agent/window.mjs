// The agent's own window on the desktop: the same conversation the dashboard shows beside the desktop, for
// someone working on the desktop itself (full screen, or signed in to nothing else). A page served on
// localhost by this process and opened from the desktop's panel as an app window. It reads the conversation
// this process already holds, so it works whatever the network is doing.

import { readFile } from "node:fs/promises";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const WINDOW_PORT = 4141;
const page = path.join(path.dirname(fileURLToPath(import.meta.url)), "window.html");

/**
 * @param {object} with
 * @param {{ id: number, author: string, kind: string, text: string, at: string }[]} with.conversation
 * @param {() => { title: string, working: boolean }} with.status
 * @param {(text: string) => void} with.onSay
 * @param {() => void} with.onInterrupt
 */
export function startWindow({ conversation, status, onSay, onInterrupt, log }) {
  const json = (response, code, body) => {
    response.writeHead(code, { "content-type": "application/json", "cache-control": "no-store" });
    response.end(JSON.stringify(body));
  };
  const server = http.createServer(async (request, response) => {
    const url = new URL(request.url, "http://localhost");
    try {
      if (request.method === "GET" && url.pathname === "/") {
        response.writeHead(200, { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" });
        response.end(await readFile(page));
      } else if (request.method === "GET" && url.pathname === "/conversation") {
        const after = Number(url.searchParams.get("after") ?? 0);
        json(response, 200, { ...status(), messages: conversation.filter((entry) => entry.id > after) });
      } else if (request.method === "POST" && (url.pathname === "/say" || url.pathname === "/interrupt")) {
        // Only the page this server served may post: another site open in the desktop's browser may not.
        if (request.headers.origin && new URL(request.headers.origin).port !== String(WINDOW_PORT)) return json(response, 403, {});
        let body = "";
        for await (const chunk of request) body += chunk;
        if (url.pathname === "/interrupt") onInterrupt();
        else {
          const text = String(JSON.parse(body || "{}").text ?? "").trim();
          if (!text || text.length > 20_000) return json(response, 400, {});
          onSay(text);
        }
        json(response, 200, {});
      } else {
        json(response, 404, {});
      }
    } catch (error) {
      log("The agent's window failed a request:", error.message);
      json(response, 500, {});
    }
  });
  server.on("error", (error) => log("The agent's window did not start:", error.message));
  server.listen(WINDOW_PORT, "127.0.0.1");
  return server;
}
