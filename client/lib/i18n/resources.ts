import auth from "@/locales/auth/en.json";
import common from "@/locales/common/en.json";
import docs from "@/locales/docs/en.json";
import landing from "@/locales/landing/en.json";
import settings from "@/locales/settings/en.json";
import workspace from "@/locales/workspace/en.json";

/*
 * Every word a person reads is a key in locales/{namespace}/{language}.json. The files are imported by name
 * because the bundler cannot compute a path. The docs pages' own text is content/docs/<page>/en.mdx; the docs
 * namespace is the frame around it. English is the only language today; another language is a
 * folder of the same files and one more entry here.
 */
export const resources = {
  en: { auth, common, docs, landing, settings, workspace },
} as const;

export type Language = keyof typeof resources;
export type Namespace = keyof (typeof resources)["en"];
export const DEFAULT_LANGUAGE: Language = "en";
