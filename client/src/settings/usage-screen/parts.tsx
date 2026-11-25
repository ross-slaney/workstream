import type { ReactNode } from "react";
import { ReadState } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import type { TeamUsage } from "@/lib/workstream/api";
import { SettingsPage } from "../page";
import { gbMonthsShown, gbShown, hoursShown } from "./figures";
import type { UsageScreenProps } from "./props";

const dollars = (value: number) => value.toLocaleString("en-US", { style: "currency", currency: "USD" });

/** One line of a card: what it is, a sentence about it, and its amounts under the card's column headings. */
export type UsageLine = { key: string; name: string; about: string; amounts: string[] };

/** A card's lines with the headings its amounts go under. Each surface draws these its own way. */
export type UsageLines = { first: string; headings: string[]; lines: UsageLine[]; empty: string };

/** The page's frame and its read: every surface draws its three cards inside this. */
export function UsageFrame({ usage, onRetry, children }: UsageScreenProps & { children: (ready: TeamUsage) => ReactNode }) {
  const t = useT("settings");
  const ready = usage.kind === "ready" ? usage.value : null;
  const since = ready ? new Date(ready.since).toLocaleDateString(undefined, { month: "long", day: "numeric", timeZone: "UTC" }) : "";
  return (
    <SettingsPage title={t("usage.title")} subtitle={ready ? t("usage.subtitle", { date: since }) : undefined}>
      {ready ? <div className="flex flex-col gap-4">{children(ready)}</div> : <ReadState read={usage} onRetry={onRetry} />}
    </SettingsPage>
  );
}

/** A card: its title, what it shows, and a sentence at the bottom saying how it is counted. */
export function Card({ title, body, children }: { title: string; body: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-4 rounded-lg border border-border bg-card p-5">
      <h2 className="text-[15px] font-semibold text-foreground">{title}</h2>
      {children}
      <p className="text-[13px] leading-relaxed text-muted-foreground">{body}</p>
    </section>
  );
}

/** Models: what the team's agents spent of the month's allowance. The one card with dollars on it. */
export function ModelsCard({ usage }: { usage: TeamUsage }) {
  const t = useT("settings");
  return (
    <Card title={t("usage.models.title")} body={t("usage.models.body")}>
      <p className="text-xl font-semibold tracking-tight text-foreground">{t("usage.models.spent", { spent: dollars(usage.modelSpentUsd), allowance: dollars(usage.modelAllowanceUsd) })}</p>
      <Meter used={usage.modelSpentUsd} of={usage.modelAllowanceUsd} label={t("usage.models.left", { left: dollars(Math.max(0, usage.modelAllowanceUsd - usage.modelSpentUsd)) })} />
    </Card>
  );
}

/** Desktop time: how many are running beside the limit, then the month's hours for each size used. */
export function DesktopsCard({ usage, draw }: { usage: TeamUsage; draw: (lines: UsageLines) => ReactNode }) {
  const t = useT("settings");
  const lines: UsageLine[] = usage.desktopHours.map((line) => ({
    key: line.size,
    name: t(`usage.desktops.sizes.${line.size}` as "usage.desktops.sizes.standard", { defaultValue: line.size }),
    about: t(`usage.desktops.machines.${line.size}` as "usage.desktops.machines.standard", { defaultValue: "" }),
    amounts: [t("usage.desktops.hours", { hours: hoursShown(line.hours) })],
  }));
  return (
    <Card title={t("usage.desktops.title")} body={t("usage.desktops.body")}>
      <Meter used={usage.runningDesktops} of={usage.maxRunningDesktops} label={t("usage.desktops.running", { running: usage.runningDesktops, limit: usage.maxRunningDesktops })} />
      {draw({ first: t("usage.desktops.size"), headings: [t("usage.desktops.month")], lines, empty: t("usage.desktops.empty") })}
    </Card>
  );
}

