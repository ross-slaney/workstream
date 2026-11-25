import { useState, type ReactNode } from "react";
import { Button } from "@/components/kit/button";
import { Dialog } from "@/components/kit/dialog";
import { TextField } from "@/components/kit/field";
import { ErrorNote, ReadState } from "@/components/kit/status";
import type { AdminOverview, RunningDesktop, TeamNumbers } from "@/lib/workstream/api";
import { dollars, gbMonths, gigabytes, hours, seconds, since } from "../format";
import { RunningChart } from "../running-chart";
import type { AdminScreenProps } from "./props";

/* English only: this page is for the people who run Workstream. */

/** The frame: a bar that says where you are, and the page under it. */
export function AdminFrame({ onBack, children }: { onBack: () => void; children: ReactNode }) {
  return (
    <div className="flex h-full flex-col bg-canvas text-foreground">
      <header className="flex h-14 shrink-0 items-center justify-between gap-4 border-b border-border bg-background px-5">
        <div className="flex items-baseline gap-2">
          <span className="text-[15px] font-semibold tracking-tight">Workstream</span>
          <span className="text-[13px] text-muted-foreground">System administration</span>
        </div>
        <Button size="small" label="Back to my tasks" onClick={onBack} />
      </header>
      <main className="min-h-0 flex-1 overflow-y-auto">
        <div className="mx-auto flex w-full max-w-[1120px] flex-col gap-6 px-4 py-6 md:px-8 md:py-8">{children}</div>
      </main>
    </div>
  );
}

/** The six numbers at the top. */
export function Numbers({ overview }: { overview: AdminOverview }) {
  const tiles: [string, string][] = [
    ["Desktops running", String(overview.runningDesktops)],
    ["Teams", String(overview.teams)],
    ["People", String(overview.people)],
    ["Tasks", String(overview.tasks)],
    ["Models this month", dollars(overview.modelSpentUsd)],
    ["Desktop time this month", hours(overview.desktopHours)],
  ];
  return (
    <dl className="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-6">
      {tiles.map(([label, value]) => (
        <div key={label} className="flex flex-col gap-1 rounded-lg border border-border bg-card px-4 py-3">
          <dt className="text-[12.5px] text-muted-foreground">{label}</dt>
          <dd className="text-xl font-semibold tracking-tight text-foreground">{value}</dd>
        </div>
      ))}
    </dl>
  );
}

export function Card({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-3 rounded-lg border border-border bg-card p-4 md:p-5">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="text-[15px] font-semibold text-foreground">{title}</h2>
        {note ? <span className="text-[12.5px] text-muted-foreground">{note}</span> : null}
      </div>
      {children}
    </section>
  );
}

export function ChartCard({ overview }: { overview: AdminOverview }) {
  const peak = Math.max(0, ...overview.running.map((point) => point.running));
  return (
    <Card title="Desktops running, last 24 hours" note={`Peak ${peak}`}>
      <RunningChart points={overview.running} />
    </Card>
  );
}

const cell = "px-3 py-2 text-sm text-foreground";
const head = "px-3 py-2 text-left text-[12.5px] font-medium text-muted-foreground";

const PURPOSE: Record<string, string> = { task: "Tasks' desktops", spare: "Spares", prepare: "Preparing environments" };
const KIND: Record<string, string> = { disk: "Desktops' disks", archive: "Archived tasks", setup: "Saved setups" };

/**
 * What the desktops cost Workstream this month, each line at its own price: the hours machines ran, by what
 * they ran for and at which size; the storage kept, by kind, in GB-months from the rows the storage job
 * keeps; and the spares running now. Narrow enough for a phone, so both surfaces draw it the same.
 */
