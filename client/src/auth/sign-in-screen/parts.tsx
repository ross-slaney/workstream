import type { ReactNode } from "react";
import { Button } from "@/components/kit/button";
import { TextField } from "@/components/kit/field";
import { ErrorNote, Spinner } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import { Mark } from "@/src/landing/landing-page";
import type { SignInScreenProps } from "./props";

/** The words above the form for the step on screen. */
function useHeading(p: SignInScreenProps) {
  const t = useT("auth");
  switch (p.mode) {
    case "verify":
      return { title: t("verify.title"), subtitle: t("verify.subtitle", { email: p.email }) };
    case "name":
      return { title: t("name.title"), subtitle: t("name.subtitle") };
    default:
      if (p.invitation) {
        const team = { team: p.invitation.teamName };
        return { title: t("invite.title", team), subtitle: p.mode === "signup" ? t("invite.subtitleNew", team) : p.method === "password" ? t("invite.subtitlePassword") : t("invite.subtitleCode") };
      }
      if (p.mode === "signup") return { title: t("signUp.title"), subtitle: t("signUp.subtitle") };
      return { title: t("signIn.title"), subtitle: p.method === "password" ? t("signIn.subtitlePassword") : t("signIn.subtitleCode") };
  }
}

/** The form itself, the same on every surface: the fields for the step, the one button, and the way to the other step. */
export function SignInForm(p: SignInScreenProps) {
  const t = useT("auth");
  const heading = useHeading(p);
  if (p.mode === "finishing" || p.mode === "opening") {
    const doing = p.mode === "opening" ? t("invite.opening") : t("state.finishing");
    return (
      <div className="flex items-center gap-3 text-sm text-muted-foreground">
        <Spinner label={doing} />
        <span>{doing}</span>
      </div>
    );
  }
  if (p.mode === "unusable") {
    return (
      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-2xl font-semibold tracking-tight text-foreground">{t("invite.unusableTitle")}</h1>
          <p className="text-sm text-muted-foreground">{t("invite.unusable")}</p>
        </div>
        <a href="/sign-in" className="self-start rounded-sm text-[13px] font-medium text-primary underline-offset-4 outline-none hover:underline focus-visible:ring-2 focus-visible:ring-ring">
          {t("invite.signInInstead")}
        </a>
      </div>
    );
  }
  const password = p.method === "password";
  const submit =
    p.mode === "verify" ? t("verify.submit")
    : p.mode === "name" ? t("name.submit")
    : p.invitation ? (p.mode === "signup" ? t("invite.submitNew") : password ? t("invite.submitPassword") : t("signIn.submitCode"))
    : p.mode === "signup" ? (password ? t("signUp.submitPassword") : t("signUp.submitCode"))
    : password ? t("signIn.submitPassword") : t("signIn.submitCode");
  return (
    <form
      className="flex flex-col gap-5"
      onSubmit={(event) => {
        event.preventDefault();
        p.onSubmit();
      }}
    >
      <div className="flex flex-col gap-1.5">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">{heading.title}</h1>
        <p className="text-sm text-muted-foreground">{heading.subtitle}</p>
      </div>
      {p.mode === "verify" ? (
        <TextField label={t("fields.code")} value={p.code} onChangeText={p.onCodeChange} inputMode="numeric" autoComplete="one-time-code" autoFocus maxLength={8} />
      ) : p.mode === "name" ? (
        <TextField label={t("fields.name")} value={p.name} onChangeText={p.onNameChange} autoComplete="name" autoFocus />
      ) : (
        <>
          {p.mode === "signup" ? <TextField label={t("fields.name")} value={p.name} onChangeText={p.onNameChange} autoComplete="name" autoFocus /> : null}
          <TextField
            label={t("fields.email")}
            value={p.email}
            onChangeText={p.onEmailChange}
            type="email"
            autoComplete="email"
            autoFocus={p.mode === "login" && !p.invitation}
            // An invitation is for one address: it is shown, not asked for.
            readOnly={p.invitation !== null}
            hint={p.invitation ? t("invite.emailHint") : undefined}
          />
          {password ? (
            <TextField label={t("fields.password")} value={p.password} onChangeText={p.onPasswordChange} type="password" autoComplete={p.mode === "signup" ? "new-password" : "current-password"} />
          ) : null}
        </>
      )}
      <ErrorNote message={p.error} />
      <Button type="submit" tone="primary" label={submit} busy={p.busy} />
      {p.mode === "verify" ? (
        <div className="flex flex-wrap gap-x-4 gap-y-1 text-[13px]">
          <LinkButton onClick={p.onResend}>{t("verify.resend")}</LinkButton>
          <LinkButton onClick={p.onRestart}>{t("verify.restart")}</LinkButton>
        </div>
      ) : p.mode === "name" ? null : (
        <LinkButton onClick={p.onSwitchMode}>{p.mode === "signup" ? t("signUp.switch") : p.invitation ? t("invite.switchNew") : t("signIn.switch")}</LinkButton>
      )}
    </form>
  );
}

function LinkButton({ onClick, children }: { onClick: () => void; children: ReactNode }) {
  return (
    <button type="button" onClick={onClick} className="self-start rounded-sm text-[13px] font-medium text-primary underline-offset-4 outline-none hover:underline focus-visible:ring-2 focus-visible:ring-ring">
      {children}
    </button>
  );
}

/** The brand at the top of the page, a link home. */
export function Brand({ href }: { href: string }) {
  const brand = useT("common")("brand.name");
  return (
    <a href={href} className="flex items-center gap-2 text-[15px] font-semibold tracking-tight text-foreground">
      <Mark />
      {brand}
    </a>
  );
}
