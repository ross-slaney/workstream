import type { ReactNode } from "react";
import { Button } from "@/components/kit/button";
import { AdminBody, Card, ChartCard, CostsCard, ModelsCard, Numbers, StartsCard, agentState, desktopState, desktopsOf, hoursOf, modelsOf } from "./parts";
import { since } from "../format";
import type { AdminScreenProps } from "./props";

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-3">
      <dt className="text-[13px] text-muted-foreground">{label}</dt>
      <dd className="text-right text-sm tabular-nums text-foreground">{children}</dd>
    </div>
  );
}

/** A phone's browser: the numbers two across, the curve, then each desktop and each team as a card of facts, because a table would not fit. */
export function AdminScreen(p: AdminScreenProps) {
  const now = Date.now();
  return (
    <AdminBody p={p}>
      {(overview, editLimits) => (
        <>
          <Numbers overview={overview} />
          <ChartCard overview={overview} />
          <CostsCard overview={overview} now={now} />
          <ModelsCard overview={overview} />
          <StartsCard overview={overview} />
          <Card title="Running now" note={`${overview.desktops.length} ${overview.desktops.length === 1 ? "desktop" : "desktops"}`}>
            {overview.desktops.length === 0 ? (
              <p className="py-2 text-sm text-muted-foreground">No desktop is running.</p>
            ) : (
              <ul className="flex flex-col divide-y divide-border">
                {overview.desktops.map((desktop) => (
                  <li key={desktop.taskId} className="flex flex-col gap-1.5 py-3">
                    <span className="truncate text-sm font-medium text-foreground">{desktop.title}</span>
                    <dl className="flex flex-col gap-1">
                      <Fact label="Team">{desktop.teamName}</Fact>
                      <Fact label="Desktop">
                        <span className="capitalize">{desktop.size}</span>, {desktopState(desktop).toLowerCase()}
                      </Fact>
                      <Fact label="Agent">{agentState(desktop)}</Fact>
                      <Fact label="Running for">{since(desktop.since, now)}</Fact>
                    </dl>
                  </li>
                ))}
              </ul>
            )}
          </Card>
          <Card title="Teams" note="This month">
            <ul className="flex flex-col divide-y divide-border">
              {overview.teamNumbers.map((team) => (
                <li key={team.id} className="flex flex-col gap-1.5 py-3">
                  <span className="truncate text-sm font-medium text-foreground">{team.name}</span>
                  <dl className="flex flex-col gap-1">
                    <Fact label="People">{team.people}</Fact>
                    <Fact label="Tasks">{team.tasks}</Fact>
                    <Fact label="Desktops running">{desktopsOf(team)}</Fact>
                    <Fact label="Models">{modelsOf(team)}</Fact>
                    <Fact label="Desktop time">{hoursOf(team)}</Fact>
                  </dl>
                  <Button size="small" label="Change limits" aria-label={`Change limits for ${team.name}`} className="self-start" onClick={() => editLimits(team)} />
                </li>
              ))}
            </ul>
          </Card>
        </>
      )}
    </AdminBody>
  );
}
