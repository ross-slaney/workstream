import { useEffect, useState } from "react";
import { Button } from "@/components/kit/button";
import { Dialog } from "@/components/kit/dialog";
import { TextField } from "@/components/kit/field";
import { ErrorNote, ReadState } from "@/components/kit/status";
import { useT } from "@/lib/i18n";
import type { TeamPerson } from "@/lib/workstream/api";
import { Section, SettingsPage } from "../page";
import { ARCHIVE_DAYS, desktopTimesOf, PAUSE_MINUTES } from "./desktop-times";
import type { TeamScreenProps } from "./props";

/** The page, the same on every surface. */
export function TeamPage(p: TeamScreenProps) {
  const t = useT("settings");
  const common = useT("common");
  const admin = p.team?.admin ?? false;
  const [name, setName] = useState(p.team?.name ?? "");
  const [email, setEmail] = useState("");
  const [asAdmin, setAsAdmin] = useState(false);
  const [removing, setRemoving] = useState<TeamPerson | null>(null);
  const [copied, setCopied] = useState(false);
  useEffect(() => setName(p.team?.name ?? ""), [p.team?.name]);
  useEffect(() => setCopied(false), [p.invited?.id]);

  return (
    <SettingsPage title={t("team.title")} subtitle={t("team.subtitle")}>
      <ErrorNote message={p.error} />

      {admin ? (
        <form
          className="flex flex-wrap items-end gap-3"
          onSubmit={(event) => {
            event.preventDefault();
            if (name.trim() && name.trim() !== p.team?.name) p.onRename(name.trim());
          }}
        >
          <div className="min-w-[240px] flex-1">
            <TextField label={t("team.name")} value={name} onChangeText={setName} maxLength={100} />
          </div>
          <Button type="submit" label={t("team.rename")} busy={p.busy} disabled={!name.trim() || name.trim() === p.team?.name} />
        </form>
      ) : null}

      <Section title={t("team.people")}>
        {p.people.kind !== "ready" ? (
          <ReadState read={p.people} onRetry={p.onRetry} />
        ) : (
          <ul className="flex flex-col divide-y divide-border rounded-lg border border-border bg-card">
            {p.people.value.map((person) => {
              const me = person.userId === p.meId;
              return (
                <li key={person.userId} className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
                  <div className="flex min-w-0 flex-1 flex-col">
                    <span className="truncate text-sm font-medium text-foreground">
                      {person.name}
                      {me ? <span className="font-normal text-muted-foreground"> ({t("team.you")})</span> : null}
                    </span>
                    {person.email ? <span className="truncate text-[13px] text-muted-foreground">{person.email}</span> : null}
                  </div>
                  <span className="text-[13px] text-muted-foreground">{person.admin ? t("team.admin") : t("team.member")}</span>
                  {admin && !me ? (
                    <Button size="small" tone="quiet" label={person.admin ? t("team.removeAdmin") : t("team.makeAdmin")} onClick={() => p.onSetAdmin(person.userId, !person.admin)} />
                  ) : null}
                  {admin || me ? <Button size="small" tone="quiet" label={me ? t("team.leave") : t("team.remove")} onClick={() => setRemoving(person)} /> : null}
                </li>
              );
            })}
          </ul>
        )}
      </Section>

      {admin ? (
        <Section title={t("team.invite.title")}>
          <form
            className="flex flex-wrap items-end gap-3"
            onSubmit={(event) => {
              event.preventDefault();
              if (!email.trim()) return;
              p.onInvite(email.trim(), asAdmin);
              setEmail("");
            }}
          >
            <div className="min-w-[240px] flex-1">
              <TextField label={t("team.invite.email")} placeholder={t("team.invite.emailPlaceholder")} type="email" value={email} onChangeText={setEmail} />
            </div>
            <label className="flex h-10 items-center gap-2 text-sm text-foreground">
              <input type="checkbox" checked={asAdmin} onChange={(event) => setAsAdmin(event.target.checked)} className="size-4 accent-[rgb(var(--primary))]" />
              {t("team.invite.asAdmin")}
            </label>
            <Button type="submit" tone="primary" label={t("team.invite.send")} busy={p.busy} disabled={!email.trim()} />
          </form>

          {p.invitations.length > 0 ? (
            <div className="flex flex-col gap-2">
              <h3 className="text-[13px] font-medium text-muted-foreground">{t("team.invite.pending")}</h3>
              <ul className="flex flex-col divide-y divide-border rounded-lg border border-border bg-card">
                {p.invitations.map((invitation) => (
                  <li key={invitation.id} className="flex flex-wrap items-center gap-3 px-4 py-2.5">
                    <span className="min-w-0 flex-1 truncate text-sm text-foreground">{invitation.email}</span>
                    <span className="text-[13px] text-muted-foreground">
                      {t("team.invite.expires", { date: new Date(invitation.expiresAt).toLocaleDateString(undefined, { month: "short", day: "numeric" }) })}
                    </span>
                    <Button size="small" tone="quiet" label={t("team.invite.withdraw")} onClick={() => p.onWithdraw(invitation.id)} />
                  </li>
                ))}
              </ul>
            </div>
          ) : null}
        </Section>
      ) : (
        <p className="text-[13px] text-muted-foreground">{t("team.adminOnly")}</p>
      )}

      <DesktopsSection {...p} admin={admin} />

      <GitHubSection {...p} admin={admin} />

      <Section title={t("team.email.title")}>
        <label className="flex items-start gap-3 rounded-lg border border-border bg-card px-4 py-3">
          <input
            type="checkbox"
            checked={p.taskEmails ?? false}
            disabled={p.taskEmails === null}
            onChange={(event) => p.onTaskEmails(event.target.checked)}
            className="mt-0.5 size-4 shrink-0 accent-[rgb(var(--primary))]"
          />
          <span className="flex min-w-0 flex-col gap-1">
            <span className="text-sm text-foreground">{t("team.email.taskEmails")}</span>
            <span className="text-[13px] text-muted-foreground">{t("team.email.taskEmailsHint")}</span>
          </span>
        </label>
      </Section>

      {p.invited ? (
        <Dialog title={t("team.invite.linkTitle")} onClose={p.onDismissInvited} footer={<Button label={common("actions.close")} tone="primary" onClick={p.onDismissInvited} />}>
          <p className="text-sm text-muted-foreground">{t(p.invited.emailed ? "team.invite.emailedBody" : "team.invite.linkBody", { email: p.invited.email })}</p>
          {p.invited.link ? (
            <div className="flex items-center gap-2">
              <input readOnly value={p.invited.link} aria-label={t("team.invite.linkTitle")} onFocus={(event) => event.target.select()} className="h-10 min-w-0 flex-1 rounded-md border border-input bg-muted px-3 font-mono text-[12.5px] text-foreground outline-none" />
              <Button
                label={copied ? common("actions.copied") : common("actions.copy")}
                onClick={() => void navigator.clipboard.writeText(p.invited?.link ?? "").then(() => setCopied(true))}
              />
            </div>
          ) : null}
        </Dialog>
      ) : null}

      {removing ? (
        <Dialog
          title={removing.userId === p.meId ? t("team.leaveTitle") : t("team.removeTitle", { name: removing.name })}
          onClose={() => setRemoving(null)}
          footer={
            <>
              <Button label={common("actions.cancel")} onClick={() => setRemoving(null)} />
              <Button
                tone="danger"
                label={removing.userId === p.meId ? t("team.leave") : t("team.remove")}
                onClick={() => {
                  p.onRemove(removing.userId);
                  setRemoving(null);
                }}
              />
            </>
          }
        >
          <p className="text-sm text-muted-foreground">{removing.userId === p.meId ? t("team.leaveBody") : t("team.removeBody")}</p>
        </Dialog>
      ) : null}
    </SettingsPage>
  );
}

