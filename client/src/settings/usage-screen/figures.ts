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
