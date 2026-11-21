import i18next, { type TFunction } from "i18next";
import ICU from "i18next-icu";
import { initReactI18next, useTranslation } from "react-i18next";
import { DEFAULT_LANGUAGE, resources, type Namespace } from "./resources";

/*
 * The app's words. Messages are ICU MessageFormat ("{count, plural, one {# task} other {# tasks}}").
 *
 *   useT("workspace")       in a view or a controller.
 *   translator("common")    in a plain function: an error's words, a view model.
 *
 * The system administrator's pages are English only and write their words in place.
 */
export const i18n = i18next.createInstance();

void i18n
  .use(ICU)
  .use(initReactI18next)
  .init({
    resources,
    lng: DEFAULT_LANGUAGE,
    fallbackLng: DEFAULT_LANGUAGE,
    ns: Object.keys(resources[DEFAULT_LANGUAGE]),
    defaultNS: "common",
    // The files are in the bundle, so the first render already has its words.
    initAsync: false,
    interpolation: { escapeValue: false },
    returnNull: false,
    returnEmptyString: false,
    react: { useSuspense: false },
  });

export type T<N extends Namespace = Namespace> = TFunction<N>;

/** A namespace's words in a view or a controller. */
export function useT<N extends Namespace>(namespace: N): T<N> {
  return useTranslation(namespace).t as unknown as T<N>;
}

/** A namespace's words outside React. */
export function translator<N extends Namespace>(namespace: N): T<N> {
  return i18n.getFixedT(null, namespace) as unknown as T<N>;
}

export type { Namespace } from "./resources";
