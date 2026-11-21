// A saved setup, as the desktop sees it. The machine that prepares an environment leaves one file behind,
// ~/.workstream/prepared.json, saying when it was prepared and at which commit; a task's machine made from
// the save finds it there, so the disk itself says what it is.

/** How long ago, in the one unit that says it: "3 minutes ago", "5 hours ago", "2 days ago". */
export function ago(from, now = new Date()) {
  const minutes = Math.max(0, Math.floor((new Date(now).getTime() - new Date(from).getTime()) / 60_000));
  const unit = (count, word) => `${count} ${word}${count === 1 ? "" : "s"} ago`;
  if (minutes < 1) return "just now";
  if (minutes < 60) return unit(minutes, "minute");
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return unit(hours, "hour");
  return unit(Math.floor(hours / 24), "day");
}

/**
 * The task's first line when its desktop started from a saved setup: when it was prepared, where the
 * repository was then, and how far the branch has moved since.
 * "Started from the setup prepared 2 days ago (main at a1b2c3d), 14 commits newer now."
 */
export function startedFromLine({ preparedAt, commit, branch, newer }, now = new Date()) {
  const when = `Started from the setup prepared ${ago(preparedAt, now)}`;
  if (!commit) return `${when}.`;
  const where = `${branch ? `${branch} at ` : ""}${commit.slice(0, 7)}`;
  if (newer === null || newer === undefined) return `${when} (${where}).`;
  if (newer === 0) return `${when} (${where}), nothing newer since.`;
  return `${when} (${where}), ${newer} ${newer === 1 ? "commit" : "commits"} newer now.`;
}

/** The last part of what the setup wrote, as much as the API keeps. */
export const tail = (text, length = 4000) => (text.length <= length ? text : text.slice(-length));

/** Bytes in use on the disk, from `df -B1 --output=used /`: what a saved setup is billed for. */
export function usedBytes(dfOutput) {
  const line = String(dfOutput ?? "").trim().split("\n").at(-1) ?? "";
  const used = Number(line.trim());
  return Number.isFinite(used) && used > 0 ? used : 0;
}
