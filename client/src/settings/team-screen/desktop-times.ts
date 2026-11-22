/** What a team may choose (api/Domain/Team.cs has the same bounds, and is what refuses). */
export const PAUSE_MINUTES = { min: 5, max: 240 } as const;
export const ARCHIVE_DAYS = { min: 1, max: 90 } as const;

/**
 * The two numbers as typed, when they are whole numbers a team may choose: minutes without use before a
 * desktop is paused, and days paused before it is archived. Null while either is not.
 */
export function desktopTimesOf(pauseAfterMinutes: string, archiveAfterDays: string): { pauseAfterMinutes: number; archiveAfterDays: number } | null {
  const minutes = whole(pauseAfterMinutes);
  const days = whole(archiveAfterDays);
  if (minutes === null || days === null) return null;
  if (minutes < PAUSE_MINUTES.min || minutes > PAUSE_MINUTES.max || days < ARCHIVE_DAYS.min || days > ARCHIVE_DAYS.max) return null;
  return { pauseAfterMinutes: minutes, archiveAfterDays: days };
}

function whole(text: string): number | null {
  const trimmed = text.trim();
  return /^\d{1,4}$/.test(trimmed) ? Number(trimmed) : null;
}
