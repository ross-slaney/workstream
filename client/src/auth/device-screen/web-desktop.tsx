import { Brand } from "@/src/auth/sign-in-screen/parts";
import { DeviceStep } from "./parts";
import type { DeviceScreenProps } from "./props";

/** The desktop: the step in a card at the middle of the frame, the brand above it, like sign-in. */
export function DeviceScreen(p: DeviceScreenProps) {
  return (
    <main className="flex h-full flex-col items-center overflow-y-auto bg-canvas px-6 py-10">
      <div className="flex w-full max-w-[440px] flex-1 flex-col justify-center gap-8">
        <Brand href={p.homeHref} />
        <div className="rounded-xl border border-border bg-card p-7">
          <DeviceStep {...p} />
        </div>
      </div>
    </main>
  );
}
