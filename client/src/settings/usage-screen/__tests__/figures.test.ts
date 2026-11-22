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
