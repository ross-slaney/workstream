import { describe, expect, it } from "vitest";
import { gridFor, parseOpen, withTask } from "../layout";

describe("how screens share the space", () => {
  it("gives one screen everything", () => {
    expect(gridFor(1, 1600, 900)).toEqual({ columns: 1, rows: 1 });
  });

  it("puts two screens side by side in a very wide window and one above the other otherwise", () => {
    expect(gridFor(2, 3440, 1000)).toEqual({ columns: 2, rows: 1 });
    expect(gridFor(2, 1400, 1000)).toEqual({ columns: 1, rows: 2 });
  });

  it("puts four screens two by two", () => {
    expect(gridFor(4, 1600, 900)).toEqual({ columns: 2, rows: 2 });
  });

  it("never asks for more than four", () => {
    expect(gridFor(9, 1600, 900)).toEqual({ columns: 2, rows: 2 });
  });
});

describe("the screens in the address", () => {
  it("reads them in order, without repeats, four at most", () => {
    expect(parseOpen("a,b,a, c ,d,e")).toEqual(["a", "b", "c", "d"]);
    expect(parseOpen(undefined)).toEqual([]);
    expect(parseOpen(["a", "b"])).toEqual(["a", "b"]);
  });
});

describe("dropping a task on the screens", () => {
  it("adds it while there is room", () => {
    expect(withTask(["a"], "b", null)).toEqual(["a", "b"]);
  });

  it("leaves a task that is already open where it is", () => {
    expect(withTask(["a", "b"], "a", 1)).toEqual(["a", "b"]);
  });

  it("replaces the screen it was dropped on when four are open", () => {
    expect(withTask(["a", "b", "c", "d"], "e", 1)).toEqual(["a", "e", "c", "d"]);
    expect(withTask(["a", "b", "c", "d"], "e", null)).toEqual(["a", "b", "c", "e"]);
  });
});
