import { useRouter } from "expo-router";
import Head from "expo-router/head";
import { useState } from "react";
import { useTeamShell } from "@/components/app/team-shell";
import { useAction } from "@/hooks/use-action";
import { goToGitHub } from "@/lib/github-connect";
import { useOnlineRead } from "@/hooks/use-online-read";
import { useT } from "@/lib/i18n";
import { valueOf } from "@/lib/online";
import { useSession } from "@/lib/session";
import { workstream, type PendingInvitation } from "@/lib/workstream/api";
import { TeamScreen } from "@/src/settings/team-screen";

/** /t/{team}/team: who is on the team, inviting someone, when its desktops are paused and archived, and the team's GitHub connection. */
export default function TeamRoute() {
  const t = useT("settings");
  const router = useRouter();
  const { teamId } = useTeamShell();
  const session = useSession();
  const team = useOnlineRead(() => workstream.team(teamId), `team:${teamId}`);
  const people = useOnlineRead(() => workstream.people(teamId), `people:${teamId}`);
  const admin = valueOf(team)?.admin ?? false;
  // Only an administrator may read invitations; anyone else has none to see.
  const invitations = useOnlineRead(() => (admin ? workstream.invitations(teamId) : Promise.resolve([])), `invitations:${teamId}:${admin}`);
  // The person's own, the same on every team.
  const settings = useOnlineRead(() => workstream.mySettings(), "my-settings");
  const github = useOnlineRead(() => workstream.github(teamId), `github:${teamId}`);
  const connected = valueOf(github)?.connected ?? false;
  // What the connection reaches is read from GitHub, so only once there is one.
  const repositories = useOnlineRead(() => (connected ? workstream.githubRepositories(teamId) : Promise.resolve([])), `github-repositories:${teamId}:${connected}`);
  const [invited, setInvited] = useState<PendingInvitation | null>(null);
  const action = useAction();
  const then = (refresh: () => void) => (run: () => Promise<unknown>) => void action.run(run).then(refresh);

  return (
    <>
      <Head>
        <title>{t("team.metaTitle")}</title>
      </Head>
      <TeamScreen
        team={valueOf(team)}
        people={people}
        github={github}
        githubRepositories={connected ? repositories : null}
        onRetryGitHub={() => {
          github.reload();
          repositories.reload();
        }}
        onConnectGitHub={() =>
          void action.run(async () => {
            const { url } = await workstream.startGitHubInstall(teamId);
            goToGitHub(teamId, url);
          })
        }
        onDisconnectGitHub={() => then(github.refresh)(() => workstream.disconnectGitHub(teamId))}
        invitations={valueOf(invitations) ?? []}
        meId={session?.userId ?? ""}
        invited={invited}
        taskEmails={valueOf(settings)?.taskEmails ?? null}
        busy={action.busy}
        error={action.error}
        onRetry={people.reload}
        onRename={(name) => then(team.refresh)(() => workstream.renameTeam(teamId, name))}
        onDesktopTimes={(pauseAfterMinutes, archiveAfterDays) => then(team.refresh)(() => workstream.setDesktopTimes(teamId, pauseAfterMinutes, archiveAfterDays))}
        onInvite={(email, asAdmin) => then(invitations.refresh)(async () => setInvited(await workstream.invite(teamId, email, asAdmin)))}
        onDismissInvited={() => setInvited(null)}
        onTaskEmails={(on) => void action.run(async () => settings.replace(await workstream.changeMySettings({ taskEmails: on })))}
        onWithdraw={(invitationId) => then(invitations.refresh)(() => workstream.revokeInvitation(teamId, invitationId))}
        onSetAdmin={(userId, makeAdmin) => then(people.refresh)(() => workstream.setAdmin(teamId, userId, makeAdmin))}
        onRemove={(userId) =>
          void action.run(async () => {
            await workstream.removePerson(teamId, userId);
            // Leaving: this team is no longer theirs to look at.
            if (userId === session?.userId) router.replace("/home");
            else people.refresh();
          })
        }
      />
    </>
  );
}
