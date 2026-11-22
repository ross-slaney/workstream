// What the command line was asked to do: the command, its options and its words, checked before anything is
// sent anywhere. Pure, so the tests (args.test.mjs) need neither a network nor a home directory.

import { parseArgs } from "node:util";

export class UsageError extends Error {}

export const USAGE = `Start and follow Workstream tasks from a terminal.

Usage:
  workstream login [--api <url>] [--no-browser]
      Sign in through your browser. --api is the Workstream API's address; it is remembered.
  workstream logout
      Sign this computer out.
  workstream start [--team <name|id>] [--env <name|id>] [--title <title>] [--no-wait] [--timeout <seconds>] [--json] <prompt | ->
      Start a task, print its link, and wait until its desktop is ready. "-" reads the prompt from standard input.
      --team and --env can be left out when there is only one to choose from.
  workstream status <task id | link> [--json]
      Show where a task is.
  workstream tasks [--team <name|id>] [--json]
      List the latest tasks of your teams, or of one team.
  workstream envs [--team <name|id>] [--json]
      List your teams' environments, with the repository each starts with.
  workstream help
`;

const COMMANDS = {
  login: { api: { type: "string" }, "no-browser": { type: "boolean" } },
  logout: {},
  start: {
    team: { type: "string" },
    env: { type: "string" },
    title: { type: "string" },
    "no-wait": { type: "boolean" },
    timeout: { type: "string" },
    json: { type: "boolean" },
  },
  status: { json: { type: "boolean" } },
  tasks: { team: { type: "string" }, json: { type: "boolean" } },
  envs: { team: { type: "string" }, json: { type: "boolean" } },
  help: {},
};

/**
 * The command and what goes with it. Throws a {@link UsageError}, in words to show, for anything it does not
 * understand: an unknown command or option, a missing prompt or task, a timeout that is not a number.
 */
export function parse(argv) {
  const [first, ...rest] = argv;
  if (first === undefined || first === "help" || first === "--help" || first === "-h") return { command: "help", options: {}, positionals: [] };
  if (first === "--version" || first === "-v") return { command: "version", options: {}, positionals: [] };
  const spec = COMMANDS[first];
  if (!spec) throw new UsageError(`There is no command "${first}". Run workstream help.`);
  let parsed;
  try {
    // "-" is a word here, the prompt from standard input, so it is kept as a positional.
    parsed = parseArgs({ args: rest, options: spec, allowPositionals: true, strict: true });
  } catch (error) {
    throw new UsageError(error instanceof Error ? error.message.replace(/^\w+ \[[^\]]+\]: /, "") : String(error));
  }
  const options = { ...parsed.values };
  const positionals = parsed.positionals;

  switch (first) {
    case "login":
      if (positionals.length) throw new UsageError("login takes no words; give the address with --api.");
      if (options.api !== undefined) options.api = apiOrigin(options.api);
      break;
    case "logout":
    case "help":
      if (positionals.length) throw new UsageError(`${first} takes no words.`);
      break;
    case "start": {
      if (!positionals.length) throw new UsageError('Say what the task is: workstream start "Fix the failing test" (or - to read it from standard input).');
      if (positionals.includes("-") && positionals.length > 1) throw new UsageError('Give the prompt as words or as "-", not both.');
      options.prompt = positionals.includes("-") ? null : positionals.join(" ").trim();
      if (options.prompt === "") throw new UsageError("The prompt is empty.");
      options.wait = !options["no-wait"];
      delete options["no-wait"];
      if (options.timeout !== undefined) {
        const seconds = Number(options.timeout);
        if (!Number.isFinite(seconds) || seconds <= 0) throw new UsageError("--timeout is a number of seconds.");
        options.timeout = seconds;
      } else {
        options.timeout = 900;
      }
      return { command: first, options, positionals: [] };
    }
    case "status":
      if (positionals.length !== 1) throw new UsageError("Say which task: workstream status <task id or link>.");
      return { command: first, options, positionals: [taskIdFrom(positionals[0])] };
    case "tasks":
    case "envs":
      if (positionals.length) throw new UsageError(`${first} takes no words; choose a team with --team.`);
      break;
  }
  return { command: first, options, positionals };
}

/** An API address as a person types it, without a trailing slash; only http and https. */
export function apiOrigin(text) {
  let url;
  try {
    url = new URL(text.includes("://") ? text : `https://${text}`);
  } catch {
    throw new UsageError(`"${text}" is not an address.`);
  }
  if (url.protocol !== "https:" && url.protocol !== "http:") throw new UsageError(`"${text}" is not an http or https address.`);
  return `${url.origin}${url.pathname.replace(/\/+$/, "")}`;
}

/** A task's id, from the id itself or from its link in the web app (…/t/{team}?open={task}). */
export function taskIdFrom(text) {
  const trimmed = text.trim();
  if (/^https?:\/\//i.test(trimmed)) {
    const url = new URL(trimmed);
    const open = url.searchParams.get("open");
    const id = open?.split(",").find((part) => part.trim());
    if (!id) throw new UsageError("That link does not open a task.");
    return id.trim();
  }
  if (!trimmed) throw new UsageError("Say which task.");
  return trimmed;
}

/** A task's link in the web app: its team's workspace with the task open. */
export function taskLink(web, task) {
  return `${web.replace(/\/+$/, "")}/t/${encodeURIComponent(task.teamId)}?open=${encodeURIComponent(task.id)}`;
}

/**
 * The one of `items` that `wanted` names, by id or by name (any case). Without `wanted`, the only one there
 * is. Throws in words that list what there is to choose from.
 */
export function choose(items, wanted, kind) {
  const names = items.map((item) => `  ${item.name} (${item.id})`).join("\n");
  if (wanted === undefined) {
    if (items.length === 1) return items[0];
    if (!items.length) throw new UsageError(`You have no ${kind}s.`);
    throw new UsageError(`Choose a ${kind} with --${kind === "team" ? "team" : "env"}:\n${names}`);
  }
  const byId = items.find((item) => item.id === wanted);
  if (byId) return byId;
  const byName = items.filter((item) => item.name.toLowerCase() === wanted.trim().toLowerCase());
  if (byName.length === 1) return byName[0];
  if (byName.length > 1) throw new UsageError(`More than one ${kind} is called "${wanted}". Use its id:\n${names}`);
  throw new UsageError(`No ${kind} is called "${wanted}". There is:\n${names || "  nothing"}`);
}

/** Where a task's desktop is, in words. */
export function describe(task) {
  switch (task.state) {
    case "starting":
      return task.step === "agent" ? "The desktop is up; the agent is starting" : task.step === "boot" ? "The desktop is starting up" : "Making the desktop";
    case "ready":
      return task.agentWorking ? "Ready; the agent is working" : "Ready";
    // The API's own words for pausing and paused.
    case "stopping":
      return "Pausing";
    case "stopped":
      return "Paused";
    case "archiving":
      return "Being archived";
    case "archived":
      return "Archived";
    case "failed":
      return task.failure ? `Failed: ${task.failure}` : "Failed";
    case "deleting":
      return "Being deleted";
    default:
      return task.state;
  }
}
