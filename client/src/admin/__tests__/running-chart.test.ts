import { describe, expect, it } from "vitest";
import { hourMarks, ticksFor } from "../running-chart";
import { since } from "../format";

describe("the running chart's axes", () => {
  it("counts in whole numbers from zero, four steps at most", () => {
    expect(ticksFor(0)).toEqual([0, 1]);
    expect(ticksFor(1)).toEqual([0, 1]);
    expect(ticksFor(3)).toEqual([0, 1, 2, 3]);
    expect(ticksFor(4)).toEqual([0, 1, 2, 3, 4]);
    expect(ticksFor(9)).toEqual([0, 3, 6, 9]);
    expect(ticksFor(10)).toEqual([0, 3, 6, 9, 12]);
    expect(ticksFor(250)).toEqual([0, 63, 126, 189, 252]);
  });

  it("marks the day at six-hour steps of the clock, whatever minute the readings fall on", () => {
    const from = new Date(2026, 10, 19, 14, 7).getTime();
    const marks = hourMarks(from, from + 24 * 3_600_000);
    expect(marks.map((mark) => mark.getHours())).toEqual([18, 0, 6, 12]);
    expect(marks.every((mark) => mark.getMinutes() === 0)).toBe(true);
  });
});

describe("how long a desktop has been running", () => {
  const now = new Date("2026-11-20T12:00:00Z").getTime();
  it("says it in the two largest units", () => {
    expect(since("2026-11-20T11:59:40Z", now)).toBe("just now");
    expect(since("2026-11-20T11:52:00Z", now)).toBe("8 min");
    expect(since("2026-11-20T09:55:00Z", now)).toBe("2 h 5 min");
    expect(since("2026-11-18T09:00:00Z", now)).toBe("2 d 3 h");
  });
});
