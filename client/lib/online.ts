/** What a screen that reads the API shows: nothing yet, the value, or why not. */
export type Online<T> = { kind: "loading" } | { kind: "failed"; message: string } | { kind: "offline" } | { kind: "ready"; value: T };

/** The value when there is one. */
export function valueOf<T>(read: Online<T>): T | null {
  return read.kind === "ready" ? read.value : null;
}
