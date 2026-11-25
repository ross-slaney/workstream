import { BuildsCard, DesktopsCard, LinesList, ModelsCard, StorageCard, UsageFrame } from "./parts";
import type { UsageScreenProps } from "./props";

/** A phone's browser: the same three cards, each line with its amounts under it, because columns would not fit. */
export function UsageScreen(p: UsageScreenProps) {
  return (
    <UsageFrame {...p}>
      {(usage) => (
        <>
          <ModelsCard usage={usage} />
          <DesktopsCard usage={usage} draw={(lines) => <LinesList {...lines} />} />
          <StorageCard usage={usage} draw={(lines) => <LinesList {...lines} />} />
          <BuildsCard usage={usage} />
        </>
      )}
    </UsageFrame>
  );
}
