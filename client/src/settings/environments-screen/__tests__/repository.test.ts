import { describe, expect, it } from "vitest";
import { sameRepository } from "../repository";

describe("a repository picked from GitHub and one typed", () => {
  it("are the same however the address ends", () => {
    expect(sameRepository("https://github.com/acme/app", "https://github.com/acme/app")).toBe(true);
    expect(sameRepository("https://github.com/acme/app", "https://github.com/Acme/app.git")).toBe(true);
    expect(sameRepository("https://github.com/acme/app", " https://github.com/acme/app/ ")).toBe(true);
  });

  it("are different repositories, and nothing typed is none of them", () => {
    expect(sameRepository("https://github.com/acme/app", "https://github.com/acme/site")).toBe(false);
    expect(sameRepository("https://github.com/acme/app", "")).toBe(false);
    expect(sameRepository("", "")).toBe(false);
  });
});