/** Storage: for each kind the team kept this month, the gigabytes kept now and the month so far in GB-months. */
export function StorageCard({ usage, draw }: { usage: TeamUsage; draw: (lines: UsageLines) => ReactNode }) {
  const t = useT("settings");
  const lines: UsageLine[] = usage.storage.map((line) => ({
    key: line.kind,
    name: t(`usage.storage.kinds.${line.kind}` as "usage.storage.kinds.disk", { defaultValue: line.kind }),
    about: t(`usage.storage.about.${line.kind}` as "usage.storage.about.disk", { defaultValue: "" }),
    amounts: [t("usage.storage.gb", { amount: gbShown(line.gb).toLocaleString() }), t("usage.storage.gbMonths", { amount: gbMonthsShown(line.gbMonths) })],
  }));
  return (
    <Card title={t("usage.storage.title")} body={t("usage.storage.body")}>
      {draw({ first: t("usage.storage.kind"), headings: [t("usage.storage.now"), t("usage.storage.month")], lines, empty: t("usage.storage.empty") })}
    </Card>
  );
}

/** iPhone builds: the minutes a Mac spent building the team's apps this month. Only a team that had one built sees it. */
export function BuildsCard({ usage }: { usage: TeamUsage }) {
  const t = useT("settings");
  if (!(usage.macBuildMinutes > 0)) return null;
  return (
    <Card title={t("usage.builds.title")} body={t("usage.builds.body")}>
      <p className="text-xl font-semibold tracking-tight text-foreground">{t("usage.builds.minutes", { minutes: Math.round(usage.macBuildMinutes) })}</p>
    </Card>
  );
}

const quiet = "text-[13px] text-muted-foreground";

/** The lines as a table: a row each, its amounts in columns that line up down the card. For a wide window. */
export function LinesTable({ first, headings, lines, empty }: UsageLines) {
  if (lines.length === 0) return <p className={quiet}>{empty}</p>;
  return (
    <table className="w-full border-collapse">
      <thead>
        <tr className="border-b border-border">
          <th scope="col" className="py-2 pr-4 text-left text-[12.5px] font-medium text-muted-foreground">
            {first}
          </th>
          {headings.map((heading) => (
            <th key={heading} scope="col" className="w-[150px] py-2 pl-4 text-right text-[12.5px] font-medium text-muted-foreground">
              {heading}
            </th>
          ))}
        </tr>
      </thead>
      <tbody className="divide-y divide-border">
        {lines.map((line) => (
          <tr key={line.key}>
            <th scope="row" className="py-3 pr-4 text-left align-top font-normal">
              <span className="block text-sm font-medium text-foreground">{line.name}</span>
              {line.about ? <span className={cn("mt-0.5 block max-w-[440px] leading-relaxed", quiet)}>{line.about}</span> : null}
            </th>
            {line.amounts.map((amount, index) => (
              <td key={headings[index]} className="py-3 pl-4 text-right align-top text-sm tabular-nums text-foreground">
                {amount}
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** The lines one under the other, each amount on a row of its own beside its heading. For a narrow window, where columns would not fit. */
export function LinesList({ headings, lines, empty }: UsageLines) {
  if (lines.length === 0) return <p className={quiet}>{empty}</p>;
  return (
    <ul className="flex flex-col divide-y divide-border border-t border-border">
      {lines.map((line) => (
        <li key={line.key} className="flex flex-col gap-2 py-3">
          <div className="flex flex-col gap-0.5">
            <span className="text-sm font-medium text-foreground">{line.name}</span>
            {line.about ? <span className={cn("leading-relaxed", quiet)}>{line.about}</span> : null}
          </div>
          <dl className="flex flex-col gap-1">
            {line.amounts.map((amount, index) => (
              <div key={headings[index]} className="flex items-baseline justify-between gap-3">
                <dt className={quiet}>{headings[index]}</dt>
                <dd className="text-right text-sm tabular-nums text-foreground">{amount}</dd>
              </div>
            ))}
          </dl>
        </li>
      ))}
    </ul>
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
      <span className={quiet}>{label}</span>
    </div>
  );
}
