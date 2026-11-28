// The desktop's own model address: http://127.0.0.1:7317.
//
// A tool a person runs on the desktop, such as OpenCode in a terminal, is pointed here and given no key. Each
// call is passed on to Workstream's model gateway (api/Model/ModelGateway.cs) with the desktop's key added
// and with which tool made it, read from the tool's User-Agent. The key stays in this process: it is in no
// file, in no shell's variables and in no tool's settings, so nothing a tool writes and no saved disk carries
// it. Any program that speaks Anthropic's Messages API works the same way, with no sign-in.
//
// It gives the desktop nothing it did not have. A program on the desktop can already ask for the desktop's
// key where the agent does (told.mjs); this only saves every tool from having to.

import http from "node:http";

/** A port a project's own servers are unlikely to want. */
export const MODEL_PORT = 7317;

export const modelAddress = (port = MODEL_PORT) => `http://127.0.0.1:${port}`;

/** Which tool made the call, for the team's usage (ModelGateway.ToolHeader). */
export const TOOL_HEADER = "x-workstream-tool";

/** A request larger than this is refused here, as the gateway would refuse it. */
const MAX_REQUEST_BYTES = 64 * 1024 * 1024;

/** Headers of one hop, or that this address sets itself. A caller's own key or tool name is never passed on. */
const NOT_FORWARDED = new Set([
  "host", "connection", "keep-alive", "proxy-authorization", "proxy-connection", "te", "trailer", "transfer-encoding", "upgrade",
  "authorization", "x-api-key", "api-key", "content-length", "accept-encoding", "cookie", "origin", "referer", TOOL_HEADER,
]);

/** fetch has already undone the answer's encoding and framing. */
const NOT_RETURNED = new Set(["connection", "keep-alive", "transfer-encoding", "content-length", "content-encoding"]);

/**
 * What a tool calls itself: the first word of its User-Agent, before any version, in lower case. A tool that
 * says nothing is "other", so that its calls are never taken for Workstream's own agent's.
 */
export function toolOf(userAgent) {
  const word = String(userAgent ?? "").trim().split(/[\s/(;,]/)[0].toLowerCase().replace(/[^a-z0-9._-]/g, "").slice(0, 40);
  return word || "other";
}

/** The caller's headers as they go on to the gateway: its own credentials dropped, the desktop's key and the tool's name added. */
export function forwardedHeaders(headers, key) {
  const forwarded = {};
  for (const [name, value] of Object.entries(headers ?? {})) {
    if (value === undefined || NOT_FORWARDED.has(name.toLowerCase())) continue;
    forwarded[name] = Array.isArray(value) ? value.join(", ") : value;
  }
  forwarded["x-api-key"] = key;
  forwarded[TOOL_HEADER] = toolOf(headers?.["user-agent"]);
  return forwarded;
}

/** A refusal in the shape Anthropic's clients read, so the tool shows the words. */
const refusal = (message) => JSON.stringify({ type: "error", error: { type: "api_error", message } });

async function bodyOf(request) {
  const parts = [];
  let size = 0;
  for await (const part of request) {
    size += part.length;
    if (size > MAX_REQUEST_BYTES) return null;
    parts.push(part);
  }
  return Buffer.concat(parts);
}

/**
 * Answer on the desktop's model address. `gateway` is where the gateway serves Anthropic's API (the setup's
 * `model.baseUrl`), so a call to /v1/messages here is a call to /v1/messages there.
 */
export function startModelAddress({ gateway, key, port = MODEL_PORT, log = () => {}, send = fetch }) {
  const server = http.createServer(async (request, response) => {
    const gone = new AbortController();
    response.on("close", () => gone.abort());
    try {
      const sends = !["GET", "HEAD"].includes(request.method);
      const body = sends ? await bodyOf(request) : undefined;
      if (body === null) {
        response.writeHead(413, { "content-type": "application/json" }).end(refusal("The request is too large."));
        return;
      }
      const answer = await send(`${gateway.replace(/\/+$/, "")}${request.url}`, {
        method: request.method, headers: forwardedHeaders(request.headers, key), body, signal: gone.signal,
      });
      const headers = {};
      answer.headers.forEach((value, name) => {
        if (!NOT_RETURNED.has(name.toLowerCase())) headers[name] = value;
      });
      response.writeHead(answer.status, headers);
      // An answer is streamed on as it arrives: a tool draws the model's words as they come.
      if (answer.body) for await (const part of answer.body) response.write(part);
      response.end();
    } catch (error) {
      if (gone.signal.aborted) return;
      log("A model call did not reach Workstream:", error.message);
      if (response.headersSent) response.destroy();
      else response.writeHead(502, { "content-type": "application/json" }).end(refusal("Workstream's models did not answer. Try again."));
    }
  });
  // Another program has the port, or this agent started twice: the tools are told nothing answers, in the log.
  server.on("error", (error) => log("The desktop's model address is not answering:", error.message));
  server.listen(port, "127.0.0.1");
  return server;
}
