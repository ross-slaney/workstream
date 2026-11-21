import { test } from "node:test";
import assert from "node:assert/strict";
import { gitIdentity, withGitHub } from "./github.mjs";

test("a GITHUB_TOKEN is also given as GH_TOKEN, which gh reads first", () => {
  assert.deepEqual(withGitHub({ GITHUB_TOKEN: "t", OTHER: "x" }), { GITHUB_TOKEN: "t", GH_TOKEN: "t", OTHER: "x" });
  assert.deepEqual(withGitHub({ GH_TOKEN: "g" }), { GH_TOKEN: "g", GITHUB_TOKEN: "g" });
  assert.deepEqual(withGitHub({ GITHUB_TOKEN: "a", GH_TOKEN: "b" }), { GITHUB_TOKEN: "a", GH_TOKEN: "b" });
  assert.deepEqual(withGitHub({ OTHER: "x" }), { OTHER: "x" });
});

test("commits are made as the token's account, at its private address", async () => {
  const asked = [];
  const fetch = async (url, init) => {
    asked.push([url, init.headers.authorization]);
    return { ok: true, json: async () => ({ login: "octo", id: 42, name: "Octo Cat" }) };
  };
  assert.deepEqual(await gitIdentity({ GITHUB_TOKEN: "t" }, { fetch }), { name: "Octo Cat", email: "42+octo@users.noreply.github.com" });
  assert.deepEqual(asked, [["https://api.github.com/user", "Bearer t"]]);
});

test("nobody is asked when there is no token or the environment names the author", async () => {
  const fetch = async () => assert.fail("GitHub was asked");
  assert.equal(await gitIdentity({}, { fetch }), null);
  assert.equal(await gitIdentity({ GITHUB_TOKEN: "t", GIT_AUTHOR_NAME: "A", GIT_AUTHOR_EMAIL: "a@b.c" }, { fetch }), null);
});

test("a refused or broken answer leaves git as it is", async () => {
  assert.equal(await gitIdentity({ GITHUB_TOKEN: "t" }, { fetch: async () => ({ ok: false }) }), null);
  assert.equal(await gitIdentity({ GITHUB_TOKEN: "t" }, { fetch: async () => { throw new Error("offline"); } }), null);
});
