// One line for something the agent did, as the conversation shows it: "Ran npm test", "Edited src/app.ts".
// Pure, so it is tested without a desktop (describe.test.mjs). Returns null for what is not worth a line.

const short = (value, max = 140) => {
  const text = String(value ?? "").split("\n")[0].trim();
  return text.length <= max ? text : text.slice(0, max - 1).trimEnd() + "…";
};

/** A path as a person would say it: relative to the workspace when it is inside it. */
const place = (file, workspace) => {
  const text = String(file ?? "");
  return workspace && text.startsWith(workspace + "/") ? text.slice(workspace.length + 1) : text;
};

const browser = {
  browser_navigate: (input) => `Opened ${short(input.url, 120)}`,
  browser_navigate_back: () => "Went back in the browser",
  browser_click: (input) => `Clicked ${short(input.element ?? "the page", 80)}`,
  browser_type: (input) => `Typed into ${short(input.element ?? "the page", 80)}`,
  browser_fill_form: () => "Filled in a form",
  browser_press_key: (input) => `Pressed ${short(input.key, 30)}`,
  browser_select_option: (input) => `Chose an option in ${short(input.element ?? "a list", 80)}`,
  browser_take_screenshot: () => "Looked at the browser",
  browser_snapshot: () => "Read the page",
  browser_tabs: () => "Looked at the browser's tabs",
  browser_wait_for: () => null,
  browser_evaluate: () => "Ran a script in the page",
  browser_file_upload: () => "Uploaded a file",
};

const desktop = {
  screenshot: () => "Looked at the screen",
  click: (input) => `Clicked at ${input.x}, ${input.y}`,
  type: () => "Typed on the keyboard",
  key: (input) => `Pressed ${short(input.keys, 30)}`,
  scroll: (input) => `Scrolled ${input.direction ?? "down"}`,
  open: (input) => `Opened ${short(input.target, 120)}`,
};

export function describe(name, input = {}, workspace = "") {
  switch (name) {
    case "Bash":
      return `Ran ${short(input.command)}`;
    case "Read":
      return `Read ${place(input.file_path, workspace)}`;
    case "Write":
      return `Wrote ${place(input.file_path, workspace)}`;
    case "Edit":
    case "MultiEdit":
    case "NotebookEdit":
      return `Edited ${place(input.file_path ?? input.notebook_path, workspace)}`;
    case "Glob":
      return `Looked for files matching ${short(input.pattern, 80)}`;
    case "Grep":
      return `Searched the files for ${short(input.pattern, 80)}`;
    case "WebFetch":
      return `Read ${short(input.url, 120)}`;
    case "WebSearch":
      return `Searched the web for ${short(input.query, 100)}`;
    case "Task":
    case "Agent":
      return `Started a helper: ${short(input.description ?? input.prompt, 100)}`;
    case "Skill":
      return `Used the ${short(input.skill ?? input.command, 60)} skill`;
    // Its own bookkeeping, not something done to the task.
    case "TodoWrite":
    case "ToolSearch":
    case "BashOutput":
    case "KillShell":
      return null;
  }
  const mcp = /^mcp__(browser|desktop)__(.+)$/.exec(name);
  if (mcp) {
    const say = (mcp[1] === "browser" ? browser : desktop)[mcp[2]];
    return say ? say(input) : `Used the ${mcp[1]}`;
  }
  return `Used ${short(name.replace(/^mcp__/, "").replaceAll("__", " "), 60)}`;
}

/**
 * What the agent said, as the conversation shows it. When the model gateway refuses a call (models not set
 * up, the team's allowance used), the harness reports it as "API Error: 400 …" followed by the gateway's own
 * words; the words are written for the person, so they are shown without the prefix.
 */
export function spoken(text) {
  return String(text ?? "").trim().replace(/^API Error: \d{3}\s+/, "");
}
