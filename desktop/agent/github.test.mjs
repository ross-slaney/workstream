import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createServer } from "node:http";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { askToken, authorFor, credentialAnswer, isGitHub, parseCredentialRequest, tokenFor } from "./github.mjs";
import { projectWork } from "./instructions.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));

/** The API, played: it answers the token route for one key, and counts what it was asked. */
async function api(answer = { status: 200, body: { token: "ghs_from_api", expiresAt: "2026-11-21T13:00:00Z", repository: "acme/app" } }) {
  const asked = [];
  const server = createServer((request, response) => {
    asked.push({ url: request.url, authorization: request.headers.authorization });
    const known = request.url === "/api/desktop/github/token" && request.headers.authorization === "Bearer wsd_key";
    response.writeHead(known ? answer.status : 401, { "content-type": "application/json" });
    response.end(JSON.stringify(known ? answer.body : {}));
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const url = `http://127.0.0.1:${server.address().port}`;
  return { url, asked, close: () => new Promise((resolve) => server.close(resolve)) };
}

/** git calling the helper: the request on its stdin, the answer on its stdout. */
function helper(args, input, env) {
  return new Promise((resolve, reject) => {
    const child = execFile(process.execPath, [path.join(here, "github.mjs"), ...args], { env, timeout: 20_000 }, (error, stdout, stderr) =>
      error ? reject(Object.assign(error, { stderr })) : resolve(stdout));
    child.stdin.end(input);
  });
}

/** The same, with what it wrote to stderr as well. */
function helperWithStderr(args, input, env) {
  return new Promise((resolve, reject) => {
    const child = execFile(process.execPath, [path.join(here, "github.mjs"), ...args], { env, timeout: 20_000 }, (error, stdout, stderr) =>
      error ? reject(error) : resolve({ stdout, stderr }));
    child.stdin.end(input);
  });
}

const told = (url) => ({ WORKSTREAM_API: url, WORKSTREAM_KEY: "wsd_key", PATH: process.env.PATH });

test("git's request is read line by line, and only GitHub over https is answered", () => {
  const request = parseCredentialRequest("protocol=https\nhost=github.com\npath=acme/app.git\n\n");
  assert.deepEqual(request, { protocol: "https", host: "github.com", path: "acme/app.git" });
  assert.equal(isGitHub(request), true);
  assert.equal(isGitHub({ protocol: "http", host: "github.com" }), false);
  assert.equal(isGitHub({ protocol: "https", host: "gitlab.com" }), false);
  assert.equal(credentialAnswer("ghs_x"), "username=x-access-token\npassword=ghs_x\n");
});

test("git gets a token the API mints for the desktop, asked for with the desktop's key", async () => {
  const stack = await api();
  try {
    const answer = await helper(["credential", "get"], "protocol=https\nhost=github.com\n\n", told(stack.url));
    assert.equal(answer, "username=x-access-token\npassword=ghs_from_api\n");
    assert.deepEqual(stack.asked, [{ url: "/api/desktop/github/token", authorization: "Bearer wsd_key" }]);
    // gh's wrapper takes the bare token.
    assert.equal(await helper(["token"], "", told(stack.url)), "ghs_from_api\n");
  } finally {
    await stack.close();
  }
});

test("git itself, set up as the agent sets it up, fills in the token", async () => {
  const stack = await api();
  try {
    const answer = await new Promise((resolve, reject) => {
      // The helper exactly as writeVariables (agent.mjs) configures it, and no other configuration.
      const helperSetting = `credential.https://github.com.helper=!node ${path.join(here, "github.mjs")} credential`;
      const env = { ...told(stack.url), GIT_CONFIG_GLOBAL: "/dev/null", GIT_CONFIG_NOSYSTEM: "1", GIT_TERMINAL_PROMPT: "0" };
      const child = execFile("git", ["-c", helperSetting, "credential", "fill"], { env, timeout: 20_000 }, (error, stdout) => (error ? reject(error) : resolve(stdout)));
      child.stdin.end("protocol=https\nhost=github.com\npath=acme/app.git\n\n");
    });
    assert.match(answer, /^username=x-access-token$/m);
    assert.match(answer, /^password=ghs_from_api$/m);
  } finally {
    await stack.close();
  }
});

test("a token pasted into the environment is used as it is, without asking", async () => {
  const stack = await api();
  try {
    const answer = await helper(["credential", "get"], "protocol=https\nhost=github.com\n\n", { ...told(stack.url), GITHUB_TOKEN: "ghp_pasted" });
    assert.equal(answer, "username=x-access-token\npassword=ghp_pasted\n");
    assert.equal(stack.asked.length, 0);
  } finally {
    await stack.close();
  }
});

test("with nothing to give, git hears no token and is told why, in the API's words", async () => {
  const why = "Grace Hopper has not signed in to Workstream with GitHub, so this desktop cannot reach GitHub as them.";
  const stack = await api({ status: 404, body: { title: why } });
  try {
    const said = await helperWithStderr(["credential", "get"], "protocol=https\nhost=github.com\n\n", told(stack.url));
    assert.equal(said.stdout, "");
    assert.equal(said.stderr, `Workstream: ${why}\n`);
    // gh's wrapper hears the same.
    assert.equal((await helperWithStderr(["token"], "", told(stack.url))).stderr, `Workstream: ${why}\n`);
    // Another host, or git storing or erasing what it was given: nothing is asked and nothing is kept.
    assert.equal(await helper(["credential", "get"], "protocol=https\nhost=gitlab.com\n\n", told(stack.url)), "");
    assert.equal(await helper(["credential", "store"], "protocol=https\nhost=github.com\npassword=x\n\n", told(stack.url)), "");
    assert.equal(stack.asked.length, 2);
  } finally {
    await stack.close();
  }
});

test("an API that cannot be reached gives no token rather than an error", async () => {
  const failing = async () => {
    throw new TypeError("fetch failed");
  };
  assert.equal(await tokenFor({ env: {}, told: async () => ({ api: "http://127.0.0.1:9", key: "wsd_key" }), fetch: failing }), null);
  assert.equal(await tokenFor({ env: {}, told: async () => ({ api: "", key: "" }) }), null);
  assert.match((await askToken({ env: {}, told: async () => ({ api: "http://127.0.0.1:9", key: "wsd_key" }), fetch: failing })).reason, /could not be reached/);
});

test("a desktop with a repository is told to finish on a branch and a pull request", () => {
  const told = projectWork({ url: "https://github.com/acme/app", branch: "develop" });
  assert.match(told, /https:\/\/github\.com\/acme\/app, on its branch develop/);
  assert.match(told, /gh pr create/);
  assert.match(told, /Never push to the default branch/);
});

test("a desktop without a repository is told it can clone one as the person, and to finish the same way", () => {
  const told = projectWork(null);
  assert.match(told, /No repository is cloned on this desktop/);
  assert.match(told, /gh repo clone owner\/name/);
  assert.match(told, /Never push to the default branch/);
});

test("commits are by the person who started the task, as the API says, and by nobody when it says nothing", async () => {
  const author = { name: "Ada Lovelace", email: "583231+octocat@users.noreply.github.com" };
  const asked = [];
  const answering = (status, body) => async (url, init) => {
    asked.push({ url, authorization: init.headers.authorization });
    return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
  };
  const told = async () => ({ api: "http://api.test", key: "wsd_key" });
  assert.deepEqual(await authorFor({ env: {}, told, fetch: answering(200, author) }), author);
  assert.deepEqual(asked, [{ url: "http://api.test/api/desktop/github/author", authorization: "Bearer wsd_key" }]);
  assert.equal(await authorFor({ env: {}, told, fetch: answering(404, { title: "Grace Hopper has not signed in to Workstream with GitHub." }) }), null);
  assert.equal(await authorFor({ env: {}, told, fetch: answering(200, { name: "", email: "x" }) }), null);
  assert.equal(await authorFor({ env: {}, told, fetch: async () => { throw new TypeError("fetch failed"); } }), null);
  assert.equal(await authorFor({ env: {}, told: async () => ({ api: "", key: "" }) }), null);
});
