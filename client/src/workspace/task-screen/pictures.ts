import type { ChatEntry } from "./props";

/** What the conversation draws one after another: an entry, or the pictures that came together, side by side. */
export type ChatRow = { kind: "entry"; id: string; entry: ChatEntry } | { kind: "pictures"; id: string; pictures: ChatEntry[] };

/**
 * The conversation's rows. The desktop adds a picture of each surface when the agent hands work over, one
 * message a picture, so pictures that follow each other are one row.
 */
export function rowsOf(entries: ChatEntry[]): ChatRow[] {
  const rows: ChatRow[] = [];
  for (const entry of entries) {
    const last = rows.at(-1);
    if (entry.kind !== "picture") rows.push({ kind: "entry", id: entry.id, entry });
    else if (last?.kind === "pictures") last.pictures.push(entry);
    else rows.push({ kind: "pictures", id: entry.id, pictures: [entry] });
  }
  return rows;
}
