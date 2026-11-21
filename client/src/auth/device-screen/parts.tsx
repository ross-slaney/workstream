import { Button } from "@/components/kit/button";
import { ErrorNote, Spinner } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import { SignInForm } from "@/src/auth/sign-in-screen/parts";
import type { DeviceScreenProps } from "./props";

/** The step on screen, the same on every surface: sign in, then allow the command line or not. */
export function DeviceStep(p: DeviceScreenProps) {
  const t = useT("auth");
  switch (p.mode) {
    case "opening":
      return (
        <div className="flex items-center gap-3 text-sm text-muted-foreground">
          <Spinner label={t("device.opening")} />
          <span>{t("device.opening")}</span>
        </div>
      );
    case "signIn":
      return (
        <div className="flex flex-col gap-5">
          <p className="rounded-md bg-muted px-3 py-2.5 text-[13px] text-foreground">{t("device.signInNote")}</p>
          <SignInForm {...p.signIn} />
        </div>
      );
    case "approve":
      return (
        <div className="flex flex-col gap-5">
          <Heading title={t("device.approveTitle")} body={t("device.approveBody", { email: p.email ?? "" })} />
          {p.code ? (
            <div className="flex flex-col gap-1.5">
              <span className="text-[13px] text-muted-foreground">{t("device.codeLabel")}</span>
              <span className="self-start rounded-md border border-border bg-muted px-3 py-1.5 font-mono text-lg tracking-widest text-foreground">{p.code}</span>
            </div>
          ) : null}
          <ErrorNote message={p.error} />
          <div className="flex flex-wrap gap-2">
            <Button tone="primary" label={t("device.approve")} busy={p.busy} onClick={p.onApprove} />
            <Button tone="outline" label={t("device.deny")} disabled={p.busy} onClick={p.onDeny} />
          </div>
        </div>
      );
    case "approved":
      return <Heading title={t("device.approvedTitle")} body={t("device.approvedBody")} />;
    case "denied":
      return <Heading title={t("device.deniedTitle")} body={t("device.deniedBody")} />;
    case "unusable":
      return <Heading title={t("device.unusableTitle")} body={t("device.unusable")} />;
  }
}

function Heading({ title, body }: { title: string; body: string }) {
  return (
    <div className="flex flex-col gap-1.5">
      <h1 className="text-2xl font-semibold tracking-tight text-foreground">{title}</h1>
      <p className="text-sm text-muted-foreground">{body}</p>
    </div>
  );
}
