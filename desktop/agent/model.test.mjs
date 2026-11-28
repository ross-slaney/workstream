import assert from "node:assert/strict";
import http from "node:http";
import test from "node:test";
import { forwardedHeaders, modelAddress, startModelAddress, toolOf, TOOL_HEADER } from "./model.mjs";

test("a tool is named by the first word of what it calls itself", () => {
  assert.equal(toolOf("opencode/1.19.6 ai-sdk/provider-utils/4.0.46 runtime/bun/1.4.4"), "opencode");
  assert.equal(toolOf("claude-cli/2.1.0 (external, cli)"), "claude-cli");
  assert.equal(toolOf("My Tool"), "my");
  // A tool that says nothing is never taken for Workstream's own agent.
  assert.equal(toolOf(undefined), "other");
  assert.equal(toolOf("  "), "other");
  assert.equal(toolOf("<<>>"), "other");
  assert.equal(toolOf("a".repeat(100)).length, 40);
});

test("a call goes on with the desktop's key and the tool's name, and without the caller's own", () => {
  const headers = forwardedHeaders({
    "user-agent": "opencode/1.19.6", "x-api-key": "workstream", authorization: "Bearer mine", "anthropic-version": "2023-06-01",
    "content-type": "application/json", "content-length": "12", host: "127.0.0.1:7317", [TOOL_HEADER]: "workstream", "accept-encoding": "gzip",
  }, "the-desktops-key");
  assert.deepEqual(headers, {
    "user-agent": "opencode/1.19.6", "anthropic-version": "2023-06-01", "content-type": "application/json",
    "x-api-key": "the-desktops-key", [TOOL_HEADER]: "opencode",
  });
});

/** The model address on a port of its own, in front of a pretend gateway that answers with `answer`. */
async function address(answer, { key = "the-desktops-key" } = {}) {
  const calls = [];
  const send = async (url, init) => {
    calls.push({ url, ...init, body: init.body ? init.body.toString("utf8") : undefined });
    return answer(url, init);
  };
  const server = startModelAddress({ gateway: "https://workstream.test/api/model/anthropic/", key, port: 0, send });
  await new Promise((resolve) => server.once("listening", resolve));
  return { url: modelAddress(server.address().port), calls, close: () => new Promise((resolve) => server.close(resolve)) };
}

test("a tool's call reaches the gateway at the same path and its answer comes back as it is", async () => {
  const proxy = await address(() => new Response(JSON.stringify({ content: [{ type: "text", text: "Done." }] }), { status: 200, headers: { "content-type": "application/json", "request-id": "r1" } }));
  try {
    const answer = await fetch(`${proxy.url}/v1/messages?beta=true`, {
      method: "POST", headers: { "user-agent": "opencode/1.19.6", "x-api-key": "workstream", "content-type": "application/json" },
      body: JSON.stringify({ model: "claude-opus-5-5", messages: [] }),
    });
    assert.equal(answer.status, 200);
    assert.equal(answer.headers.get("request-id"), "r1");
    assert.equal((await answer.json()).content[0].text, "Done.");
    const [call] = proxy.calls;
    assert.equal(call.url, "https://workstream.test/api/model/anthropic/v1/messages?beta=true");
    assert.equal(call.method, "POST");
    assert.equal(call.headers["x-api-key"], "the-desktops-key");
    assert.equal(call.headers[TOOL_HEADER], "opencode");
    assert.equal(JSON.parse(call.body).model, "claude-opus-5-5");
  } finally {
    await proxy.close();
  }
});

test("a streamed answer arrives in the parts it was sent in", async () => {
  const proxy = await address(() => {
    const parts = ["event: message_start\ndata: {}\n\n", "event: message_stop\ndata: {}\n\n"];
    const body = new ReadableStream({
      async pull(controller) {
        const part = parts.shift();
        if (part === undefined) return controller.close();
        controller.enqueue(new TextEncoder().encode(part));
        await new Promise((resolve) => setTimeout(resolve, 30));
      },
    });
    return new Response(body, { status: 200, headers: { "content-type": "text/event-stream" } });
  });
  try {
    const answer = await fetch(`${proxy.url}/v1/messages`, { method: "POST", body: "{}" });
    assert.equal(answer.headers.get("content-type"), "text/event-stream");
    const seen = [];
    for await (const part of answer.body) seen.push(new TextDecoder().decode(part));
    assert.ok(seen.length >= 2, "the first event came before the last was sent");
    assert.match(seen.join(""), /message_start[\s\S]*message_stop/);
  } finally {
    await proxy.close();
  }
});

test("the gateway's refusal reaches the tool with its words", async () => {
  const refusal = { type: "error", error: { type: "invalid_request_error", message: "Your team has used its model allowance for this month. A system administrator can raise it." } };
  const proxy = await address(() => new Response(JSON.stringify(refusal), { status: 400, headers: { "content-type": "application/json" } }));
  try {
    const answer = await fetch(`${proxy.url}/v1/messages`, { method: "POST", body: "{}" });
    assert.equal(answer.status, 400);
    assert.deepEqual(await answer.json(), refusal);
  } finally {
    await proxy.close();
  }
});

test("when Workstream cannot be reached the tool is told so, in the shape it reads", async () => {
  const proxy = await address(() => {
    throw new Error("getaddrinfo ENOTFOUND");
  });
  try {
    const answer = await fetch(`${proxy.url}/v1/messages`, { method: "POST", body: "{}" });
    assert.equal(answer.status, 502);
    assert.equal((await answer.json()).error.message, "Workstream's models did not answer. Try again.");
  } finally {
    await proxy.close();
  }
});

test("it answers on this desktop only", async () => {
  const proxy = await address(() => new Response("{}"));
  try {
    const listening = await new Promise((resolve) => {
      const probe = http.get(proxy.url, (response) => resolve(response.socket.localAddress));
      probe.on("error", () => resolve(null));
    });
    assert.equal(listening, "127.0.0.1");
  } finally {
    await proxy.close();
  }
});
