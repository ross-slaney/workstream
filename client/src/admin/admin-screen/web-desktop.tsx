import { Button } from "@/components/kit/button";
import { AdminBody, Card, ChartCard, CostsCard, Numbers, StartsCard, agentState, desktopState, desktopsOf, hoursOf, modelsOf } from "./parts";
import { since } from "../format";
import type { AdminScreenProps } from "./props";

const th = "px-3 py-2 text-left text-[12.5px] font-medium text-muted-foreground";
const td = "px-3 py-2.5 text-sm text-foreground";

/** The desktop: the numbers in a row, the curve, then two tables that read across. */
export function AdminScreen(p: AdminScreenProps) {
  const now = Date.now();
  return (
    <AdminBody p={p}>
      {(overview, editLimits) => (
        <>
          <Numbers overview={overview} />
          <ChartCard overview={overview} />
          <CostsCard overview={overview} now={now} />
          <StartsCard overview={overview} />
          <Card title="Running now" note={`${overview.desktops.length} ${overview.desktops.length === 1 ? "desktop" : "desktops"}`}>
            {overview.desktops.length === 0 ? (
              <p className="py-2 text-sm text-muted-foreground">No desktop is running.</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full border-collapse">
                  <thead>
                    <tr className="border-b border-border">
                      {["Task", "Team", "Size", "Desktop", "Agent", "Running for"].map((heading) => (
                        <th key={heading} scope="col" className={th}>
                          {heading}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-border">
                    {overview.desktops.map((desktop) => (
                      <tr key={desktop.taskId}>
                        <td className={`${td} max-w-[320px] truncate font-medium`}>{desktop.title}</td>
                        <td className={td}>{desktop.teamName}</td>
                        <td className={`${td} capitalize`}>{desktop.size}</td>
                        <td className={td}>{desktopState(desktop)}</td>
                        <td className={td}>{agentState(desktop)}</td>
                        <td className={`${td} tabular-nums`}>{since(desktop.since, now)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Card>
          <Card title="Teams" note="This month">
            <div className="overflow-x-auto">
              <table className="w-full border-collapse">
                <thead>
                  <tr className="border-b border-border">
                    {["Team", "People", "Tasks", "Desktops running", "Models", "Desktop time", ""].map((heading, index) => (
                      <th key={index} scope="col" className={th}>
                        {heading}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-border">
                  {overview.teamNumbers.map((team) => (
                    <tr key={team.id}>
                      <td className={`${td} max-w-[280px] truncate font-medium`}>{team.name}</td>
                      <td className={`${td} tabular-nums`}>{team.people}</td>
                      <td className={`${td} tabular-nums`}>{team.tasks}</td>
                      <td className={`${td} tabular-nums`}>{desktopsOf(team)}</td>
                      <td className={`${td} tabular-nums`}>{modelsOf(team)}</td>
                      <td className={`${td} tabular-nums`}>{hoursOf(team)}</td>
                      <td className="px-3 py-1.5 text-right">
                        <Button size="small" tone="quiet" label="Change limits" aria-label={`Change limits for ${team.name}`} onClick={() => editLimits(team)} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Card>
        </>
      )}
    </AdminBody>
  );
}
