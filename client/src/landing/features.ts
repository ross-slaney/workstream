/*
 * The features the website has a page for (/features/<slug>), in the order it lists them, each with the
 * picture that shows it. Their words are locales/landing, under features.list.<slug>. Every picture is a
 * screenshot of the product itself.
 */

export const FEATURES = ["open-desktop", "tasks", "ai", "environments", "usage"] as const;

export type FeatureSlug = (typeof FEATURES)[number];

export const isFeature = (slug: string | undefined): slug is FeatureSlug => (FEATURES as readonly string[]).includes(slug ?? "");

export const featureHref = (slug: FeatureSlug) => `/features/${slug}`;

export const featuresHref = "/features";

export type Picture = { src: string; width: number; height: number };

/** The website's pictures, with the size each was taken at so the page keeps its shape while one loads. */
export const PICTURES = {
  hero: { src: "/images/hero.webp", width: 2880, height: 1474 },
  desktop: { src: "/images/desktop.webp", width: 2880, height: 1474 },
  workspace: { src: "/images/workspace.webp", width: 2400, height: 1500 },
  newTask: { src: "/images/new-task.webp", width: 2400, height: 1500 },
  opencode: { src: "/images/opencode.webp", width: 1738, height: 1306 },
  surfaces: { src: "/images/surfaces.webp", width: 1280, height: 960 },
  environment: { src: "/images/environment.webp", width: 2400, height: 1500 },
  usage: { src: "/images/usage.webp", width: 2400, height: 1500 },
} satisfies Record<string, Picture>;

export const FEATURE_PICTURES: Record<FeatureSlug, Picture> = {
  "open-desktop": PICTURES.desktop,
  tasks: PICTURES.workspace,
  ai: PICTURES.opencode,
  environments: PICTURES.environment,
  usage: PICTURES.usage,
};
