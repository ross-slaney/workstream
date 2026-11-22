import type { ReactNode } from "react";
import { Button } from "@/components/kit/button";
import { TextField } from "@/components/kit/field";
import { ErrorNote, Spinner } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import { Mark } from "@/src/landing/landing-page";
import type { SignInProvider, SignInScreenProps } from "./props";

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
      <Providers providers={p.providers} disabled={p.busy} onProvider={p.onProvider} />
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

/** The other ways in, under the form's own button: a line, "or", and a button for each provider. Nothing where there are none. */
function Providers({ providers, disabled, onProvider }: { providers: SignInProvider[]; disabled: boolean; onProvider: (id: string) => void }) {
  const t = useT("auth");
  if (providers.length === 0) return null;
  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center gap-3 text-[13px] text-muted-foreground">
        <span className="h-px flex-1 bg-border" />
        <span>{t("provider.or")}</span>
        <span className="h-px flex-1 bg-border" />
      </div>
      {providers.map((provider) => (
        <Button
          key={provider.id}
          tone="outline"
          label={t("provider.continue", { provider: provider.name })}
          icon={provider.kind === "github" ? <GitHubMark /> : undefined}
          disabled={disabled}
          onClick={() => onProvider(provider.id)}
        />
      ))}
    </div>
  );
}

/** GitHub's mark, in the color of the words beside it. */
function GitHubMark() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" className="size-[18px] shrink-0" fill="currentColor">
      <path d="M12 1.5C6.2 1.5 1.5 6.2 1.5 12c0 4.64 3.01 8.58 7.18 9.97.52.1.71-.23.71-.5v-1.86c-2.92.64-3.54-1.24-3.54-1.24-.48-1.22-1.17-1.54-1.17-1.54-.96-.65.07-.64.07-.64 1.06.07 1.62 1.09 1.62 1.09.94 1.61 2.46 1.14 3.06.87.1-.68.37-1.14.67-1.4-2.33-.27-4.78-1.17-4.78-5.19 0-1.15.41-2.08 1.08-2.82-.11-.27-.47-1.34.1-2.78 0 0 .89-.28 2.9 1.08A10.1 10.1 0 0 1 12 6.68c.9 0 1.79.12 2.63.36 2.01-1.36 2.9-1.08 2.9-1.08.57 1.44.21 2.51.1 2.78.67.74 1.08 1.67 1.08 2.82 0 4.03-2.45 4.92-4.79 5.18.38.33.72.97.72 1.96v2.77c0 .28.19.61.72.5A10.51 10.51 0 0 0 22.5 12C22.5 6.2 17.8 1.5 12 1.5Z" />
    </svg>
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
