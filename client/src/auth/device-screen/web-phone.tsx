import { Brand } from "@/src/auth/sign-in-screen/parts";
import { DeviceStep } from "./parts";
import type { DeviceScreenProps } from "./props";

/** A phone's browser: the step on the page itself, from the top, like sign-in. */
export function DeviceScreen(p: DeviceScreenProps) {
  return (
    <main className="flex h-full flex-col gap-8 overflow-y-auto bg-background px-5 py-6">
      <Brand href={p.homeHref} />
      <DeviceStep {...p} />
    </main>
  );
}