/**
 * When the team's desktops are paused and archived. Everyone on the team sees the two times; an administrator
 * changes them.
 */
function DesktopsSection(p: TeamScreenProps & { admin: boolean }) {
  const t = useT("settings");
  const [minutes, setMinutes] = useState(p.team ? String(p.team.pauseAfterMinutes) : "");
  const [days, setDays] = useState(p.team ? String(p.team.archiveAfterDays) : "");
  useEffect(() => setMinutes(p.team ? String(p.team.pauseAfterMinutes) : ""), [p.team?.pauseAfterMinutes]);
  useEffect(() => setDays(p.team ? String(p.team.archiveAfterDays) : ""), [p.team?.archiveAfterDays]);
  if (!p.team) return null;
  const times = desktopTimesOf(minutes, days);
  const changed = times !== null && (times.pauseAfterMinutes !== p.team.pauseAfterMinutes || times.archiveAfterDays !== p.team.archiveAfterDays);

  return (
    <Section title={t("team.desktops.title")}>
      <p className="max-w-[620px] text-sm text-muted-foreground">{t("team.desktops.body")}</p>
      {p.admin ? (
        <form
          className="flex flex-wrap items-start gap-3"
          onSubmit={(event) => {
            event.preventDefault();
            if (times && changed) p.onDesktopTimes(times.pauseAfterMinutes, times.archiveAfterDays);
          }}
        >
          <div className="min-w-[220px] flex-1">
            <TextField
              label={t("team.desktops.pauseAfter")}
              hint={t("team.desktops.pauseAfterHint", PAUSE_MINUTES)}
              type="number"
              inputMode="numeric"
              min={PAUSE_MINUTES.min}
              max={PAUSE_MINUTES.max}
              step={1}
              value={minutes}
              onChangeText={setMinutes}
            />
          </div>
          <div className="min-w-[220px] flex-1">
            <TextField
              label={t("team.desktops.archiveAfter")}
              hint={t("team.desktops.archiveAfterHint", ARCHIVE_DAYS)}
              type="number"
              inputMode="numeric"
              min={ARCHIVE_DAYS.min}
              max={ARCHIVE_DAYS.max}
              step={1}
              value={days}
              onChangeText={setDays}
            />
          </div>
          {/* Level with the fields, under a gap the height of their labels. */}
          <div className="pt-[26px]">
            <Button type="submit" label={t("team.desktops.save")} busy={p.busy} disabled={!changed} />
          </div>
        </form>
      ) : (
        <p className="rounded-lg border border-border bg-card px-4 py-3 text-sm text-foreground">
          {t("team.desktops.now", { minutes: p.team.pauseAfterMinutes, days: p.team.archiveAfterDays })}
        </p>
      )}
    </Section>
  );
}

