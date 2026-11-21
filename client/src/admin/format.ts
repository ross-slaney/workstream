/* How the administrator's page writes its numbers. English only, like the page. */

export const dollars = (value: number) => value.toLocaleString("en-US", { style: "currency", currency: "USD" });

export const hours = (value: number) => `${(Math.round(value * 10) / 10).toLocaleString("en-US")} h`;

/** How long ago, in the two largest units that say it. */
export function since(from: string, now: number): string {
  const minutes = Math.max(0, Math.floor((now - new Date(from).getTime()) / 60_000));
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min`;
  const wholeHours = Math.floor(minutes / 60);
  if (wholeHours < 24) return `${wholeHours} h ${minutes % 60} min`;
  return `${Math.floor(wholeHours / 24)} d ${wholeHours % 24} h`;
}
