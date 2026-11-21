#!/usr/bin/env node
// Tries a running Workstream stack from outside, the way a person's browser would: creates an account, starts
// a task, waits for its desktop, opens the desktop's display through the API's relay, writes to the agent,
// then stops, starts and deletes the task. It needs a stack that signs in with a password: the local AppHost
// or staging (.github/workflows/staging.yml). Production signs in by emailed code and cannot be tried this way.
//
//   node scripts/try-stack.mjs --api http://localhost:5280 --web http://localhost:8091
//   node scripts/try-stack.mjs --api https://workstream-staging-api.<domain> --web https://workstream-staging-web.<domain> --keep
//
//   --keep        leave the task and its desktop running at the end, and print how to sign in to look at it
//   --prompt "…"  what to ask the agent (default: a small task that needs the browser)
//   --wait 600    seconds to wait for the desktop to be ready (default 600)
//
// Prints one line per step with how long it took. Exits 1 at the first step that fails.

import { createHash, randomBytes, randomUUID } from "node:crypto";

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, arg, index, all) => {
    if (arg.startsWith("--")) pairs.push([arg.slice(2), all[index + 1] && !all[index + 1].startsWith("--") ? all[index + 1] : "true"]);
    return pairs;
  }, []),
);
const api = (args.api ?? "http://localhost:5280").replace(/\/+$/, "");
const web = (args.web ?? "http://localhost:8091").replace(/\/+$/, "");
const prompt = args.prompt ?? "Open example.com in the browser and tell me the page's main heading.";
const waitSeconds = Number(args.wait ?? 600);
const clientId = "workstream-app";
const scope = "openid profile email offline_access workstream.read workstream.write";
const redirectUri = `${web}/auth/callback`;

const started = Date.now();
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const b64 = (bytes) => Buffer.from(bytes).toString("base64url");
function step(text) {
  console.log(`${String(Math.round((Date.now() - started) / 1000)).padStart(4)}s  ${text}`);
}
function fail(text) {
  console.error(`FAILED: ${text}`);
  process.exit(1);
}

/** A person: signs up or in through SqlOS's headless flow, as the web app does, and calls the API. */
class Person {
  cookies = new Map();
  token = null;

