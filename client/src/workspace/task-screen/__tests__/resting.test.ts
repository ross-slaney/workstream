import { describe, expect, it } from "vitest";
import { chatNoteOf, restingOf } from "../resting";

const team = { prompt: true, drive: true, manage: true };
const driver = { prompt: true, drive: true, manage: false };
const prompter = { prompt: true, drive: false, manage: false };
const watcher = { prompt: false, drive: false, manage: false };

describe("a desktop that is paused or archived", () => {
  it("comes back at a click for whoever may use it", () => {
    expect(restingOf("stopped", team)).toEqual({ kind: "paused", resume: "click" });
    expect(restingOf("stopped", driver)).toEqual({ kind: "paused", resume: "click" });
    expect(restingOf("archived", team)).toEqual({ kind: "archived", resume: "click" });
  });

  it("comes back at a message for someone who may only write to its agent", () => {
    expect(restingOf("stopped", prompter)).toEqual({ kind: "paused", resume: "message" });
    expect(restingOf("archived", prompter)).toEqual({ kind: "archived", resume: "message" });
  });

  it("is not brought back by someone who only watches, or before anyone knows what they may do", () => {
    expect(restingOf("stopped", watcher)).toEqual({ kind: "paused", resume: "none" });
    expect(restingOf("archived", null)).toEqual({ kind: "archived", resume: "none" });
  });

  it("is no other state: a running, pausing, archiving or failed desktop is drawn its own way", () => {
    for (const state of ["starting", "ready", "stopping", "archiving", "failed", "deleting"] as const) {
      expect(restingOf(state, team)).toBeNull();
    }
  });
});

describe("the line above the message box", () => {
  it("says nothing while the agent is there to read", () => {
    expect(chatNoteOf("ready", "agent")).toBeNull();
    expect(chatNoteOf("deleting", "agent")).toBeNull();
  });

  it("says what a message does to a desktop that is not running", () => {
    expect(chatNoteOf("starting", "machine")).toBe("waitingForDesktop");
    expect(chatNoteOf("starting", "agent")).toBe("agentStarting");
    expect(chatNoteOf("stopping", "agent")).toBe("desktopPausing");
    expect(chatNoteOf("stopped", "agent")).toBe("desktopPaused");
    expect(chatNoteOf("archiving", "agent")).toBe("desktopArchiving");
    expect(chatNoteOf("archived", "agent")).toBe("desktopArchived");
    expect(chatNoteOf("failed", "agent")).toBe("desktopFailed");
  });
});