const shortDate = (at: string) => new Date(at).toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" });

/** The team's GitHub connection: what it is connected to and what it reaches, or connecting it. */
function GitHubSection(p: TeamScreenProps & { admin: boolean }) {
  const t = useT("settings");
  const common = useT("common");
  const [disconnecting, setDisconnecting] = useState(false);
  const github = p.github.kind === "ready" ? p.github.value : null;

  return (
    <Section title={t("team.github.title")}>
      {!github ? (
        <ReadState read={p.github} onRetry={p.onRetryGitHub} />
      ) : !github.available ? (
        <p className="text-[13px] text-muted-foreground">{t("team.github.notSetUp")}</p>
      ) : !github.connected ? (
        <div className="flex flex-col items-start gap-3 rounded-lg border border-border bg-card px-4 py-4">
          <p className="max-w-[620px] text-sm text-muted-foreground">{t("team.github.body")}</p>
          {p.admin ? (
            <Button tone="primary" label={t("team.github.connect")} busy={p.busy} onClick={p.onConnectGitHub} />
          ) : (
            <p className="text-[13px] text-muted-foreground">{t("team.github.adminOnly")}</p>
          )}
        </div>
      ) : (
        <div className="flex flex-col divide-y divide-border rounded-lg border border-border bg-card">
          <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3">
            <div className="flex min-w-0 flex-1 flex-col">
              <span className="truncate text-sm font-medium text-foreground">{t("team.github.connectedTo", { account: github.account ?? "" })}</span>
              <span className="truncate text-[13px] text-muted-foreground">
                {github.accountType === "Organization" ? t("team.github.organization") : t("team.github.personal")}
                {github.connectedAt
                  ? ` · ${github.connectedByName ? t("team.github.connectedBy", { name: github.connectedByName, date: shortDate(github.connectedAt) }) : t("team.github.connectedOn", { date: shortDate(github.connectedAt) })}`
                  : null}
              </span>
            </div>
            {p.admin && github.manageUrl ? (
              <a
                href={github.manageUrl}
                target="_blank"
                rel="noreferrer"
                className="inline-flex h-8 items-center rounded-md px-3 text-[13px] font-medium text-foreground outline-none hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring"
              >
                {t("team.github.manage")}
              </a>
            ) : null}
            {p.admin ? <Button size="small" tone="quiet" label={t("team.github.disconnect")} onClick={() => setDisconnecting(true)} /> : null}
          </div>
          <div className="flex flex-col gap-2 px-4 py-3">
            <h3 className="text-[13px] font-medium text-muted-foreground">{t("team.github.repositories")}</h3>
            {!p.githubRepositories || p.githubRepositories.kind !== "ready" ? (
              p.githubRepositories ? <ReadState read={p.githubRepositories} onRetry={p.onRetryGitHub} /> : null
            ) : p.githubRepositories.value.length === 0 ? (
              <p className="text-[13px] text-muted-foreground">{t("team.github.noRepositories")}</p>
            ) : (
              <ul className="flex flex-col gap-1">
                {p.githubRepositories.value.map((repository) => (
                  <li key={repository.fullName} className="flex items-center gap-2 text-sm text-foreground">
                    <span className="min-w-0 truncate font-mono text-[13px]">{repository.fullName}</span>
                    {repository.private ? <span className="text-[12px] text-muted-foreground">{t("team.github.private")}</span> : null}
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      )}

      {disconnecting && github ? (
        <Dialog
          title={t("team.github.disconnectTitle")}
          onClose={() => setDisconnecting(false)}
          footer={
            <>
              <Button label={common("actions.cancel")} onClick={() => setDisconnecting(false)} />
              <Button
                tone="danger"
                label={t("team.github.disconnect")}
                onClick={() => {
                  p.onDisconnectGitHub();
                  setDisconnecting(false);
                }}
              />
            </>
          }
        >
          <p className="text-sm text-muted-foreground">{t("team.github.disconnectBody", { account: github.account ?? "" })}</p>
        </Dialog>
      ) : null}
    </Section>
  );
}
