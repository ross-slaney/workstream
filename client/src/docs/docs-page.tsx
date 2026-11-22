import { RemoteMDX } from "@bacons/mdx";

import type { DocsPageData } from "@/lib/docs/page";
import { useT } from "@/lib/i18n";

import { DocsShell, type DocsLinks } from "./docs-chrome";
import { docsComponents } from "./docs-mdx";

/*
 * A docs page on the website: the docs frame, then the article (title, description, the page's text compiled
 * on the server and rendered here as real HTML, previous and next).
 * Props in, links out; the route reads the page (app/docs).
 */

export type DocsPageProps = {
  links: DocsLinks;
  page: DocsPageData;
};

export function DocsPage({ links, page }: DocsPageProps) {
  const { entry, compiled, navigation, previous, next } = page;
  const t = useT("docs");

  return (
    <DocsShell links={links} navigation={navigation} currentHref={entry.href} currentTitle={entry.isHome ? t("overview") : entry.title}>
      <article className="min-w-0 max-w-[720px]">
        <header className="flex flex-col gap-3 pb-8">
          <h1 className="m-0 text-balance text-[32px] font-semibold leading-[1.12] tracking-tight text-foreground md:text-[40px]">{entry.title}</h1>
          <p className="m-0 text-pretty text-[17px] leading-relaxed text-muted-foreground">{entry.description}</p>
          {entry.updated ? <p className="m-0 text-[13px] leading-5 text-muted-foreground">{entry.updated}</p> : null}
        </header>

        <div className="min-w-0">
          <RemoteMDX source={compiled} components={docsComponents} />
        </div>

        {previous || next ? (
          <nav aria-label={t("pagesNav")} className="mt-12 flex flex-col gap-3 md:flex-row md:justify-between md:gap-4">
            {previous ? <PageLink label={t("previous")} title={previous.title} href={previous.href} /> : <span className="hidden md:block" />}
            {next ? <PageLink label={t("next")} title={next.title} href={next.href} align="right" /> : null}
          </nav>
        ) : null}
      </article>
    </DocsShell>
  );
}

function PageLink({ label, title, href, align = "left" }: { label: string; title: string; href: string; align?: "left" | "right" }) {
  return (
    <a
      href={href}
      className={`flex flex-col gap-1 rounded-lg border border-border px-4 py-3 outline-none hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring md:w-[300px] ${align === "right" ? "md:items-end md:text-right" : ""}`}
    >
      <span className="text-[13px] text-muted-foreground">{label}</span>
      <span className="text-[15px] font-semibold leading-5 text-foreground">{title}</span>
    </a>
  );
}
