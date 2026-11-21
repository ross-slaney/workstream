/*
 * How the open screens share the space. A screen is a conversation beside a desktop, and a desktop's picture
 * keeps its shape, so the best split is the one that gives each desktop the widest picture: two screens sit
 * side by side in a wide window and one above the other in a tall one.
 */

export const MAX_SCREENS = 4;

/** The part of a screen's width its desktop gets; the conversation has the rest. */
export const DESKTOP_SHARE = 0.75;
/** A desktop's shape, width over height. */
const PICTURE = 16 / 9;
/** The bar above each screen. */
const HEADER = 44;

export type Grid = { columns: number; rows: number };

function splits(count: number): Grid[] {
  switch (count) {
    case 1:
      return [{ columns: 1, rows: 1 }];
    case 2:
      return [{ columns: 2, rows: 1 }, { columns: 1, rows: 2 }];
    case 3:
      return [{ columns: 2, rows: 2 }, { columns: 3, rows: 1 }, { columns: 1, rows: 3 }];
    default:
      return [{ columns: 2, rows: 2 }, { columns: 4, rows: 1 }, { columns: 1, rows: 4 }];
  }
}

/** How wide a desktop's picture is in one cell of this split. */
function pictureWidth(grid: Grid, width: number, height: number): number {
  const paneWidth = (width / grid.columns) * DESKTOP_SHARE;
  const paneHeight = height / grid.rows - HEADER;
  return Math.max(0, Math.min(paneWidth, paneHeight * PICTURE));
}

/** The split for this many screens in a space of this size. The first split listed wins a tie. */
export function gridFor(count: number, width: number, height: number): Grid {
  const options = splits(Math.min(Math.max(count, 1), MAX_SCREENS));
  return options.reduce((best, option) => (pictureWidth(option, width, height) > pictureWidth(best, width, height) ? option : best));
}

/** The tasks on screen, from the address (`?open=a,b`): no repeats, four at most. */
export function parseOpen(value: string | string[] | undefined): string[] {
  const text = Array.isArray(value) ? value.join(",") : (value ?? "");
  return [...new Set(text.split(",").map((id) => id.trim()).filter(Boolean))].slice(0, MAX_SCREENS);
}

/**
 * The screens after a task is dropped on them. A task already open stays where it is. With room, it is added
 * at the end; when four are open it takes the place of the screen it was dropped on (the last, when it was
 * dropped between them).
 */
export function withTask(open: string[], taskId: string, onto: number | null): string[] {
  if (open.includes(taskId)) return open;
  if (open.length < MAX_SCREENS) return [...open, taskId];
  const at = onto !== null && onto >= 0 && onto < open.length ? onto : open.length - 1;
  return open.map((id, index) => (index === at ? taskId : id));
}