export function CostsCard({ overview, now }: { overview: AdminOverview; now: number }) {
  const { thisMonth, storage, spares, mac } = overview.machines;
  const machinesTotal = thisMonth.reduce((sum, line) => sum + line.usd, 0);
  const storageTotal = storage.reduce((sum, line) => sum + line.usd, 0);
  return (
    <Card title="What the desktops cost" note="This month, at Azure's list prices">
      <table className="w-full border-collapse">
        <thead>
          <tr className="border-b border-border">
            <th scope="col" className={head}>Machines running</th>
            <th scope="col" className={`${head} text-right`}>Hours</th>
            <th scope="col" className={`${head} text-right`}>Cost</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {thisMonth.map((line) => (
            <tr key={`${line.purpose}:${line.size}`}>
              <td className={cell}>
                {PURPOSE[line.purpose] ?? line.purpose}, <span className="text-muted-foreground">{line.size}</span>
              </td>
              <td className={`${cell} whitespace-nowrap text-right tabular-nums`}>{hours(line.hours)}</td>
              <td className={`${cell} whitespace-nowrap text-right tabular-nums`}>{dollars(line.usd)}</td>
            </tr>
          ))}
          <tr>
            <td className={`${cell} font-medium`}>All machines</td>
            <td className={cell} />
            <td className={`${cell} whitespace-nowrap text-right font-medium tabular-nums`}>{dollars(machinesTotal)}</td>
          </tr>
        </tbody>
      </table>
      <table className="w-full border-collapse">
        <thead>
          <tr className="border-b border-border">
            <th scope="col" className={head}>Storage kept</th>
            <th scope="col" className={`${head} text-right`}>Now</th>
            <th scope="col" className={`${head} text-right`}>GB-months</th>
            <th scope="col" className={`${head} text-right`}>Cost</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {storage.map((line) => (
            <tr key={line.kind}>
              <td className={cell}>
                {KIND[line.kind] ?? line.kind}, <span className="text-muted-foreground">{line.kept} kept</span>
              </td>
              <td className={`${cell} whitespace-nowrap text-right tabular-nums`}>{gigabytes(line.gb)}</td>
              <td className={`${cell} whitespace-nowrap text-right tabular-nums`}>{gbMonths(line.gbMonths)}</td>
              <td className={`${cell} whitespace-nowrap text-right tabular-nums`}>{dollars(line.usd)}</td>
            </tr>
          ))}
          <tr>
            <td className={`${cell} font-medium`}>{storage.length === 0 ? "Nothing is kept" : "All storage"}</td>
            <td className={cell} />
            <td className={cell} />
            <td className={`${cell} whitespace-nowrap text-right font-medium tabular-nums`}>{dollars(storageTotal)}</td>
          </tr>
        </tbody>
      </table>
      {mac && mac.builds > 0 ? (
        <table className="w-full border-collapse">
          <thead>
            <tr className="border-b border-border">
              <th scope="col" className={head}>iPhone builds</th>
              <th scope="col" className={`${head} text-right`}>Minutes</th>
              <th scope="col" className={`${head} text-right`}>Cost</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td className={cell}>
                A Mac, <span className="text-muted-foreground">{mac.builds === 1 ? "1 build" : `${mac.builds} builds`}</span>
              </td>
              <td className={`${cell} whitespace-nowrap text-right tabular-nums`}>{Math.round(mac.minutes).toLocaleString()}</td>
              <td className={`${cell} whitespace-nowrap text-right tabular-nums`}>{dollars(mac.usd)}</td>
            </tr>
          </tbody>
        </table>
      ) : null}
      <p className="text-[13px] text-muted-foreground">
        A GB-month is a gigabyte kept for a whole month; what is kept is counted every 15 minutes. A desktop's disk is counted at its full size, an archived task and a saved setup at what is on them.{" "}
        {spares.length === 0
          ? "No spare is running."
          : `Spares running now: ${spares.map((spare) => `${spare.teamName} (${spare.size}, ${spare.state}, ${since(spare.since, now)})`).join("; ")}.`}
      </p>
    </Card>
  );
}

const SOURCE: Record<string, string> = { image: "A new machine", save: "A new machine, from a saved setup", spare: "A spare", stopped: "Its own machine, paused", archive: "A new machine, from its archive" };

