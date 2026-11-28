import { existsSync } from "node:fs";
import path from "node:path";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { FeaturePage, FeaturesPage } from "../feature-page";
import { FEATURES, FEATURE_PICTURES, PICTURES, isFeature } from "../features";
import { LandingPage } from "../landing-page";

/* The website: the front page and a page for each feature. */

const links = { signInHref: "/sign-in", startHref: "/sign-in?view=signup", docsHref: "/docs" };

describe("the front page", () => {
  const markup = renderToStaticMarkup(<LandingPage {...links} />);

  it("says what Workstream is in one heading", () => {
    expect(markup.match(/<h1/g)).toHaveLength(1);
    expect(markup).toContain("A cloud dev desktop for you and your agents");
  });

  it("gives the three steps in the order a team does them, with the file as it is written", () => {
    const [file, github, work] = ["Describe the desktop in workstream.yml", "Sign in with GitHub", "Open a desktop or start a task"].map((title) => markup.indexOf(title));
    expect(file).toBeGreaterThan(0);
    expect(github).toBeGreaterThan(file);
    expect(work).toBeGreaterThan(github);
    expect(markup).toContain("surfaces: [chrome, iphone, android]");
  });

  it("links to every feature's page", () => {
    for (const slug of FEATURES) expect(markup).toContain(`href="/features/${slug}"`);
  });

  it("says what each picture shows", () => {
    const pictures = markup.match(/<img[^>]*>/g) ?? [];
    expect(pictures.length).toBeGreaterThanOrEqual(5);
    for (const picture of pictures) expect(picture).toMatch(/alt="[^"]{40,}"/);
  });
});

describe("a feature's page", () => {
  it.each(FEATURES)("%s leads with its name, shows its screen, and says how to use it in four steps", (slug) => {
    const markup = renderToStaticMarkup(<FeaturePage slug={slug} {...links} />);
    expect(markup.match(/<h1/g)).toHaveLength(1);
    expect(markup).toContain(FEATURE_PICTURES[slug].src);
    expect(markup).toContain("How to use it");
    expect(markup.match(/<li class="flex items-start gap-4"/g)).toHaveLength(4);
    // Four points, each with its words: a key with no words would be drawn as the key.
    expect(markup).not.toContain("features.list.");
    // It links to the other four, and not to itself.
    for (const other of FEATURES) {
      if (other === slug) expect(markup).not.toContain(`href="/features/${other}"`);
      else expect(markup).toContain(`href="/features/${other}"`);
    }
  });

  it("is only at a feature's own address", () => {
    expect(isFeature("usage")).toBe(true);
    expect(isFeature("pricing")).toBe(false);
    expect(isFeature(undefined)).toBe(false);
  });

  it("shows the file on the environments page", () => {
    expect(renderToStaticMarkup(<FeaturePage slug="environments" {...links} />)).toContain("surfaces: [chrome, iphone, android]");
    expect(renderToStaticMarkup(<FeaturePage slug="usage" {...links} />)).not.toContain("surfaces: [chrome, iphone, android]");
  });
});

describe("the page of every feature", () => {
  it("lists each one with its picture and a link to its page", () => {
    const markup = renderToStaticMarkup(<FeaturesPage {...links} />);
    for (const slug of FEATURES) {
      expect(markup).toContain(`href="/features/${slug}"`);
      expect(markup).toContain(FEATURE_PICTURES[slug].src);
    }
  });
});

describe("the website's pictures", () => {
  it("are all files the website serves", () => {
    for (const picture of Object.values(PICTURES)) expect(existsSync(path.join(__dirname, "../../../public", picture.src)), picture.src).toBe(true);
  });
});
