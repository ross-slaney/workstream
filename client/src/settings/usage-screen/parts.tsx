import type { ReactNode } from "react";
import { ReadState } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import type { ModelReport, TeamUsage } from "@/lib/workstream/api";
import { SettingsPage } from "../page";
import { barShares, dollarsShown, gbMonthsShown, gbShown, hoursShown, modelShown, tokensShown } from "./figures";
import type { UsageScreenProps } from "./props";

const dollars = (value: number) => value.toLocaleString("en-US", { style: "currency", currency: "USD" });

/**
 * One line of a card: what it is, a sentence about it, and its amounts under the card's column headings.
 * `share` is the line's part of the card's whole, from 0 to 1, drawn as a bar under its name where a card compares its lines.
 */
export type UsageLine = { key: string; name: string; about: string; amounts: string[]; share?: number };

/** A card's lines with the headings its amounts go under. Each surface draws these its own way. */
export type UsageLines = { first: string; headings: string[]; lines: UsageLine[]; empty: string };

/** The page's frame and its read: every surface draws its three cards inside this. */
export function UsageFrame({ usage, onRetry, children }: Pick<UsageScreenProps, "usage" | "onRetry"> & { children: (ready: TeamUsage) => ReactNode }) {
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

/** A line's part of the whole as a thin bar. Decoration: the amounts beside it say the same in figures. */
function Share({ share }: { share: number | undefined }) {
  if (share === undefined) return null;
  return (
    <span aria-hidden className="mt-1.5 block h-1 w-full max-w-[280px] overflow-hidden rounded-full bg-muted">
      <span className="block h-full rounded-full bg-primary" style={{ width: `${Math.max(Math.min(share, 1) * 100, share > 0 ? 2 : 0)}%` }} />
    </span>
  );
}

const shareOf = (part: number, whole: number) => (whole > 0 ? part / whole : 0);

/**
 * The month's AI use day by day: what it cost in all, how many calls that was, and a bar for each day so far.
 * It leads the cards about who and what used the models.
 */
export function DaysCard({ report }: { report: ModelReport }) {
  const t = useT("settings");
  const shares = barShares(report.days.map((day) => day.costUsd));
  const dayShown = (day: string) => new Date(`${day}T00:00:00Z`).toLocaleDateString(undefined, { month: "short", day: "numeric", timeZone: "UTC" });
  return (
    <Card title={t("usage.days.title")} body={t(report.everyone ? "usage.days.bodyEveryone" : "usage.days.bodyOwn")}>
      <p className="text-xl font-semibold tracking-tight text-foreground">{t("usage.days.total", { cost: dollarsShown(report.costUsd), calls: report.calls })}</p>
      {report.calls === 0 ? (
        <p className={quiet}>{t("usage.days.empty")}</p>
      ) : (
        <div className="flex flex-col gap-1.5">
          <ol aria-label={t("usage.days.title")} className="flex h-28 items-end gap-[3px] border-b border-border">
            {report.days.map((day, index) => {
              const label = t("usage.days.day", { date: dayShown(day.day), cost: dollarsShown(day.costUsd), calls: day.calls });
              return (
                <li key={day.day} title={label} aria-label={label} className="flex h-full min-w-0 flex-1 items-end">
                  <span className={cn("block w-full rounded-t-[3px]", shares[index] > 0 ? "bg-primary" : "bg-transparent")} style={{ height: `${shares[index] * 100}%` }} />
                </li>
              );
            })}
          </ol>
          <div className={cn("flex justify-between", quiet)}>
            <span>{dayShown(report.days[0].day)}</span>
            <span>{dayShown(report.days[report.days.length - 1].day)}</span>
          </div>
        </div>
      )}
    </Card>
  );
}

/** Each person's month: the tasks they started, their desktops' hours, and what their desktops' AI calls cost. */
export function PeopleCard({ report, draw }: { report: ModelReport; draw: (lines: UsageLines) => ReactNode }) {
  const t = useT("settings");
  const lines: UsageLine[] = report.people.map((person) => ({
    key: person.userId || "nobody",
    name: person.name || t("usage.people.unknown"),
    about: "",
    share: shareOf(person.costUsd, report.costUsd),
    amounts: [
      t("usage.people.taskCount", { count: person.tasks }),
      t("usage.desktops.hours", { hours: hoursShown(person.desktopHours) }),
      t("usage.calls", { count: person.calls }),
      dollarsShown(person.costUsd),
    ],
  }));
  return (
    <Card title={t(report.everyone ? "usage.people.title" : "usage.people.titleOwn")} body={t(report.everyone ? "usage.people.body" : "usage.people.bodyOwn")}>
      {draw({
        first: t("usage.people.person"),
        headings: [t("usage.people.tasks"), t("usage.people.hours"), t("usage.people.calls"), t("usage.people.cost")],
        lines,
        empty: t("usage.people.empty"),
      })}
    </Card>
  );
}

/** Which program made the calls: Workstream's own agent, or a tool on a desktop such as OpenCode. */
export function ToolsCard({ report, draw }: { report: ModelReport; draw: (lines: UsageLines) => ReactNode }) {
  const t = useT("settings");
  const lines: UsageLine[] = report.tools.map((tool) => ({
    key: tool.tool,
    name: t(`usage.tools.names.${tool.tool}` as "usage.tools.names.workstream", { defaultValue: tool.tool }),
    about: t(`usage.tools.about.${tool.tool}` as "usage.tools.about.workstream", { defaultValue: t("usage.tools.about.other") }),
    share: shareOf(tool.costUsd, report.costUsd),
    amounts: [t("usage.calls", { count: tool.calls }), dollarsShown(tool.costUsd)],
  }));
  return (
    <Card title={t("usage.tools.title")} body={t("usage.tools.body")}>
      {draw({ first: t("usage.tools.tool"), headings: [t("usage.tools.calls"), t("usage.tools.cost")], lines, empty: t("usage.days.empty") })}
    </Card>
  );
}

/** Which models answered, how much was sent to each and how much each wrote back. */
export function ModelsUsedCard({ report, draw }: { report: ModelReport; draw: (lines: UsageLines) => ReactNode }) {
  const t = useT("settings");
  const lines: UsageLine[] = report.models.map((model) => ({
    key: model.model,
    name: modelShown(model.model),
    about: "",
    share: shareOf(model.costUsd, report.costUsd),
    amounts: [
      t("usage.modelsUsed.tokens", { amount: tokensShown(model.inputTokens) }),
      t("usage.modelsUsed.tokens", { amount: tokensShown(model.outputTokens) }),
      t("usage.calls", { count: model.calls }),
      dollarsShown(model.costUsd),
    ],
  }));
  return (
    <Card title={t("usage.modelsUsed.title")} body={t("usage.modelsUsed.body")}>
      {draw({
        first: t("usage.modelsUsed.model"),
        headings: [t("usage.modelsUsed.sent"), t("usage.modelsUsed.answered"), t("usage.modelsUsed.calls"), t("usage.modelsUsed.cost")],
        lines,
        empty: t("usage.days.empty"),
      })}
    </Card>
  );
}

/** The tasks whose AI calls cost most this month, with who started each. */
export function TasksCard({ report, draw }: { report: ModelReport; draw: (lines: UsageLines) => ReactNode }) {
  const t = useT("settings");
  const lines: UsageLine[] = report.tasks.map((task) => ({
    key: task.taskId,
    name: task.title ?? t("usage.tasks.deleted"),
    about: report.everyone && task.userName ? t("usage.tasks.by", { name: task.userName }) : "",
    amounts: [t("usage.calls", { count: task.calls }), dollarsShown(task.costUsd)],
  }));
  return (
    <Card title={t("usage.tasks.title")} body={t("usage.tasks.body")}>
      {draw({ first: t("usage.tasks.task"), headings: [t("usage.tasks.calls"), t("usage.tasks.cost")], lines, empty: t("usage.days.empty") })}
    </Card>
  );
}

/** The cards about who and what used the models, once the report has been read. While it has not, the page's other cards stand alone. */
export function ReportCards({ report, draw }: { report: UsageScreenProps["report"]; draw: (lines: UsageLines) => ReactNode }) {
  if (report.kind !== "ready") return null;
  return (
    <>
      <DaysCard report={report.value} />
      <PeopleCard report={report.value} draw={draw} />
      <ToolsCard report={report.value} draw={draw} />
      <ModelsUsedCard report={report.value} draw={draw} />
      <TasksCard report={report.value} draw={draw} />
    </>
  );
}

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
            <th key={heading} scope="col" className={cn("py-2 pl-4 text-right text-[12.5px] font-medium text-muted-foreground", headings.length > 2 ? "w-[124px]" : "w-[150px]")}>
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
              <Share share={line.share} />
            </th>
            {line.amounts.map((amount, index) => (
              <td key={headings[index]} className="whitespace-nowrap py-3 pl-4 text-right align-top text-sm tabular-nums text-foreground">
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
            <Share share={line.share} />
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
