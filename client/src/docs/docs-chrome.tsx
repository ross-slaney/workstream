import type { DocsNavSection } from "@agenetix/docs/source";
import type { ReactNode } from "react";

import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { Mark, SiteFooter, primaryLink, quietLink } from "@/src/landing/landing-page";

/*
 * The frame every docs page shares: the website's header with "Docs" beside the name, the list of pages, the
 * article, the page's own headings on the right, and the website's footer. Server-rendered with plain
 * anchors, so a search engine reads it and every link works before any script runs. On a phone the list of
 * pages folds into a <details> under the header, which needs no script either.
 */

export type DocsLinks = { home: string; docs: string; signIn: string; start: string };

export type DocsShellProps = {
  links: DocsLinks;
  navigation: DocsNavSection[];
  currentHref: string;
  /** The page's name, shown on a phone beside the control that opens the list of pages. */
  currentTitle: string;
  rail?: ReactNode;
  children: ReactNode;
};

export function DocsShell({ links, navigation, currentHref, currentTitle, rail, children }: DocsShellProps) {
  return (
    <div className="h-full overflow-y-auto scroll-smooth bg-background text-foreground">
      <DocsHeader links={links} />
      <PagesBar links={links} navigation={navigation} currentHref={currentHref} currentTitle={currentTitle} />
      <main className="mx-auto flex w-full max-w-[1120px] flex-col px-5 lg:flex-row lg:items-start lg:gap-12">
        <aside className="sticky top-0 hidden max-h-screen w-[220px] shrink-0 overflow-y-auto py-10 lg:block">
          <PagesNav links={links} navigation={navigation} currentHref={currentHref} />
        </aside>
        <div className="min-w-0 flex-1 pb-16 pt-8 lg:pt-10">{children}</div>
        {rail ? <aside className="sticky top-0 hidden w-[200px] shrink-0 py-10 xl:block">{rail}</aside> : null}
      </main>
      <SiteFooter signInHref={links.signIn} docsHref={links.docs} />
    </div>
  );
}

/* ---------- Header ---------- */

function DocsHeader({ links }: { links: DocsLinks }) {
  const t = useT("docs");
  const site = useT("landing");
  const brand = useT("common")("brand.name");
  return (
    <header className="border-b border-border">
      <div className="mx-auto flex h-16 w-full max-w-[1120px] items-center justify-between gap-3 px-5">
        <div className="flex min-w-0 items-center gap-3">
          <a href={links.home} aria-label={t("header.home")} className="flex items-center gap-2 text-[15px] font-semibold tracking-tight">
            <Mark />
            {brand}
          </a>
          <span aria-hidden="true" className="h-5 w-px bg-border" />
          <a href={links.docs} className="text-[15px] font-medium text-muted-foreground hover:text-foreground">
            {t("docs")}
          </a>
        </div>
        <nav aria-label={t("header.site")} className="flex items-center gap-1">
          <a href={links.signIn} className={cn(quietLink, "hidden md:inline-flex")}>
            {site("nav.signIn")}
          </a>
          <a href={links.start} className={cn(primaryLink, "h-10 px-4")}>
            {site("nav.start")}
          </a>
        </nav>
      </div>
    </header>
  );
}

/* ---------- The list of pages, folded on a phone ---------- */

function PagesBar({ links, navigation, currentHref, currentTitle }: Pick<DocsShellProps, "links" | "navigation" | "currentHref" | "currentTitle">) {
  const t = useT("docs");
  return (
    <details className="group border-b border-border bg-canvas lg:hidden">
      <summary className="flex h-12 cursor-pointer list-none items-center justify-between gap-4 px-5 [&::-webkit-details-marker]:hidden">
        <span className="min-w-0 truncate text-sm font-medium text-foreground">{currentTitle}</span>
        <span className="flex shrink-0 items-center gap-1 text-[13px] font-medium text-muted-foreground">
          {t("pagesToggle")}
          <Chevron className="transition-transform group-open:rotate-180" />
        </span>
      </summary>
      <div className="border-t border-border bg-background px-2 py-4">
        <PagesNav links={links} navigation={navigation} currentHref={currentHref} />
      </div>
    </details>
  );
}

export function PagesNav({ links, navigation, currentHref }: Pick<DocsShellProps, "links" | "navigation" | "currentHref">) {
  const t = useT("docs");
  return (
    <nav aria-label={t("navLabel")} className="flex flex-col gap-6">
      {navigation.map((section, index) => (
        <div key={section.key} className="flex flex-col gap-0.5">
          <p className="m-0 px-3 pb-2 text-[13px] font-semibold text-foreground">{section.label}</p>
          <ol className="m-0 flex list-none flex-col gap-0.5 p-0">
            {index === 0 ? <NavLink href={links.docs} title={t("overview")} active={currentHref === links.docs} /> : null}
            {section.items.map((item) => (
              <NavLink key={item.href} href={item.href} title={item.title} active={item.href === currentHref} />
            ))}
          </ol>
        </div>
      ))}
    </nav>
  );
}

function NavLink({ href, title, active }: { href: string; title: string; active: boolean }) {
  return (
    <li>
      <a
        href={href}
        aria-current={active ? "page" : undefined}
        className={cn(
          "flex min-h-9 items-center rounded-md px-3 py-1.5 text-sm leading-5 outline-none focus-visible:ring-2 focus-visible:ring-ring",
          active ? "bg-primary-soft font-semibold text-primary" : "text-muted-foreground hover:bg-muted hover:text-foreground",
        )}
      >
        {title}
      </a>
    </li>
  );
}

export function Chevron({ className }: { className?: string }) {
  return (
    <svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true" className={className}>
      <path d="M2.5 4.5L6 8l3.5-3.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
