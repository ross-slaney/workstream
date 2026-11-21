import { ReadState } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { SettingsPage } from "../page";
import type { UsageScreenProps } from "./props";

const dollars = (value: number) => value.toLocaleString("en-US", { style: "currency", currency: "USD" });

/** The page, the same on every surface: one card for models, one for desktops. */
export function UsagePage({ usage, onRetry }: UsageScreenProps) {
  const t = useT("settings");
  const ready = usage.kind === "ready" ? usage.value : null;
  const since = ready ? new Date(ready.since).toLocaleDateString(undefined, { month: "long", day: "numeric" }) : "";
  return (
    <SettingsPage title={t("usage.title")} subtitle={ready ? t("usage.subtitle", { date: since }) : undefined}>
      {ready ? (
        <div className="grid gap-4 md:grid-cols-2">
          <Card title={t("usage.models.title")} figure={t("usage.models.spent", { spent: dollars(ready.modelSpentUsd), allowance: dollars(ready.modelAllowanceUsd) })} body={t("usage.models.body")}>
            <Meter used={ready.modelSpentUsd} of={ready.modelAllowanceUsd} label={t("usage.models.left", { left: dollars(Math.max(0, ready.modelAllowanceUsd - ready.modelSpentUsd)) })} />
          </Card>
          <Card title={t("usage.desktops.title")} figure={t("usage.desktops.hours", { hours: Math.round(ready.desktopHours * 10) / 10 })} body={t("usage.desktops.body")}>
            <Meter used={ready.runningDesktops} of={ready.maxRunningDesktops} label={t("usage.desktops.running", { running: ready.runningDesktops, limit: ready.maxRunningDesktops })} />
          </Card>
        </div>
      ) : (
        <ReadState read={usage} onRetry={onRetry} />
      )}
    </SettingsPage>
  );
}

function Card({ title, figure, body, children }: { title: string; figure: string; body: string; children: React.ReactNode }) {
  return (
    <section className="flex flex-col gap-3 rounded-lg border border-border bg-card p-5">
      <h2 className="text-[13px] font-medium text-muted-foreground">{title}</h2>
      <p className="text-xl font-semibold tracking-tight text-foreground">{figure}</p>
      {children}
      <p className="text-[13px] leading-relaxed text-muted-foreground">{body}</p>
    </section>
  );
}

/** How much of a limit is used: a bar, and the same in words. */
function Meter({ used, of, label }: { used: number; of: number; label: string }) {
  const share = of > 0 ? Math.min(1, used / of) : used > 0 ? 1 : 0;
  return (
    <div className="flex flex-col gap-1.5">
      <div role="meter" aria-valuemin={0} aria-valuemax={of} aria-valuenow={used} aria-label={label} className="h-2 overflow-hidden rounded-full bg-muted">
        <div className={cn("h-full rounded-full", share >= 1 ? "bg-destructive" : share >= 0.8 ? "bg-warning" : "bg-primary")} style={{ width: `${Math.max(share * 100, used > 0 ? 2 : 0)}%` }} />
      </div>
      <span className="text-[13px] text-muted-foreground">{label}</span>
    </div>
  );
}
