import { useT } from "@/lib/i18n";
import { FEATURES, FEATURE_PICTURES, featuresHref, type FeatureSlug } from "./features";
import { FeatureList, FileExample, Point, Shot, SiteFooter, SiteHeader, primaryLink, quietLink, type LandingPageProps } from "./landing-page";

/*
 * A feature's own page (/features/<slug>): what it is and why a team would use it, the screen that shows
 * it, what it gives in four points, and how to use it in four steps. It is written for someone deciding
 * whether to use Workstream and for a teammate who wants to remember how. Semantic HTML, props in, links
 * out; its words are locales/landing under features.list.<slug>.
 */

const page = "mx-auto w-full max-w-[1120px] px-5";

/** Each feature's four points, by the keys its words are under. */
const POINTS: Record<FeatureSlug, readonly string[]> = {
  "open-desktop": ["ready", "github", "ai", "pause"],
  tasks: ["watch", "takeOver", "parallel", "done"],
  ai: ["agent", "opencode", "keys", "counted"],
  environments: ["surfaces", "apps", "setup", "variables"],
  usage: ["people", "tools", "tasks", "private"],
};

const STEPS = ["one", "two", "three", "four"] as const;

export function FeaturePage({ slug, signInHref, startHref, docsHref }: LandingPageProps & { slug: FeatureSlug }) {
  const t = useT("landing");
  // The keys are built from the slug; every feature has the same ones (locales/landing).
  const words = (key: string) => t(`features.list.${slug}.${key}` as "features.list.tasks.name");
  return (
    <div className="h-full overflow-y-auto bg-background text-foreground">
      <SiteHeader signInHref={signInHref} startHref={startHref} docsHref={docsHref} />
      <main>
        <section className={`${page} pb-16 pt-10 md:pt-16`}>
          <a href={featuresHref} className="text-sm font-semibold text-primary hover:underline">
            {t("features.all")}
          </a>
          <div className="mt-4 max-w-[820px]">
            <h1 className="text-balance text-[34px] font-semibold leading-[1.12] tracking-tight md:text-[48px]">{words("title")}</h1>
            <p className="mt-5 max-w-[700px] text-pretty text-[17px] leading-relaxed text-muted-foreground md:text-[19px]">{words("lead")}</p>
            <div className="mt-8 flex flex-wrap items-center gap-2">
              <a href={startHref} className={primaryLink}>
                {t("hero.cta")}
              </a>
              <a href={words("docsHref")} className={quietLink}>
                {t("features.docs")}
              </a>
            </div>
          </div>
          <Shot picture={FEATURE_PICTURES[slug]} alt={words("pictureAlt")} className="mt-12" eager />
        </section>

        <section className="border-y border-border bg-canvas">
          <div className={`${page} grid gap-x-10 gap-y-8 py-16 md:grid-cols-2`}>
            {POINTS[slug].map((point) => (
              <Point key={point} title={words(`points.${point}.title`)} body={words(`points.${point}.body`)} />
            ))}
          </div>
        </section>

        <section className={`${page} py-16`}>
          <h2 className="text-[28px] font-semibold tracking-tight">{t("features.how")}</h2>
          <ol className="mt-8 grid gap-6 md:grid-cols-2">
            {STEPS.map((step, index) => (
              <li key={step} className="flex items-start gap-4">
                <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground">{index + 1}</span>
                <p className="pt-1 text-[16px] leading-relaxed text-foreground">{words(`steps.${step}`)}</p>
              </li>
            ))}
          </ol>
          {slug === "environments" ? <FileExample name={t("start.fileName")} text={t("start.fileExample")} caption={t("start.fileCaption")} /> : null}
        </section>

        <section className="border-t border-border bg-canvas">
          <div className={`${page} py-16`}>
            <h2 className="text-[28px] font-semibold tracking-tight">{t("features.title")}</h2>
            <FeatureList className="mt-8" except={slug} />
          </div>
        </section>
      </main>
      <SiteFooter signInHref={signInHref} docsHref={docsHref} />
    </div>
  );
}

/** /features: every feature with its picture, for someone who wants the whole list on one page. */
export function FeaturesPage({ signInHref, startHref, docsHref }: LandingPageProps) {
  const t = useT("landing");
  return (
    <div className="h-full overflow-y-auto bg-background text-foreground">
      <SiteHeader signInHref={signInHref} startHref={startHref} docsHref={docsHref} />
      <main>
        <section className={`${page} pb-6 pt-10 md:pt-16`}>
          <h1 className="text-balance text-[34px] font-semibold leading-[1.12] tracking-tight md:text-[48px]">{t("features.title")}</h1>
          <p className="mt-5 max-w-[700px] text-pretty text-[17px] leading-relaxed text-muted-foreground md:text-[19px]">{t("hero.body")}</p>
        </section>
        {FEATURES.map((slug, index) => (
          <section key={slug} className={index % 2 === 1 ? "border-y border-border bg-canvas" : undefined}>
            <div className={`${page} grid items-center gap-10 py-14 md:grid-cols-[minmax(0,5fr)_minmax(0,7fr)]`}>
              <div>
                <h2 className="text-[26px] font-semibold tracking-tight">{t(`features.list.${slug}.name`)}</h2>
                <p className="mt-3 text-[16px] leading-relaxed text-muted-foreground">{t(`features.list.${slug}.lead`)}</p>
                <a href={`/features/${slug}`} className="mt-5 inline-block text-sm font-semibold text-primary hover:underline">
                  {t("features.more")}
                </a>
              </div>
              <Shot picture={FEATURE_PICTURES[slug]} alt={t(`features.list.${slug}.pictureAlt`)} />
            </div>
          </section>
        ))}
      </main>
      <SiteFooter signInHref={signInHref} docsHref={docsHref} />
    </div>
  );
}
