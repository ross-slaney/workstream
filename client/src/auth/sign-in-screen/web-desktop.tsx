import { Brand, SignInForm } from "./parts";
import type { SignInScreenProps } from "./props";

/** The desktop: the form in a card at the middle of the frame, the brand above it. */
export function SignInScreen(p: SignInScreenProps) {
  return (
    <main className="flex h-full flex-col items-center overflow-y-auto bg-canvas px-6 py-10">
      <div className="flex w-full max-w-[400px] flex-1 flex-col justify-center gap-8">
        <Brand href={p.homeHref} />
        <div className="rounded-xl border border-border bg-card p-7">
          <SignInForm {...p} />
        </div>
      </div>
    </main>
  );
}
