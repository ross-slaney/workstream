import { BuildsCard, DesktopsCard, LinesList, ModelsCard, ReportCards, StorageCard, UsageFrame } from "./parts";
import type { UsageScreenProps } from "./props";

/** A phone's browser: the same cards in the same order, each line with its amounts under it, because columns would not fit. */
export function UsageScreen(p: UsageScreenProps) {
  return (
    <UsageFrame usage={p.usage} onRetry={p.onRetry}>
      {(usage) => (
        <>
          <ModelsCard usage={usage} />
          <ReportCards report={p.report} draw={(lines) => <LinesList {...lines} />} />
          <DesktopsCard usage={usage} draw={(lines) => <LinesList {...lines} />} />
          <StorageCard usage={usage} draw={(lines) => <LinesList {...lines} />} />
          <BuildsCard usage={usage} />
        </>
      )}
    </UsageFrame>
  );
}
