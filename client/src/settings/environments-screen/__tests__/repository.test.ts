import { describe, expect, it } from "vitest";
import type { GitHubRepository } from "@/lib/workstream/api";
import type { EnvironmentDraft } from "../props";
import { changesFileSettings, fillFromFile, gitHubName, matchingRepositories, sameRepository } from "../repository";

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

  it("is named owner/name only when it is on github.com", () => {
    expect(gitHubName("https://github.com/acme/app.git")).toBe("acme/app");
    expect(gitHubName("https://www.github.com/acme/app/")).toBe("acme/app");
    expect(gitHubName("https://gitlab.com/acme/app")).toBeNull();
    expect(gitHubName("")).toBeNull();
  });
});

const repository = (fullName: string): GitHubRepository => ({ fullName, url: `https://github.com/${fullName}`, defaultBranch: "main", private: true, pushedAt: null });

describe("finding a repository by name", () => {
  const listed = ["acme/web-app", "acme/api", "ada/notes", "acme/web-site"].map(repository);

  it("keeps GitHub's order and every word typed", () => {
    expect(matchingRepositories(listed, "").map((x) => x.fullName)).toEqual(["acme/web-app", "acme/api", "ada/notes", "acme/web-site"]);
    expect(matchingRepositories(listed, "WEB").map((x) => x.fullName)).toEqual(["acme/web-app", "acme/web-site"]);
    expect(matchingRepositories(listed, "acme site").map((x) => x.fullName)).toEqual(["acme/web-site"]);
    expect(matchingRepositories(listed, "nothing")).toEqual([]);
    expect(matchingRepositories(listed, "", 2)).toHaveLength(2);
  });
});

const draft: EnvironmentDraft = {
  name: "Web", apps: ["chrome"], size: "small", repositoryUrl: "https://github.com/acme/app", repositoryBranch: "main", setupScript: "", instructions: "",
  variables: [{ name: "API_KEY", value: "", saved: true }], fromFile: false,
};

describe("filling the form from the repository", () => {
  it("takes workstream.yml's settings, keeps the variables' values and follows the file", () => {
    const filled = fillFromFile(draft, { size: "large", apps: ["chrome", "terminal"], setup: "npm ci", variables: ["DATABASE_URL", "API_KEY"], instructions: null });
    expect(filled).toMatchObject({ size: "large", apps: ["chrome", "terminal"], setupScript: "npm ci", instructions: "", fromFile: true, name: "Web" });
    expect(filled.variables).toEqual([
      { name: "DATABASE_URL", value: "", saved: false },
      { name: "API_KEY", value: "", saved: true },
    ]);
  });

  it("knows a change to what the file decides from one that is not", () => {
    expect(changesFileSettings(draft, { ...draft, name: "Other" })).toBe(false);
    expect(changesFileSettings(draft, { ...draft, variables: [] })).toBe(false);
    expect(changesFileSettings(draft, { ...draft, size: "large" })).toBe(true);
    expect(changesFileSettings(draft, { ...draft, apps: ["chrome", "vscode"] })).toBe(true);
    expect(changesFileSettings(draft, { ...draft, setupScript: "make" })).toBe(true);
  });
});
