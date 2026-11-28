import type { ReactNode } from "react";
import { useT } from "@/lib/i18n";
import { FEATURES, PICTURES, featureHref, featuresHref, type FeatureSlug, type Picture } from "./features";

/*
 * The website's front page, in semantic HTML so it reads as a document to a search engine and a screen
 * reader. Props in, links out; the route decides who sees it (app/index.tsx). Its words are locales/landing,
 * and every picture on it is a screenshot of the product.
 *
 * It says one thing in the order a team would do it: describe the desktop in a file, sign in with GitHub,
 * then open a desktop or start a task. The sections after that are what the team gets: two ways to work,
 * AI already connected, the surfaces a product runs on, one setup for everyone, and a free laptop.
 */
export type LandingPageProps = {
  signInHref: string;
  startHref: string;
  docsHref: string;
};

export const primaryLink =
  "inline-flex h-11 items-center justify-center rounded-md bg-primary px-5 text-sm font-semibold text-primary-foreground outline-none hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background";
export const quietLink =
  "inline-flex h-11 items-center justify-center rounded-md px-4 text-sm font-medium text-foreground outline-none hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring";

const page = "mx-auto w-full max-w-[1120px] px-5";

export function LandingPage({ signInHref, startHref, docsHref }: LandingPageProps) {
  const t = useT("landing");
  return (
    <div className="h-full overflow-y-auto bg-background text-foreground">
      <SiteHeader signInHref={signInHref} startHref={startHref} docsHref={docsHref} />

      <main>
        <section className={`${page} pb-16 pt-12 md:pt-20`}>
          <div className="max-w-[820px]">
            <h1 className="text-balance text-[40px] font-semibold leading-[1.08] tracking-tight md:text-[60px]">{t("hero.title")}</h1>
            <p className="mt-5 max-w-[680px] text-pretty text-[17px] leading-relaxed text-muted-foreground md:text-[19px]">{t("hero.body")}</p>
            <div className="mt-8 flex flex-wrap items-center gap-2">
              <a href={startHref} className={primaryLink}>
                {t("hero.cta")}
              </a>
              <a href="#how" className={quietLink}>
                {t("hero.secondary")}
              </a>
            </div>
          </div>
          <Shot picture={PICTURES.hero} alt={t("hero.pictureAlt")} className="mt-12" eager />
        </section>

        <section id="how" className="border-y border-border bg-canvas">
          <div className={`${page} py-16`}>
            <h2 className="text-[28px] font-semibold tracking-tight md:text-[34px]">{t("start.title")}</h2>
            <ol className="mt-8 grid gap-8 md:grid-cols-3">
              {(["file", "github", "work"] as const).map((step, index) => (
                <li key={step} className="flex flex-col gap-2">
                  <span className="flex size-8 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground">{index + 1}</span>
                  <h3 className="mt-2 text-[17px] font-semibold">{t(`start.steps.${step}.title`)}</h3>
                  <p className="text-[15px] leading-relaxed text-muted-foreground">{t(`start.steps.${step}.body`)}</p>
                </li>
              ))}
            </ol>
            <FileExample name={t("start.fileName")} text={t("start.fileExample")} caption={t("start.fileCaption")} />
          </div>
        </section>

        <Block eyebrow={t("ways.eyebrow")} title={t("ways.title")} picture={<Shot picture={PICTURES.newTask} alt={t("ways.pictureAlt")} />}>
          {(["open", "task", "both"] as const).map((point) => (
            <Point key={point} title={t(`ways.points.${point}.title`)} body={t(`ways.points.${point}.body`)} />
          ))}
        </Block>

        <Block eyebrow={t("ai.eyebrow")} title={t("ai.title")} body={t("ai.body")} tinted picture={<Shot picture={PICTURES.opencode} alt={t("ai.pictureAlt")} />}>
          {(["agent", "opencode", "counted"] as const).map((point) => (
            <Point key={point} title={t(`ai.points.${point}.title`)} body={t(`ai.points.${point}.body`)} />
          ))}
        </Block>

        <Block eyebrow={t("surfaces.eyebrow")} title={t("surfaces.title")} body={t("surfaces.body")} picture={<Shot picture={PICTURES.surfaces} alt={t("surfaces.pictureAlt")} caption={t("surfaces.pictureCaption")} />} />

        <Block eyebrow={t("team.eyebrow")} title={t("team.title")} tinted picture={<Shot picture={PICTURES.usage} alt={t("team.pictureAlt")} />}>
          {(["file", "versions", "usage"] as const).map((point) => (
            <Point key={point} title={t(`team.points.${point}.title`)} body={t(`team.points.${point}.body`)} />
          ))}
        </Block>

        <Block eyebrow={t("free.eyebrow")} title={t("free.title")}>
          {(["install", "parallel", "pause"] as const).map((point) => (
            <Point key={point} title={t(`free.points.${point}.title`)} body={t(`free.points.${point}.body`)} />
          ))}
        </Block>

        <section id="features" className="border-y border-border bg-canvas">
          <div className={`${page} py-16`}>
            <h2 className="text-[28px] font-semibold tracking-tight md:text-[34px]">{t("features.title")}</h2>
            <FeatureList className="mt-8" />
          </div>
        </section>

        <section className={`${page} py-20 text-center`}>
          <h2 className="text-[32px] font-semibold tracking-tight">{t("closing.title")}</h2>
          <p className="mx-auto mt-3 max-w-[520px] text-[17px] text-muted-foreground">{t("closing.body")}</p>
          <a href={startHref} className={`${primaryLink} mt-7`}>
            {t("closing.cta")}
          </a>
        </section>
      </main>

      <SiteFooter signInHref={signInHref} docsHref={docsHref} />
    </div>
  );
}

