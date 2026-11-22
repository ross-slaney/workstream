/** The last lines of what the setup script wrote, without the empty lines it ended on. */
export function lastLines(output: string | null, count: number) {
  return (output ?? "").trimEnd().split("\n").slice(-count).join("\n");
}
