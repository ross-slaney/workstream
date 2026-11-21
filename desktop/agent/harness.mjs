// The harness: the one file that knows which agent runs on the desktop. It is the Claude Agent SDK (the engine
// behind Claude Code) with every model call sent to Workstream's model gateway, which holds the real Foundry
// key. The rest of the agent only hands this file prompts and receives what the agent said and did, so another
// harness would replace this file and nothing else.

import { spawn } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { query } from "@anthropic-ai/claude-agent-sdk";
import { describe, spoken } from "./describe.mjs";
import { desktopTools } from "./tools.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));

/** What the agent is told about where it is, added to the harness's own instructions. */
const onThisDesktop = (workspace) => `
You are the agent on a Workstream desktop: a Linux computer (Debian with the XFCE desktop) set up for one task. People watch this desktop live in their browser and can take the mouse and keyboard at any time, so do the work where they can see it.

- Graphical programs you start appear on the screen (display :1).
- For anything on the web, use the browser tools (mcp__browser__*). They drive the Chromium window on the desktop, the same one the person can use.
- For other graphical programs, use the desktop tools (mcp__desktop__*): take a screenshot to look, then click, type and press keys.
- Files you change open in VS Code as you change them. Your working folder is ${workspace}.
- Start long-running programs (a dev server, a watcher) in the background and say where they listen. Leave them running when you finish, with the result open in the browser, so the person can try it.
- You have sudo without a password. Install what the task needs.
- When you finish, say what you did and what is on screen for the person to look at.
- The person reads your messages in a narrow panel beside the desktop. Write plain, short paragraphs.
`.trim();

/** Models are reached as Foundry serves them, through the gateway, with the desktop's own key. */
const modelEnvironment = (setup, key) => ({
  CLAUDE_CODE_USE_FOUNDRY: "1",
  ANTHROPIC_FOUNDRY_BASE_URL: setup.model.baseUrl,
  ANTHROPIC_FOUNDRY_API_KEY: key,
  ANTHROPIC_MODEL: setup.model.main,
  ANTHROPIC_DEFAULT_OPUS_MODEL: setup.model.main,
  ANTHROPIC_DEFAULT_SONNET_MODEL: setup.model.sonnet,
  ANTHROPIC_DEFAULT_HAIKU_MODEL: setup.model.small,
  // The desktop talks to Workstream and to nothing of the harness's own.
  DISABLE_TELEMETRY: "1",
  DISABLE_ERROR_REPORTING: "1",
  DISABLE_AUTOUPDATER: "1",
  CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: "1",
});

/**
 * Run the agent for as long as the desktop is up.
 * @param {object} with
 * @param {object} with.setup      What the API says this desktop is set up from.
 * @param {string} with.key        The desktop's key.
 * @param {string} with.workspace  The folder the agent works in.
 * @param {object} with.state      Kept across restarts: `sessionId` is the conversation to resume.
 * @param {() => Promise<void>} with.saveState
 * @param {(kind: "text" | "activity", text: string) => void} with.say
 * @param {(working: boolean) => void} with.setWorking
 * @param {{ take(signal: AbortSignal): Promise<string>, size: number }} with.prompts
 * @param {() => Promise<void>} with.browserReady  Resolves when the desktop's browser can be driven.
 * @param {(...args: unknown[]) => void} with.log
 * @returns {{ interrupt(): void }}
 */
export function startHarness({ setup, key, workspace, state, saveState, say, setWorking, prompts, browserReady, log }) {
  let current = null;

  const environment = { ...process.env, ...setup.variables, ...modelEnvironment(setup, key) };
  const options = () => ({
    cwd: workspace,
    env: environment,
    model: setup.model.main,
    // The desktop is the task's own machine, made for it and deleted with it: nothing here asks before acting.
    permissionMode: "bypassPermissions",
    allowDangerouslySkipPermissions: true,
    systemPrompt: {
      type: "preset",
      preset: "claude_code",
      append: [onThisDesktop(workspace), setup.instructions].filter(Boolean).join("\n\n"),
    },
    // The repository's own instructions and skills (CLAUDE.md, .claude/skills) apply, as they would on a laptop.
    settingSources: ["user", "project", "local"],
    mcpServers: {
      desktop: desktopTools(),
      // Attached to the Chromium on the desktop, so there is one browser for the agent and the person.
      browser: { command: process.execPath, args: [path.join(here, "node_modules/@playwright/mcp/cli.js"), "--cdp-endpoint", "http://127.0.0.1:9222"] },
    },
    hooks: {
      // What the agent is working in is what the person sees: the browser comes to the front when the agent
      // uses it, and a file the agent changes opens in the editor.
      PreToolUse: [{ matcher: "mcp__browser__.*", hooks: [showBrowser] }],
      PostToolUse: [{ matcher: "Edit|Write|MultiEdit", hooks: [showInEditor] }],
    },
    resume: state.sessionId ?? undefined,
    stderr: (line) => log("harness:", line.trimEnd()),
  });

  /**
   * Before a browser tool runs: the browser is up (the agent may reach for it while the desktop is still
   * drawing itself), and its window comes to the front.
   */
  async function showBrowser() {
    await browserReady();
    spawn("wmctrl", ["-x", "-a", "chromium"], { detached: true, stdio: "ignore", env: environment }).on("error", () => {}).unref();
    return {};
  }

  /** A file the agent just changed opens in VS Code, where the person is watching. */
  async function showInEditor(input) {
    const file = input.tool_input?.file_path;
    if (file && setup.apps.includes("vscode")) {
      spawn("code", ["--reuse-window", file], { detached: true, stdio: "ignore", env: environment }).on("error", () => {}).unref();
    }
    return {};
  }

  /** One session: prompts in as people write them, messages out as the agent works. Ends only when it breaks. */
  async function session() {
    const ending = new AbortController();
    async function* incoming() {
      while (true) {
        const text = await prompts.take(ending.signal);
        setWorking(true);
        yield { type: "user", message: { role: "user", content: [{ type: "text", text }] }, parent_tool_use_id: null, session_id: state.sessionId ?? "" };
      }
    }
    try {
      current = query({ prompt: incoming(), options: options() });
      for await (const message of current) await heard(message);
    } finally {
      current = null;
      // Whatever prompt the stream was waiting for goes back to the queue for the next session.
      ending.abort();
      setWorking(false);
    }
  }

  async function heard(message) {
    switch (message.type) {
      case "system":
        if (message.subtype === "init" && message.session_id && message.session_id !== state.sessionId) {
          state.sessionId = message.session_id;
          await saveState();
        }
        break;
      case "assistant":
        // A helper's own messages stay with the helper; its result reaches the conversation through the agent.
        if (message.parent_tool_use_id) break;
        for (const block of message.message?.content ?? []) {
          if (block.type === "text" && spoken(block.text)) say("text", spoken(block.text));
          if (block.type === "tool_use") {
            const line = describe(block.name, block.input ?? {}, workspace);
            if (line) say("activity", line);
          }
        }
        break;
      case "result":
        if (message.subtype !== "success") say("text", "I had to stop before finishing. Tell me to continue, or what to do instead.");
        setWorking(prompts.size > 0);
        break;
    }
  }

  (async () => {
    while (true) {
      try {
        await session();
      } catch (error) {
        log("The agent's session ended:", error);
        say("text", `I ran into a problem and am starting again: ${String(error?.message ?? error).slice(0, 300)}`);
      }
      await new Promise((resolve) => setTimeout(resolve, 3000));
    }
  })();

  return {
    interrupt() {
      current?.interrupt().catch((error) => log("Interrupt failed:", error));
    },
  };
}
