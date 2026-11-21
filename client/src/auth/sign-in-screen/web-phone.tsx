import { Brand, SignInForm } from "./parts";
import type { SignInScreenProps } from "./props";

/** A phone's browser: the form on the page itself, from the top, with nothing around it to scroll past. */
export function SignInScreen(p: SignInScreenProps) {
  return (
    <main className="flex h-full flex-col gap-8 overflow-y-auto bg-background px-5 py-6">
      <Brand href={p.homeHref} />
      <SignInForm {...p} />
    </main>
  );
}
