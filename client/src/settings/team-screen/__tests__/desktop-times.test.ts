import { describe, expect, it } from "vitest";
import { desktopTimesOf } from "../desktop-times";

describe("when a team's desktops are paused and archived", () => {
  it("takes whole numbers a team may choose", () => {
    expect(desktopTimesOf("30", "7")).toEqual({ pauseAfterMinutes: 30, archiveAfterDays: 7 });
    expect(desktopTimesOf(" 5 ", "1")).toEqual({ pauseAfterMinutes: 5, archiveAfterDays: 1 });
    expect(desktopTimesOf("240", "90")).toEqual({ pauseAfterMinutes: 240, archiveAfterDays: 90 });
  });

  it("takes nothing outside 5 to 240 minutes and 1 to 90 days", () => {
    expect(desktopTimesOf("4", "7")).toBeNull();
    expect(desktopTimesOf("241", "7")).toBeNull();
    expect(desktopTimesOf("30", "0")).toBeNull();
    expect(desktopTimesOf("30", "91")).toBeNull();
  });

  it("takes nothing that is not a whole number", () => {
    expect(desktopTimesOf("", "7")).toBeNull();
    expect(desktopTimesOf("30", "")).toBeNull();
    expect(desktopTimesOf("30.5", "7")).toBeNull();
    expect(desktopTimesOf("half an hour", "7")).toBeNull();
    expect(desktopTimesOf("-30", "7")).toBeNull();
  });
});
