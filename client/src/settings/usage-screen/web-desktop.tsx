import { BuildsCard, DesktopsCard, LinesTable, ModelsCard, StorageCard, UsageFrame } from "./parts";
import type { UsageScreenProps } from "./props";

/** The desktop: models, desktop time and storage down one column, each card's lines a table whose amounts line up. */
export function UsageScreen(p: UsageScreenProps) {
  return (
    <UsageFrame {...p}>
      {(usage) => (
        <>
          <ModelsCard usage={usage} />
          <DesktopsCard usage={usage} draw={(lines) => <LinesTable {...lines} />} />
          <StorageCard usage={usage} draw={(lines) => <LinesTable {...lines} />} />
          <BuildsCard usage={usage} />
        </>
      )}
    </UsageFrame>
  );
}
