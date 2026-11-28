import { BuildsCard, DesktopsCard, LinesTable, ModelsCard, ReportCards, StorageCard, UsageFrame } from "./parts";
import type { UsageScreenProps } from "./props";

/** The desktop: the allowance, who and what used the models, then desktop time and storage, down one column, each card's lines a table whose amounts line up. */
export function UsageScreen(p: UsageScreenProps) {
  return (
    <UsageFrame usage={p.usage} onRetry={p.onRetry}>
      {(usage) => (
        <>
          <ModelsCard usage={usage} />
          <ReportCards report={p.report} draw={(lines) => <LinesTable {...lines} />} />
          <DesktopsCard usage={usage} draw={(lines) => <LinesTable {...lines} />} />
          <StorageCard usage={usage} draw={(lines) => <LinesTable {...lines} />} />
          <BuildsCard usage={usage} />
        </>
      )}
    </UsageFrame>
  );
}
