import assert from "node:assert/strict";
import test from "node:test";
import { modelName, openCodeFile, openCodeSettings, openCodeVariables, welcome } from "./opencode.mjs";

const model = { baseUrl: "https://workstream.test/api/model/anthropic", main: "claude-opus-5-5", sonnet: "claude-sonnet-5-5", small: "claude-haiku-5-5" };

test("OpenCode is pointed at the desktop's model address and holds no key", () => {
  const settings = openCodeSettings(model);
  assert.equal(settings.provider.anthropic.options.baseURL, "http://127.0.0.1:7317/v1");
  // A word the model address ignores: the desktop's key is added there, and is in no file.
  assert.equal(settings.provider.anthropic.options.apiKey, "workstream");
  assert.ok(!JSON.stringify(settings).includes(model.baseUrl), "it never calls the gateway itself");
});

test("it starts on the model the Workstream agent uses and offers only the deployment's models", () => {
  const settings = openCodeSettings(model);
  assert.equal(settings.model, "anthropic/claude-opus-5-5");
  assert.equal(settings.small_model, "anthropic/claude-haiku-5-5");
  assert.deepEqual(settings.enabled_providers, ["anthropic"]);
  assert.deepEqual(settings.provider.anthropic.whitelist, ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"]);
  assert.deepEqual(Object.keys(settings.provider.anthropic.models), ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"]);
  assert.equal(settings.provider.anthropic.models["claude-opus-5-5"].name, "Claude Opus 5.5");
});

test("a deployment with one model for everything lists it once", () => {
  const settings = openCodeSettings({ main: "claude-sonnet-5-5", sonnet: "claude-sonnet-5-5", small: "claude-sonnet-5-5" });
  assert.deepEqual(settings.provider.anthropic.whitelist, ["claude-sonnet-5-5"]);
});

test("it does not update itself or share a session", () => {
  const settings = openCodeSettings(model);
  assert.equal(settings.autoupdate, false);
  assert.equal(settings.share, "disabled");
});

test("a model's name is read as a person would say it", () => {
  assert.equal(modelName("claude-opus-5-5"), "Claude Opus 5.5");
  assert.equal(modelName("claude-haiku-5"), "Claude Haiku 5");
  assert.equal(modelName("my-deployment"), "My Deployment");
});

test("every shell is told where OpenCode's settings and the model address are", () => {
  assert.equal(openCodeFile("/home/work/.workstream"), "/home/work/.workstream/opencode.json");
  assert.deepEqual(openCodeVariables("/home/work/.workstream"), { OPENCODE_CONFIG: "/home/work/.workstream/opencode.json", WORKSTREAM_MODEL_URL: "http://127.0.0.1:7317" });
});

test("the first terminal says what is ready in words a shell can print", () => {
  assert.match(welcome, /opencode/);
  assert.ok(!welcome.includes("'"), "it is printed inside single quotes");
});
