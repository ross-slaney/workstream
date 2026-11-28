import { describe, expect, it } from "vitest";
import { gbMonthsShown, gbShown, hoursShown } from "../figures";

describe("what the Usage page shows", () => {
  it("rounds hours and gigabytes to a tenth", () => {
    expect(hoursShown(13.64)).toBe(13.6);
    expect(hoursShown(0.04)).toBe(0);
    expect(gbShown(64)).toBe(64);
    expect(gbShown(12.537)).toBe(12.5);
  });

  it("keeps a hundredth of a GB-month while there is less than one, so a few minutes of a disk is not nothing", () => {
    expect(gbMonthsShown(0.022)).toBe(0.02);
    expect(gbMonthsShown(0.996)).toBe(1);
    expect(gbMonthsShown(4.62)).toBe(4.6);
    expect(gbMonthsShown(41.26)).toBe(41.3);
  });
});

import { barShares, dollarsShown, modelShown, tokensShown } from "../figures";

describe("what the AI use cards show", () => {
  it("says an amount under a cent is under a cent, and nothing is nothing", () => {
    expect(dollarsShown(0)).toBe("$0.00");
    expect(dollarsShown(0.004)).toBe("<$0.01");
    expect(dollarsShown(2.314)).toBe("$2.31");
    expect(dollarsShown(1234.5)).toBe("$1,234.50");
  });

  it("writes tokens the short way", () => {
    expect(tokensShown(950)).toBe("950");
    expect(tokensShown(340_000)).toBe("340K");
    expect(tokensShown(1_240_000)).toBe("1.2M");
  });

  it("says a model's name as a person does", () => {
    expect(modelShown("claude-opus-5-5")).toBe("Claude Opus 5.5");
    expect(modelShown("claude-haiku-5")).toBe("Claude Haiku 5");
    expect(modelShown("my-deployment")).toBe("My Deployment");
  });

  it("draws each day against the tallest, and a day with any use tall enough to see", () => {
    expect(barShares([0, 5, 10])).toEqual([0, 0.5, 1]);
    expect(barShares([0.001, 10])).toEqual([0.04, 1]);
    expect(barShares([0, 0])).toEqual([0, 0]);
    expect(barShares([])).toEqual([]);
  });
});
