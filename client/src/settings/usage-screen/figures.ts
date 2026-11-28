/* How the Usage page rounds what it shows. The API answers to more places than a person reads. */

/** Hours to a tenth. */
export const hoursShown = (hours: number) => Math.round(hours * 10) / 10;

/** Gigabytes to a tenth: a disk is whole gigabytes, what is on a save or an archive rarely is. */
export const gbShown = (gb: number) => Math.round(gb * 10) / 10;

/**
 * GB-months to a tenth, or to a hundredth while there is less than one, so a disk kept for a few minutes or
 * a small save does not read as nothing.
 */
export const gbMonthsShown = (gbMonths: number) => (gbMonths < 1 ? Math.round(gbMonths * 100) / 100 : Math.round(gbMonths * 10) / 10);

const money = (value: number) => value.toLocaleString("en-US", { style: "currency", currency: "USD" });

/** Dollars to the cent. An amount under a cent that is not nothing says so, where rounding would call it $0.00. */
export const dollarsShown = (usd: number) => (usd > 0 && usd < 0.01 ? `<${money(0.01)}` : money(usd));

/** Tokens as a person reads them: 1.2M, 340K. */
export const tokensShown = (tokens: number) => new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 }).format(tokens);

/** A deployment's name as a person says it: "claude-opus-5-5" is "Claude Opus 5.5". A name that is not a family and a version stays as it is. */
export function modelShown(id: string) {
  const words = id.split("-");
  const version: string[] = [];
  while (words.length > 1 && /^\d+$/.test(words[words.length - 1])) version.unshift(words.pop()!);
  const name = words.map((word) => word.charAt(0).toUpperCase() + word.slice(1)).join(" ");
  return version.length > 0 ? `${name} ${version.join(".")}` : name;
}

/** How tall each day's bar is, as a share of the tallest. A day with any use is tall enough to see. */
export const barShares = (amounts: number[]) => {
  const most = Math.max(0, ...amounts);
  return amounts.map((amount) => (most > 0 && amount > 0 ? Math.max(amount / most, 0.04) : 0));
};
