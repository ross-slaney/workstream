import "i18next";
import type { resources } from "./resources";

// Every key is typed from the English files, so a key with no words fails the type check.
declare module "i18next" {
  interface CustomTypeOptions {
    defaultNS: "common";
    resources: (typeof resources)["en"];
  }
}
