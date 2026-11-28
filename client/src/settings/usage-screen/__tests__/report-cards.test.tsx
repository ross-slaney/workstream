import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { ModelReport } from "@/lib/workstream/api";
import { LinesList, LinesTable, ReportCards } from "../parts";

/* Who and what used the team's models: the whole team's for an administrator, the reader's own for anyone else. */

const report = (changes: Partial<ModelReport> = {}): ModelReport => ({
  since: "2026-11-01T00:00:00Z", everyone: true, costUsd: 12, calls: 340,
  people: [
    { userId: "ada", name: "Ada Lovelace", costUsd: 9, calls: 300, tasks: 4, desktopHours: 13.64 },
    { userId: "lin", name: "Lin Clark", costUsd: 3, calls: 40, tasks: 1, desktopHours: 2 },
  ],
  tools: [
    { tool: "workstream", costUsd: 8, calls: 250 },
    { tool: "opencode", costUsd: 3.996, calls: 89 },
    { tool: "aider", costUsd: 0.004, calls: 1 },
  ],
  models: [{ model: "claude-opus-5-5", costUsd: 12, calls: 340, inputTokens: 1_240_000, outputTokens: 340_000 }],
  days: [
    { day: "2026-11-01", costUsd: 0, calls: 0 },
    { day: "2026-11-02", costUsd: 12, calls: 340 },
  ],
  tasks: [
    { taskId: "t1", title: "Fix the sign-up form", userName: "Ada Lovelace", costUsd: 9, calls: 300 },
    { taskId: "t2", title: null, userName: "Lin Clark", costUsd: 3, calls: 40 },
  ],
  ...changes,
});

const drawn = (value: ModelReport, wide = true) =>
  renderToStaticMarkup(<ReportCards report={{ kind: "ready", value }} draw={(lines) => (wide ? <LinesTable {...lines} /> : <LinesList {...lines} />)} />);

describe("the AI use cards", () => {
  it("lead with the month's cost and calls, and a bar for each day", () => {
    const markup = drawn(report());
    expect(markup).toContain("AI use day by day");
    expect(markup).toContain("$12.00 in 340 calls");
    expect(markup).toContain("Nov 2: $12.00 in 340 calls");
    expect(markup).toContain("Nov 1: $0.00 in 0 calls");
  });

  it("show an administrator each person's tasks, desktop time, calls and cost", () => {
    const markup = drawn(report());
    expect(markup).toContain(">People<");
    expect(markup).toContain("Ada Lovelace");
    expect(markup).toContain("4 tasks");
    expect(markup).toContain("13.6 hours");
    expect(markup).toContain("300 calls");
    expect(markup).toContain("$9.00");
    expect(markup).toContain("Lin Clark");
    expect(markup).toContain("1 task<");
  });

  it("show anyone else their own use, and say an administrator sees everyone's", () => {
    const own = report({ everyone: false, people: [{ userId: "lin", name: "Lin Clark", costUsd: 3, calls: 40, tasks: 1, desktopHours: 2 }] });
    const markup = drawn(own);
    expect(markup).toContain("Your use");
    expect(markup).not.toContain(">People<");
    expect(markup).toContain("A team administrator sees everyone");
    // Their own tasks need no "Started by".
    expect(markup).not.toContain("Started by");
  });

  it("name the tools people know, and show any other by what it called itself", () => {
    const markup = drawn(report());
    expect(markup).toContain("Workstream agent");
    expect(markup).toContain("OpenCode");
    expect(markup).toContain("aider");
    expect(markup).toContain("&lt;$0.01");
  });

  it("say a model's name and how much was sent and answered", () => {
    const markup = drawn(report());
    expect(markup).toContain("Claude Opus 5.5");
    expect(markup).toContain("1.2M tokens");
    expect(markup).toContain("340K tokens");
  });

  it("list the tasks that used most, with who started each, and say when a task was deleted", () => {
    const markup = drawn(report());
    expect(markup).toContain("Fix the sign-up form");
    expect(markup).toContain("Started by Ada Lovelace");
    expect(markup).toContain("A deleted task");
  });

  it("say so when nothing was used", () => {
    const markup = drawn(report({ costUsd: 0, calls: 0, people: [], tools: [], models: [], tasks: [], days: [{ day: "2026-11-01", costUsd: 0, calls: 0 }] }));
    expect(markup).toContain("No AI model has been used this month.");
    expect(markup).toContain("Nobody has used a desktop this month.");
  });

  it("draw the same lines on a phone, each amount beside its heading", () => {
    const markup = drawn(report(), false);
    expect(markup).not.toContain("<table");
    expect(markup).toContain("Ada Lovelace");
    expect(markup).toContain("AI cost");
    expect(markup).toContain("$9.00");
  });

  it("draw nothing until the report has been read", () => {
    expect(renderToStaticMarkup(<ReportCards report={{ kind: "loading" }} draw={() => null} />)).toBe("");
  });
});
