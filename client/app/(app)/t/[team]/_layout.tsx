import { Slot, useLocalSearchParams } from "expo-router";
import { TeamShellController } from "@/components/app/team-shell";

/** The frame around every page of a team: its tasks down the side, the page in the rest. */
export default function TeamLayout() {
  const { team } = useLocalSearchParams<{ team: string }>();
  return (
    <TeamShellController teamId={team}>
      <Slot />
    </TeamShellController>
  );
}
