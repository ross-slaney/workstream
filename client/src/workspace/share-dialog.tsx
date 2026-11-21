import { useEffect, useState } from "react";
import { Button } from "@/components/kit/button";
import { Dialog } from "@/components/kit/dialog";
import { SelectField, TextField } from "@/components/kit/field";
import { ErrorNote, ReadState } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import type { Online } from "@/lib/online";
import type { ShareLength, ShareRole, TaskAccessList, TaskShared } from "@/lib/workstream/api";

export type ShareDialogProps = {
  title: string;
  /** Who has the task: its team, and each person and invitation it is shared with. */
  access: Online<TaskAccessList>;
  busy: boolean;
  /** Why the last action was refused, in the server's words. */
  error: string | null;
  /** What the last share made, with the link to send: shown until another share or the dialog closes. */
  shared: TaskShared | null;
  onShare: (email: string, role: ShareRole, length: ShareLength) => void;
  onRevoke: (userId: string) => void;
  onWithdraw: (invitationId: string) => void;
  onRetry: () => void;
  onClose: () => void;
};

const ROLES: ShareRole[] = ["watcher", "prompter", "driver"];
const LENGTHS: ShareLength[] = ["hour", "day", "week", "never"];

/**
 * Sharing one task with people outside its team: an address, what they may do and for how long; then who
 * has it, each with a way to take it back. The same on every surface: a dialog over the screens.
 */
export function ShareDialog(p: ShareDialogProps) {
  const t = useT("workspace");
  const common = useT("common");
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<ShareRole>("watcher");
  const [length, setLength] = useState<ShareLength>("day");
  const [copied, setCopied] = useState(false);
  useEffect(() => setCopied(false), [p.shared?.link]);
  const until = (moment: string | null) =>
    moment ? t("share.until", { date: new Date(moment).toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }) }) : t("share.untilTakenBack");

  return (
    <Dialog title={t("share.title", { title: p.title })} onClose={p.onClose} wide footer={<Button label={common("actions.close")} onClick={p.onClose} />}>
      <p className="text-sm text-muted-foreground">{t("share.body")}</p>
      <form
        className="flex flex-col gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          if (!email.trim()) return;
          p.onShare(email.trim(), role, length);
          setEmail("");
        }}
      >
        <TextField label={t("share.email")} placeholder={t("share.emailPlaceholder")} type="email" value={email} onChangeText={setEmail} />
        <div className="flex flex-wrap items-end gap-3">
          <div className="min-w-[220px] flex-1">
            <SelectField label={t("share.role")} value={role} onChangeValue={(value) => setRole(value as ShareRole)} options={ROLES.map((value) => ({ value, label: t(`share.roles.${value}`) }))} />
          </div>
          <div className="min-w-[160px]">
            <SelectField label={t("share.length")} value={length} onChangeValue={(value) => setLength(value as ShareLength)} options={LENGTHS.map((value) => ({ value, label: t(`share.lengths.${value}`) }))} />
          </div>
          <Button type="submit" tone="primary" label={t("share.send")} busy={p.busy} disabled={!email.trim()} />
        </div>
      </form>
      <ErrorNote message={p.error} />

      {p.shared ? (
        <div className="flex flex-col gap-2 rounded-lg border border-border bg-muted/50 p-3">
          <p className="text-sm text-foreground">
            {p.shared.person
              ? t(p.shared.emailed ? "share.grantedEmailed" : "share.granted", { name: p.shared.person.name })
              : t(p.shared.emailed ? "share.invitedEmailed" : "share.invited", { email: p.shared.invitation?.email ?? "" })}
          </p>
          <div className="flex items-center gap-2">
            <input readOnly value={p.shared.link} aria-label={t("share.link")} onFocus={(event) => event.target.select()} className="h-10 min-w-0 flex-1 rounded-md border border-input bg-background px-3 font-mono text-[12.5px] text-foreground outline-none" />
            <Button label={copied ? common("actions.copied") : common("actions.copy")} onClick={() => void navigator.clipboard.writeText(p.shared?.link ?? "").then(() => setCopied(true))} />
          </div>
        </div>
      ) : null}

      <section className="flex flex-col gap-2">
        <h3 className="text-[13px] font-medium text-muted-foreground">{t("share.who")}</h3>
        {p.access.kind !== "ready" ? (
          <ReadState read={p.access} onRetry={p.onRetry} />
        ) : (
          <ul className="flex flex-col divide-y divide-border rounded-lg border border-border bg-card">
            <li className="flex flex-wrap items-center gap-3 px-4 py-2.5">
              <span className="min-w-0 flex-1 truncate text-sm font-medium text-foreground">{t("share.team", { team: p.access.value.teamName })}</span>
              <span className="text-[13px] text-muted-foreground">{t("share.teamCan")}</span>
            </li>
            {p.access.value.people.map((person) => (
              <li key={person.userId} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5">
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className="truncate text-sm text-foreground">{person.name}</span>
                  {person.email ? <span className="truncate text-[12.5px] text-muted-foreground">{person.email}</span> : null}
                </span>
                <span className="text-[13px] text-muted-foreground">
                  {t(`share.roles.${person.role}`)} · {until(person.until)}
                </span>
                <Button size="small" tone="quiet" label={t("share.revoke")} onClick={() => p.onRevoke(person.userId)} />
              </li>
            ))}
            {p.access.value.invitations.map((invitation) => (
              <li key={invitation.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5">
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className="truncate text-sm text-foreground">{invitation.email}</span>
                  <span className="text-[12.5px] text-muted-foreground">{t("share.waiting")}</span>
                </span>
                <span className="text-[13px] text-muted-foreground">
                  {t(`share.roles.${invitation.role}`)} · {until(invitation.until)}
                </span>
                <Button size="small" tone="quiet" label={t("share.withdraw")} onClick={() => p.onWithdraw(invitation.id)} />
              </li>
            ))}
          </ul>
        )}
      </section>
    </Dialog>
  );
}
