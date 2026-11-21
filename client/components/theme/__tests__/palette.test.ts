import { describe, expect, it } from "vitest";
import { contrastRatio, palettes, type ColorToken } from "../palette";

/** Text on a surface, as the app pairs them. Each must read at WCAG AA (4.5:1) in both schemes. */
const pairs: [ColorToken, ColorToken][] = [
  ["foreground", "background"],
  ["foreground", "canvas"],
  ["foreground", "muted"],
  ["cardForeground", "card"],
  ["mutedForeground", "background"],
  ["mutedForeground", "canvas"],
  ["mutedForeground", "card"],
  ["primaryForeground", "primary"],
  ["primary", "background"],
  ["primary", "primarySoft"],
  ["destructiveForeground", "destructive"],
  ["destructive", "background"],
  ["positive", "background"],
  ["positive", "canvas"],
  ["warning", "background"],
  ["warning", "canvas"],
  ["stageForeground", "stage"],
];

describe("the palette", () => {
  for (const scheme of ["light", "dark"] as const) {
    for (const [text, surface] of pairs) {
      it(`${scheme}: ${text} reads on ${surface}`, () => {
        expect(contrastRatio(palettes[scheme][text], palettes[scheme][surface])).toBeGreaterThanOrEqual(4.5);
      });
    }
  }
});
