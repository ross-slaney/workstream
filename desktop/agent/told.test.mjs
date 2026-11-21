import assert from "node:assert/strict";
import { test } from "node:test";
import { panelFor } from "./panel.mjs";
import { decodeUserData, parseTold } from "./told.mjs";

const encode = (text) => Buffer.from(text, "utf8").toString("base64");

test("a machine reads its API and its key out of its user data", () => {
  const told = decodeUserData(encode("WORKSTREAM_API=https://api.example.test/\nWORKSTREAM_KEY=wsk_abc=def\n"));
  assert.deepEqual(told, { api: "https://api.example.test", key: "wsk_abc=def" });
});

test("a machine told nothing has neither", () => {
  assert.deepEqual(decodeUserData(""), { api: "", key: "" });
  assert.deepEqual(decodeUserData(undefined), { api: "", key: "" });
  assert.deepEqual(parseTold("WORKSTREAM_API=https://api.example.test"), { api: "https://api.example.test", key: "" });
});

const catalog = [
  { id: "chrome", default: true },
  { id: "vscode", default: true },
  { id: "terminal", default: true },
  { id: "writer", default: false },
];

test("the default apps are what a new desktop already shows", () => {
  assert.deepEqual(panelFor(catalog, ["terminal", "chrome", "vscode"], undefined), { apps: ["chrome", "vscode", "terminal"], changed: false });
});

test("other apps mean the panel is written again, once", () => {
  const first = panelFor(catalog, ["chrome", "writer"], undefined);
  assert.deepEqual(first, { apps: ["chrome", "writer"], changed: true });
  assert.equal(panelFor(catalog, ["writer", "chrome"], first.apps).changed, false);
  assert.equal(panelFor(catalog, ["chrome", "vscode", "terminal"], first.apps).changed, true);
});

test("an app the desktop does not know is left out", () => {
  assert.deepEqual(panelFor(catalog, ["chrome", "vscode", "terminal", "gimp"], undefined), { apps: ["chrome", "vscode", "terminal"], changed: false });
});
