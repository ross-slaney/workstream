import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Team } from "@/lib/workstream/api";
import { TeamPage } from "../parts";
import type { TeamScreenProps } from "../props";

// The icons and the theme come from React Native packages, which Node cannot load; the views only need them to be there.
vi.mock("lucide-react-native", () => {
  const Icon = () => null;
  return { ArrowUp: Icon, Check: Icon, Maximize2: Icon, Minimize2: Icon, MoreHorizontal: Icon, Square: Icon, X: Icon };
});
vi.mock("@/components/theme/theme", () => ({ useTheme: () => ({ colors: new Proxy({}, { get: () => "#000000" }) }) }));

/* When a team's desktops are paused and archived: everyone on the team sees the two times, an administrator changes them. */

const nothing = () => {};
const team = (admin: boolean): Team => ({ id: "team", name: "Ada's team", admin, modelAllowanceUsd: 20, maxRunningDesktops: 2, pauseAfterMinutes: 45, archiveAfterDays: 14 });
const page = (admin: boolean) =>
  renderToStaticMarkup(
    <TeamPage
      {...({
        team: team(admin), people: { kind: "ready", value: [] }, github: { kind: "loading" }, githubRepositories: null, onRetryGitHub: nothing, onConnectGitHub: nothing,
        onDisconnectGitHub: nothing, invitations: [], meId: "me", invited: null, taskEmails: true, busy: false, error: null, onRetry: nothing, onRename: nothing,
        onDesktopTimes: nothing, onInvite: nothing, onDismissInvited: nothing, onWithdraw: nothing, onSetAdmin: nothing, onRemove: nothing, onTaskEmails: nothing,
      } as unknown as TeamScreenProps)}
    />,
  );

describe("the team's desktop times", () => {
  it("are two fields an administrator changes, holding what the team has now", () => {
    const drawn = page(true);
    expect(drawn).toContain("Pause after, in minutes without use");
    expect(drawn).toContain("Archive after, in days paused");
    expect(drawn).toMatch(/<input[^>]*type="number"[^>]*value="45"/);
    expect(drawn).toMatch(/<input[^>]*type="number"[^>]*value="14"/);
    expect(drawn).toContain("Between 5 and 240 minutes.");
    expect(drawn).toContain("Between 1 and 90 days.");
  });

  it("are a sentence for a member, with nothing to change", () => {
    const drawn = page(false);
    expect(drawn).toContain("Paused after 45 minutes without use, and archived after 14 days paused. An administrator can change this.");
    expect(drawn).not.toContain('type="number"');
  });
});