/** How long a start took, from when each step was written down as it happened: the middle start of the last seven days, by what it started from. */
export function StartsCard({ overview }: { overview: AdminOverview }) {
  return (
    <Card title="How long a start takes" note="Last 7 days, the middle start">
      {overview.starts.length === 0 ? (
        <p className="py-2 text-sm text-muted-foreground">No desktop has started in the last seven days.</p>
      ) : (
        <table className="w-full border-collapse">
          <thead>
            <tr className="border-b border-border">
              <th scope="col" className={head}>Started from</th>
              <th scope="col" className={`${head} text-right`}>Starts</th>
              <th scope="col" className={`${head} text-right`}>Screen</th>
              <th scope="col" className={`${head} text-right`}>Agent</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
            {overview.starts.map((line) => (
              <tr key={line.source}>
                <td className={cell}>{SOURCE[line.source] ?? line.source}</td>
                <td className={`${cell} text-right tabular-nums`}>{line.starts}</td>
                <td className={`${cell} text-right tabular-nums`}>{seconds(line.screenSeconds)}</td>
                <td className={`${cell} text-right tabular-nums`}>{seconds(line.agentSeconds)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  );
}

const STATE: Record<string, string> = { starting: "Starting", ready: "Ready", stopping: "Pausing", stopped: "Paused", archiving: "Archiving", archived: "Archived", failed: "Failed", deleting: "Deleting" };

export const desktopState = (desktop: RunningDesktop) => STATE[desktop.state] ?? desktop.state;
export const agentState = (desktop: RunningDesktop) => (desktop.agentWorking ? "Working" : "Waiting");
export const desktopsOf = (team: TeamNumbers) => `${team.runningDesktops} of ${team.maxRunningDesktops}`;
export const modelsOf = (team: TeamNumbers) => `${dollars(team.modelSpentUsd)} of ${dollars(team.modelAllowanceUsd)}`;
export const hoursOf = (team: TeamNumbers) => hours(team.desktopHours);

/** A team's limits: what its agents may spend on models in a month, and how many desktops it may run at once. */
export function LimitsDialog({ team, busy, error, onClose, onSave }: { team: TeamNumbers; busy: boolean; error: string | null; onClose: () => void; onSave: (allowance: number, desktops: number) => void }) {
  const [allowance, setAllowance] = useState(String(team.modelAllowanceUsd));
  const [desktops, setDesktops] = useState(String(team.maxRunningDesktops));
  const allowanceValue = Number(allowance);
  const desktopsValue = Number(desktops);
  const valid = allowance.trim() !== "" && Number.isFinite(allowanceValue) && allowanceValue >= 0 && Number.isInteger(desktopsValue) && desktopsValue >= 0;
  return (
    <Dialog
      title={`Limits for ${team.name}`}
      onClose={onClose}
      footer={
        <>
          <Button label="Cancel" onClick={onClose} />
          <Button tone="primary" label="Save limits" busy={busy} disabled={!valid} onClick={() => onSave(allowanceValue, desktopsValue)} />
        </>
      }
    >
      <ErrorNote message={error} />
      <TextField label="Model allowance, US dollars a month" hint={`Used so far this month: ${dollars(team.modelSpentUsd)}. At the allowance, the team's agents stop.`} inputMode="decimal" value={allowance} onChangeText={setAllowance} />
      <TextField label="Desktops running at once" hint={`Running now: ${team.runningDesktops}. Lowering it stops nothing that is running.`} inputMode="numeric" value={desktops} onChangeText={setDesktops} />
    </Dialog>
  );
}

/** What every surface does around its own layout: the read's state, the error, and the dialog. */
export function AdminBody({ p, children }: { p: AdminScreenProps; children: (overview: AdminOverview, editLimits: (team: TeamNumbers) => void) => ReactNode }) {
  const [editing, setEditing] = useState<TeamNumbers | null>(null);
  return (
    <AdminFrame onBack={p.onBack}>
      {p.overview.kind === "ready" ? children(p.overview.value, setEditing) : <ReadState read={p.overview} onRetry={p.onRetry} />}
      {editing ? (
        <LimitsDialog
          team={editing}
          busy={p.busy}
          error={p.error}
          onClose={() => setEditing(null)}
          onSave={(allowance, desktops) => void p.onSetLimits(editing.id, allowance, desktops).then((saved) => saved && setEditing(null))}
        />
      ) : null}
    </AdminFrame>
  );
}
