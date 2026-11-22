import { describe, expect, it } from "vitest";
import { lastLines } from "../setup-output";

describe("the setup script's last lines", () => {
  it("are the end of what it wrote, without the empty lines it ended on", () => {
    expect(lastLines("one\ntwo\nthree\n\n", 2)).toBe("two\nthree");
  });

  it("are everything when it wrote little, and nothing when it wrote nothing", () => {
    expect(lastLines("only", 40)).toBe("only");
    expect(lastLines(null, 40)).toBe("");
    expect(lastLines("", 40)).toBe("");
  });
});
