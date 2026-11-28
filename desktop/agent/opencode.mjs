// OpenCode on the desktop, ready with no sign-in.
//
// OpenCode (opencode.ai) is an open-source coding agent that runs in a terminal. It is in the desktops' image
// (image/install.sh), and this writes the settings that point it at the desktop's own model address
// (model.mjs), so a person who opens a terminal types `opencode` and works. It asks for no account and holds
// no key: its "key" below is a word the model address ignores. Its calls are counted to the team like the
// Workstream agent's, under the tool's own name.
//
// The settings are a file of Workstream's own (OPENCODE_CONFIG), read on top of the person's
// ~/.config/opencode, so a person's own theme and keys stay theirs, and a repository's opencode.json still applies.

import path from "node:path";
import { modelAddress } from "./model.mjs";

/** Where the settings are written, inside the folder everything of this desktop's is in. */
export const openCodeFile = (dir) => path.join(dir, "opencode.json");

/** "claude-opus-5-5" as a person reads it: "Claude Opus 5.5". A name that is not a model's family and version is left as it is. */
export function modelName(id) {
  const words = String(id ?? "").split("-");
  const version = [];
  while (words.length > 1 && /^\d+$/.test(words.at(-1))) version.unshift(words.pop());
  const name = words.map((word) => word.charAt(0).toUpperCase() + word.slice(1)).join(" ");
  return version.length > 0 ? `${name} ${version.join(".")}` : name;
}

/**
 * OpenCode's settings for this desktop. `model` is the setup's: the gateway's address and the three models
 * the deployment has. The main one is what OpenCode starts on, as the Workstream agent does; the small one
 * names sessions. Only Workstream's models are offered, so nothing on the list asks for a key.
 */
export function openCodeSettings(model, address = modelAddress()) {
  const ids = [...new Set([model.main, model.sonnet, model.small].filter(Boolean))];
  return {
    $schema: "https://opencode.ai/config.json",
    // The image has the version Workstream tried. A session is never sent to OpenCode's sharing service.
    autoupdate: false,
    share: "disabled",
    model: `anthropic/${model.main}`,
    small_model: `anthropic/${model.small ?? model.main}`,
    enabled_providers: ["anthropic"],
    provider: {
      anthropic: {
        options: { baseURL: `${address}/v1`, apiKey: "workstream" },
        // The deployment's own models and no others: OpenCode's list of Claude models is longer than what Workstream has.
        whitelist: ids,
        models: Object.fromEntries(ids.map((id) => [id, { name: modelName(id) }])),
      },
    },
  };
}

/** What a person who opened the desktop to work on it reads in its first terminal. */
export const welcome = `
This desktop is yours to work on.

  opencode    An AI coding agent in this terminal. It is already connected: no sign-in and no key.
  git, gh     Signed in to GitHub as you, when you signed in to Workstream with GitHub.

You can also write to the Workstream agent beside the desktop at any time. It works on this same desktop.
`;

/** What every shell on the desktop is given, so `opencode` finds its settings and any other tool can find the model address. */
export const openCodeVariables = (dir, address = modelAddress()) => ({
  OPENCODE_CONFIG: openCodeFile(dir),
  WORKSTREAM_MODEL_URL: address,
});