/** The website's header, over the front page and every feature page. */
export function SiteHeader({ signInHref, startHref, docsHref }: LandingPageProps) {
  const t = useT("landing");
  const brand = useT("common")("brand.name");
  return (
    <header className={`${page} flex h-16 items-center justify-between`}>
      <a href="/" className="flex items-center gap-2 text-[15px] font-semibold tracking-tight">
        <Mark />
        {brand}
      </a>
      <nav aria-label={brand} className="flex items-center gap-1">
        {/* A phone's header has room for two links, so there the features and the docs are reached from the footer. */}
        <a href={featuresHref} className={`${quietLink} max-md:hidden`}>
          {t("nav.features")}
        </a>
        <a href={docsHref} className={`${quietLink} max-md:hidden`}>
          {t("nav.docs")}
        </a>
        <a href={signInHref} className={quietLink}>
          {t("nav.signIn")}
        </a>
        <a href={startHref} className={primaryLink}>
          {t("nav.start")}
        </a>
      </nav>
    </header>
  );
}

/** The website's footer, under the front page, every feature page and every docs page. */
export function SiteFooter({ signInHref, docsHref }: { signInHref: string; docsHref: string }) {
  const t = useT("landing");
  return (
    <footer className="border-t border-border">
      <div className={`${page} flex items-center justify-between py-6 text-[13px] text-muted-foreground`}>
        <span>
          © {new Date().getFullYear()} {t("footer.rights")}
        </span>
        <nav aria-label={t("footer.rights")} className="flex items-center gap-5">
          <a href={featuresHref} className="hover:text-foreground">
            {t("nav.features")}
          </a>
          <a href={docsHref} className="hover:text-foreground">
            {t("nav.docs")}
          </a>
          <a href={signInHref} className="hover:text-foreground">
            {t("nav.signIn")}
          </a>
        </nav>
      </div>
    </footer>
  );
}

/** The features, each a link to its page: its name, a sentence, and where to read more. */
export function FeatureList({ className, except }: { className?: string; except?: FeatureSlug }) {
  const t = useT("landing");
  return (
    <ul className={`grid gap-4 md:grid-cols-2 ${except ? "lg:grid-cols-4" : "lg:grid-cols-3"} ${className ?? ""}`}>
      {FEATURES.filter((slug) => slug !== except).map((slug) => (
        <li key={slug}>
          <a href={featureHref(slug)} className="flex h-full flex-col gap-2 rounded-lg border border-border bg-card p-5 outline-none hover:border-primary focus-visible:ring-2 focus-visible:ring-ring">
            <h3 className="text-[17px] font-semibold">{t(`features.list.${slug}.name`)}</h3>
            <p className="text-[15px] leading-relaxed text-muted-foreground">{t(`features.list.${slug}.summary`)}</p>
            <span className="mt-auto pt-2 text-sm font-semibold text-primary">{t("features.more")}</span>
          </a>
        </li>
      ))}
    </ul>
  );
}

/** A screenshot of the product in a frame, with what it shows said for someone who cannot see it. */
export function Shot({ picture, alt, caption, className, eager }: { picture: Picture; alt: string; caption?: string; className?: string; eager?: boolean }) {
  return (
    <figure className={className}>
      <div className="overflow-hidden rounded-xl border border-border bg-canvas shadow-[0_24px_60px_-30px_rgba(15,23,42,0.35)]">
        <img src={picture.src} alt={alt} width={picture.width} height={picture.height} loading={eager ? "eager" : "lazy"} className="block h-auto w-full" />
      </div>
      {caption ? <figcaption className="mt-3 text-center text-[13px] text-muted-foreground">{caption}</figcaption> : null}
    </figure>
  );
}

/** A file as it is written, under its name. */
export function FileExample({ name, text, caption }: { name: string; text: string; caption?: string }) {
  return (
    <figure className="mt-10 max-w-[640px]">
      <div className="overflow-hidden rounded-lg border border-border bg-card">
        <div className="border-b border-border px-4 py-2 font-mono text-[12.5px] text-muted-foreground">{name}</div>
        <pre className="overflow-x-auto p-4 font-mono text-[13px] leading-6 text-foreground">
          <code>{text}</code>
        </pre>
      </div>
      {caption ? <figcaption className="mt-3 text-[13px] text-muted-foreground">{caption}</figcaption> : null}
    </figure>
  );
}

/** One thing the page says: who it is for, the claim, its points side by side, and the screen that shows it. */
function Block({ eyebrow, title, body, tinted, picture, children }: { eyebrow: string; title: string; body?: string; tinted?: boolean; picture?: ReactNode; children?: ReactNode }) {
  return (
    <section className={tinted ? "border-y border-border bg-canvas" : undefined}>
      <div className={`${page} py-16`}>
        <p className="text-sm font-semibold text-primary">{eyebrow}</p>
        <h2 className="mt-2 max-w-[760px] text-balance text-[28px] font-semibold tracking-tight md:text-[34px]">{title}</h2>
        {body ? <p className="mt-4 max-w-[720px] text-pretty text-[17px] leading-relaxed text-muted-foreground">{body}</p> : null}
        {children ? <div className="mt-10 grid gap-x-10 gap-y-8 md:grid-cols-3">{children}</div> : null}
        {picture ? <div className="mt-12">{picture}</div> : null}
      </div>
    </section>
  );
}

export function Point({ title, body }: { title: string; body: string }) {
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
