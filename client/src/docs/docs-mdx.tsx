import { getDOMComponents } from "@bacons/mdx";
import type { ComponentType, ReactNode } from "react";

/*
 * What a docs page may be written with. The plain elements (headings, paragraphs, lists, code, tables) are
 * HTML with the website's classes; the named components are the one shape the pages need beyond text: a row of
 * stages with arrows between them. A page uses them as JSX in its MDX; nothing here knows which
 * page it is on.
 */

type AnyProps = Record<string, unknown> & { children?: ReactNode };

const dom = getDOMComponents() as Record<string, ComponentType<AnyProps>>;

/** The element @bacons/mdx renders for a tag, with the website's classes on it. */
function styled(tag: string, className: string): ComponentType<AnyProps> {
  const Base = dom[tag];
  const Styled = (props: AnyProps) => <Base {...props} className={className} />;
  Styled.displayName = `docs.${tag}`;
  return Styled;
}

/** @bacons/mdx passes layout hints to every component; our components take only their own props. */
function own<T extends AnyProps>(props: T): T {
  const { index: _i, firstChild: _f, lastChild: _l, firstOfType: _o, prevSibling: _p, parentName: _n, components: _c, ...rest } = props as AnyProps & Record<string, unknown>;
  return rest as T;
}

/** Stages in order with an arrow between each: across on a wide window, down on a phone. */
function Flow(props: AnyProps) {
  const { children } = own(props);
  return <ol className="my-6 flex list-none flex-col gap-2 p-0 md:flex-row md:items-stretch">{children}</ol>;
}

function Stage(props: AnyProps) {
  const { name, children } = own(props) as { name: string; children?: ReactNode };
  return (
    <li className="group flex flex-1 flex-col gap-2 md:flex-row md:items-stretch">
      <div className="flex flex-1 flex-col gap-1 rounded-lg border border-border bg-card px-4 py-3">
        <span className="text-[15px] font-semibold leading-5 text-foreground">{name}</span>
        <span className="text-sm leading-5 text-muted-foreground [&>p]:m-0">{children}</span>
      </div>
      <span aria-hidden="true" className="flex items-center justify-center text-muted-foreground group-last:hidden">
        <svg width="16" height="16" viewBox="0 0 16 16" fill="none" className="rotate-90 md:rotate-0">
          <path d="M3 8h10M9 4l4 4-4 4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </span>
    </li>
  );
}

/** A table scrolls sideways inside its own frame on a narrow window, so the page never does. */
function Table(props: AnyProps) {
  const Base = dom.table;
  return (
    <div className="mb-6 overflow-x-auto">
      <Base {...props} className="w-full border-collapse text-left text-sm leading-5" />
    </div>
  );
}

export const docsComponents: Record<string, ComponentType<AnyProps>> = {
  ...dom,
  h2: styled("h2", "mb-3 mt-10 scroll-mt-6 text-[22px] font-semibold leading-7 tracking-tight text-foreground first:mt-0"),
  h3: styled("h3", "mb-2 mt-7 scroll-mt-6 text-[17px] font-semibold leading-6 text-foreground"),
  p: styled("p", "my-0 mb-4 text-base leading-7 text-foreground/80"),
  a: styled("a", "font-medium text-primary underline-offset-4 hover:underline"),
  strong: styled("strong", "font-semibold text-foreground"),
  ul: styled("ul", "mb-4 flex list-disc flex-col gap-1.5 pl-6 text-base leading-7 text-foreground/80"),
  ol: styled("ol", "mb-4 flex list-decimal flex-col gap-1.5 pl-6 text-base leading-7 text-foreground/80"),
  li: styled("li", "pl-1 [&>p]:m-0"),
  hr: styled("hr", "my-8 border-0 border-t border-border"),
  code: styled("code", "rounded-sm bg-muted px-1.5 py-0.5 font-mono text-[13px] text-foreground"),
  pre: styled("pre", "mb-4 overflow-x-auto rounded-lg border border-border bg-canvas p-4 font-mono text-[13px] leading-5 text-foreground [&>code]:bg-transparent [&>code]:p-0"),
  table: Table,
  th: styled("th", "border-b border-input py-2 pr-4 align-top text-[13px] font-semibold text-foreground"),
  td: styled("td", "border-b border-border py-2.5 pr-4 align-top text-foreground/80"),
  Flow,
  Stage,
};
