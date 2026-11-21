import type { ReactNode } from "react";

/** The frame of a settings page: its heading, what can be done from it, and its content in one readable column. */
export function SettingsPage({ title, subtitle, action, children }: { title: string; subtitle?: string; action?: ReactNode; children: ReactNode }) {
  return (
    <main className="min-h-0 flex-1 overflow-y-auto bg-background">
      <div className="mx-auto flex w-full max-w-[860px] flex-col gap-6 px-5 py-8 md:px-8">
        <header className="flex flex-wrap items-start justify-between gap-4">
          <div className="flex min-w-0 flex-col gap-1">
            <h1 className="text-2xl font-semibold tracking-tight text-foreground">{title}</h1>
            {subtitle ? <p className="max-w-[620px] text-sm text-muted-foreground">{subtitle}</p> : null}
          </div>
          {action}
        </header>
        {children}
      </div>
    </main>
  );
}

/** A titled group on a settings page. */
export function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-[15px] font-semibold text-foreground">{title}</h2>
      {children}
    </section>
  );
}
