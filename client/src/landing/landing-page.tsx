import type { ReactNode } from "react";
import { useT } from "@/lib/i18n";

/*
 * The website: one page, in semantic HTML so it reads as a document to a search engine and a screen reader.
 * Props in, links out; the route decides who sees it (app/index.tsx). Its words are locales/landing.
 */
export type LandingPageProps = {
  signInHref: string;
  startHref: string;
};

const primaryLink =
  "inline-flex h-11 items-center justify-center rounded-md bg-primary px-5 text-sm font-semibold text-primary-foreground outline-none hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background";
const quietLink =
  "inline-flex h-11 items-center justify-center rounded-md px-4 text-sm font-medium text-foreground outline-none hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring";

export function LandingPage({ signInHref, startHref }: LandingPageProps) {
  const t = useT("landing");
  const brand = useT("common")("brand.name");
  return (
    <div className="h-full overflow-y-auto bg-background text-foreground">
      <header className="mx-auto flex h-16 w-full max-w-[1120px] items-center justify-between px-5">
        <a href="/" className="flex items-center gap-2 text-[15px] font-semibold tracking-tight">
          <Mark />
          {brand}
        </a>
        <nav aria-label={brand} className="flex items-center gap-1">
          <a href={signInHref} className={quietLink}>
            {t("nav.signIn")}
          </a>
          <a href={startHref} className={primaryLink}>
            {t("nav.start")}
          </a>
        </nav>
      </header>

      <main>
        <section className="mx-auto w-full max-w-[1120px] px-5 pb-16 pt-12 md:pt-20">
          <div className="max-w-[760px]">
            <h1 className="text-balance text-[40px] font-semibold leading-[1.08] tracking-tight md:text-[60px]">{t("hero.title")}</h1>
            <p className="mt-5 max-w-[640px] text-pretty text-[17px] leading-relaxed text-muted-foreground md:text-[19px]">{t("hero.body")}</p>
            <div className="mt-8 flex flex-wrap items-center gap-2">
              <a href={startHref} className={primaryLink}>
                {t("hero.cta")}
              </a>
              <a href="#how" className={quietLink}>
                {t("hero.secondary")}
              </a>
            </div>
          </div>
          <figure className="mt-12 overflow-hidden rounded-xl border border-border bg-canvas shadow-[0_24px_60px_-30px_rgba(15,23,42,0.35)]">
            <img src="/images/workspace.webp" alt={t("hero.pictureAlt")} width={2400} height={1500} className="block h-auto w-full" />
          </figure>
        </section>

        <section id="how" className="border-y border-border bg-canvas">
          <div className="mx-auto w-full max-w-[1120px] px-5 py-16">
            <h2 className="text-[28px] font-semibold tracking-tight">{t("how.title")}</h2>
            <ol className="mt-8 grid gap-8 md:grid-cols-3">
              {(["ask", "watch", "takeOver"] as const).map((step, index) => (
                <li key={step} className="flex flex-col gap-2">
                  <span className="flex size-8 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground">{index + 1}</span>
                  <h3 className="mt-2 text-[17px] font-semibold">{t(`how.steps.${step}.title`)}</h3>
                  <p className="text-[15px] leading-relaxed text-muted-foreground">{t(`how.steps.${step}.body`)}</p>
                </li>
              ))}
            </ol>
          </div>
        </section>

        <Audience eyebrow={t("solo.eyebrow")} title={t("solo.title")}>
          {(["parallel", "free", "ready"] as const).map((point) => (
            <Point key={point} title={t(`solo.points.${point}.title`)} body={t(`solo.points.${point}.body`)} />
          ))}
        </Audience>

        <Audience eyebrow={t("teams.eyebrow")} title={t("teams.title")} tinted>
          {(["shared", "visible", "control"] as const).map((point) => (
            <Point key={point} title={t(`teams.points.${point}.title`)} body={t(`teams.points.${point}.body`)} />
          ))}
        </Audience>

        <section className="mx-auto w-full max-w-[1120px] px-5 py-20 text-center">
          <h2 className="text-[32px] font-semibold tracking-tight">{t("closing.title")}</h2>
          <p className="mx-auto mt-3 max-w-[520px] text-[17px] text-muted-foreground">{t("closing.body")}</p>
          <a href={startHref} className={`${primaryLink} mt-7`}>
            {t("closing.cta")}
          </a>
        </section>
      </main>

      <footer className="border-t border-border">
        <div className="mx-auto flex w-full max-w-[1120px] items-center justify-between px-5 py-6 text-[13px] text-muted-foreground">
          <span>
            © {new Date().getFullYear()} {t("footer.rights")}
          </span>
          <a href={signInHref} className="hover:text-foreground">
            {t("nav.signIn")}
          </a>
        </div>
      </footer>
    </div>
  );
}

function Audience({ eyebrow, title, tinted, children }: { eyebrow: string; title: string; tinted?: boolean; children: ReactNode }) {
  return (
    <section className={tinted ? "border-y border-border bg-canvas" : undefined}>
      <div className="mx-auto w-full max-w-[1120px] px-5 py-16">
        <p className="text-sm font-semibold text-primary">{eyebrow}</p>
        <h2 className="mt-2 max-w-[720px] text-balance text-[28px] font-semibold tracking-tight md:text-[34px]">{title}</h2>
        <div className="mt-10 grid gap-x-10 gap-y-8 md:grid-cols-3">{children}</div>
      </div>
    </section>
  );
}

function Point({ title, body }: { title: string; body: string }) {
  return (
    <div className="flex flex-col gap-2 border-t border-border pt-5">
      <h3 className="text-[17px] font-semibold">{title}</h3>
      <p className="text-[15px] leading-relaxed text-muted-foreground">{body}</p>
    </div>
  );
}

/** The mark: three screens side by side, the product in one glance. */
export function Mark({ size = 22 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true">
      <rect x="2" y="4" width="9" height="16" rx="2" className="fill-primary" />
      <rect x="13" y="4" width="9" height="7" rx="2" className="fill-primary" opacity="0.6" />
      <rect x="13" y="13" width="9" height="7" rx="2" className="fill-primary" opacity="0.35" />
    </svg>
  );
}