  async flow(path, body) {
    const response = await fetch(`${api}/sqlos/auth/headless/${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json", Cookie: [...this.cookies].map(([k, v]) => `${k}=${v}`).join("; ") },
      body: JSON.stringify(body),
    });
    for (const cookie of response.headers.getSetCookie?.() ?? []) {
      const [pair] = cookie.split(";");
      const at = pair.indexOf("=");
      this.cookies.set(pair.slice(0, at), pair.slice(at + 1));
    }
    const text = await response.text();
    if (!response.ok) fail(`sign-in step ${path} answered ${response.status}: ${text.slice(0, 300)}`);
    return JSON.parse(text);
  }

  async sign(email, password, name) {
    const verifier = b64(randomBytes(32));
    const start = await this.flow("start", {
      responseType: "code", clientId, redirectUri, scope, state: b64(randomBytes(16)),
      codeChallenge: b64(createHash("sha256").update(verifier).digest()), codeChallengeMethod: "S256", view: name ? "signup" : "login",
    });
    const requestId = start.viewModel?.requestId ?? fail("SqlOS started no sign-in flow");
    let result = name
      ? await this.flow("signup", { requestId, displayName: name, email, password })
      : await this.flow("password/login", { requestId, email, password });
    if (!result.redirectUrl) {
      if (result.viewModel?.error) fail(`sign-in was refused: ${result.viewModel.error}`);
      const team = result.viewModel?.organizationSelection?.[0]?.id ?? fail("sign-in ended nowhere");
      result = await this.flow("organization/select", { requestId, organizationId: team });
    }
    const code = new URL(result.redirectUrl).searchParams.get("code");
    const tokens = await fetch(`${api}/sqlos/auth/token`, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ grant_type: "authorization_code", client_id: clientId, code, redirect_uri: redirectUri, code_verifier: verifier }),
    });
    if (!tokens.ok) fail(`the token exchange answered ${tokens.status}`);
    this.token = (await tokens.json()).access_token;
    return this;
  }

  async call(method, path, body) {
    const response = await fetch(`${api}${path}`, {
      method,
      headers: { Accept: "application/json", Authorization: `Bearer ${this.token}`, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await response.text();
    return { status: response.status, body: text ? safeJson(text) : null };
  }

  async get(path) {
    const answer = await this.call("GET", path);
    if (answer.status !== 200) fail(`GET ${path} answered ${answer.status}: ${JSON.stringify(answer.body).slice(0, 300)}`);
    return answer.body;
  }
}

function safeJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

/**
 * Open the desktop's display through the relay and speak enough of the display's protocol (RFB) to learn the
 * screen's size and name: proof that bytes travel from the browser's side to the desktop and back.
 */
function openDisplay(taskId, token) {
  return new Promise((resolve, reject) => {
    // The relay takes this stream only from the web app's own pages, so the script says where it came from as a browser would.
    const socket = new WebSocket(`${api.replace(/^http/, "ws")}/api/tasks/${taskId}/desktop`, { protocols: ["binary", `bearer.${token}`], headers: { Origin: web } });
    socket.binaryType = "arraybuffer";
    let buffer = Buffer.alloc(0);
    let phase = "version";
    const timer = setTimeout(() => {
      socket.close();
      reject(new Error(`the display did not answer (stopped at "${phase}")`));
    }, 30_000);
    socket.onerror = () => {
      clearTimeout(timer);
      reject(new Error("the relay refused the connection"));
    };
    socket.onclose = (event) => {
      clearTimeout(timer);
      if (phase !== "done") reject(new Error(`the relay closed the connection at "${phase}" (${event.code} ${event.reason})`));
    };
    socket.onmessage = (event) => {
      buffer = Buffer.concat([buffer, Buffer.from(event.data)]);
      if (phase === "version" && buffer.length >= 12) {
        const version = buffer.subarray(0, 12).toString("latin1");
        if (!version.startsWith("RFB ")) return reject(new Error(`not a display: ${JSON.stringify(version)}`));
        buffer = buffer.subarray(12);
        socket.send(Buffer.from("RFB 003.008\n", "latin1"));
        phase = "security";
      }
      if (phase === "security" && buffer.length >= 1 && buffer.length >= 1 + buffer[0]) {
        const types = [...buffer.subarray(1, 1 + buffer[0])];
        buffer = buffer.subarray(1 + types.length);
        if (!types.includes(1)) return reject(new Error(`the display asks for a password (security types ${types})`));
        socket.send(Buffer.from([1]));
        phase = "result";
      }
      if (phase === "result" && buffer.length >= 4) {
        if (buffer.readUInt32BE(0) !== 0) return reject(new Error("the display refused the handshake"));
        buffer = buffer.subarray(4);
        socket.send(Buffer.from([1])); // shared: others may watch too
        phase = "init";
      }
      if (phase === "init" && buffer.length >= 24 && buffer.length >= 24 + buffer.readUInt32BE(20)) {
        const size = { width: buffer.readUInt16BE(0), height: buffer.readUInt16BE(2), name: buffer.subarray(24, 24 + buffer.readUInt32BE(20)).toString("utf8") };
        phase = "done";
        clearTimeout(timer);
        socket.close();
        resolve(size);
      }
    };
  });
}

// --- The journey -------------------------------------------------------------------------------------------

const health = await fetch(`${api}/ready`).then((r) => r.json()).catch(() => null);
if (health?.status !== "ready") fail(`${api}/ready does not say ready`);
step(`${api} is ready at revision ${String(health.revision).slice(0, 12)}`);

const email = `try-${randomUUID().slice(0, 8)}@workstream.test`;
const password = `Try-${b64(randomBytes(12))}-9a`;
const person = await new Person().sign(email, password, "Trial Person");
const me = await person.get("/api/me");
const team = (await person.get("/api/teams")).data[0] ?? fail("signing up gave no team");
const environment = (await person.get(`/api/teams/${team.id}/environments`)).data[0] ?? fail("the team has no environment");
step(`signed up as ${me.name}, on "${team.name}", with the environment "${environment.name}" (${environment.apps.join(", ")})`);

const taskId = randomUUID();
const startedTask = await person.call("POST", `/api/teams/${team.id}/tasks`, { id: taskId, environmentId: environment.id, prompt, promptKey: randomUUID() });
if (startedTask.status !== 201) fail(`starting a task answered ${startedTask.status}: ${JSON.stringify(startedTask.body).slice(0, 300)}`);
step(`started task ${taskId} ("${startedTask.body.title}"), state ${startedTask.body.state}`);

let task = startedTask.body;
const deadline = Date.now() + waitSeconds * 1000;
while (task.state !== "ready") {
  if (task.state === "failed") fail(`the desktop failed: ${task.failure}`);
  if (Date.now() > deadline) fail(`the desktop was still ${task.state} after ${waitSeconds} seconds`);
  await sleep(3000);
  task = await person.get(`/api/tasks/${taskId}`);
}
step("the desktop is ready");

const screen = await openDisplay(taskId, person.token).catch((error) => fail(error.message));
step(`the display answers through the relay: ${screen.width}x${screen.height}, named "${screen.name}"`);

const stranger = await new Person().sign(`try-${randomUUID().slice(0, 8)}@workstream.test`, password, "A Stranger");
if ((await stranger.call("GET", `/api/tasks/${taskId}`)).status !== 404) fail("a stranger can see the task");
const refused = await openDisplay(taskId, stranger.token).then(() => "opened", () => "refused");
if (refused !== "refused") fail("a stranger can open the desktop's display");
step("a stranger can neither see the task nor open its display");

// What the agent answers: its words when it has a model, or the gateway's refusal when it has none.
let conversation = await person.get(`/api/tasks/${taskId}/messages?after=0`);
const answerDeadline = Date.now() + 240_000;
while (!conversation.messages.some((message) => message.author === "agent" && message.kind === "text") || conversation.task.agentWorking) {
  if (Date.now() > answerDeadline) break;
  await sleep(3000);
  conversation = await person.get(`/api/tasks/${taskId}/messages?after=0`);
}
const lines = conversation.messages.filter((message) => message.author === "agent");
if (lines.length === 0) fail("the agent wrote nothing in four minutes");
for (const line of lines.slice(-8)) step(`  agent ${line.kind === "activity" ? "did" : "said"}: ${line.text.replace(/\s+/g, " ").slice(0, 220)}`);
const usage = await person.get(`/api/teams/${team.id}/usage`);
step(`the team has spent $${usage.modelSpentUsd} of $${usage.modelAllowanceUsd} on models, with ${usage.runningDesktops} of ${usage.maxRunningDesktops} desktops running`);

if (args.keep === "true") {
  console.log(`\nLeft running. Sign in at ${web}/sign-in with:\n  email:    ${email}\n  password: ${password}\nand open ${web}/t/${team.id}?open=${taskId}`);
  process.exit(0);
}

const stopping = await person.call("POST", `/api/tasks/${taskId}/stop`);
if (stopping.status !== 200) fail(`stopping answered ${stopping.status}`);
while ((task = await person.get(`/api/tasks/${taskId}`)).state !== "stopped") {
  if (task.state === "failed") fail(`stopping failed: ${task.failure}`);
  if (Date.now() > deadline + 300_000) fail("the desktop never stopped");
  await sleep(3000);
}
step("the desktop is stopped");

const again = await person.call("POST", `/api/tasks/${taskId}/start`);
if (again.status !== 200) fail(`starting again answered ${again.status}`);
const restartDeadline = Date.now() + waitSeconds * 1000;
while ((task = await person.get(`/api/tasks/${taskId}`)).state !== "ready") {
  if (task.state === "failed") fail(`starting again failed: ${task.failure}`);
  if (Date.now() > restartDeadline) fail("the desktop never came back");
  await sleep(3000);
}
const back = await openDisplay(taskId, person.token).catch((error) => fail(error.message));
step(`the desktop is back, and its display answers again (${back.width}x${back.height})`);

const deleted = await person.call("DELETE", `/api/tasks/${taskId}`);
if (deleted.status !== 204) fail(`deleting answered ${deleted.status}`);
if ((await person.get(`/api/teams/${team.id}/tasks`)).data.length !== 0) fail("the deleted task is still listed");
step("the task is deleted");
console.log("\nEvery step worked.");
