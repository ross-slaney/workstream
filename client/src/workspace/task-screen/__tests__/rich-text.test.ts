import { describe, expect, it } from "vitest";
import { blocksOf, piecesOf } from "../rich-text";

describe("the agent's words", () => {
  it("keeps a fenced block of code apart from the words around it", () => {
    expect(blocksOf("Run this:\n```bash\nnpm test\n```\nThen look.")).toEqual([
      { code: false, text: "Run this:\n" },
      { code: true, text: "npm test" },
      { code: false, text: "\nThen look." },
    ]);
    // A block the agent is still writing has no closing fence yet.
    expect(blocksOf("```\nhalf")).toEqual([{ code: true, text: "half" }]);
  });

  it("sets inline code and bold apart, and leaves everything else as written", () => {
    expect(piecesOf("The page is `index.html`, and has:\n- **Header:** a gradient")).toEqual([
      { kind: "text", text: "The page is " },
      { kind: "code", text: "index.html" },
      { kind: "text", text: ", and has:\n- " },
      { kind: "bold", text: "Header:" },
      { kind: "text", text: " a gradient" },
    ]);
    expect(piecesOf("2 ** 3 is 8, and a * b ** c")).toEqual([{ kind: "text", text: "2 ** 3 is 8, and a * b ** c" }]);
    expect(piecesOf("`**not bold**`")).toEqual([{ kind: "code", text: "**not bold**" }]);
  });

  it("reads a heading line as a bold line", () => {
    expect(piecesOf("## What I did\nI ran the tests.")).toEqual([
      { kind: "bold", text: "What I did" },
      { kind: "text", text: "\nI ran the tests." },
    ]);
    expect(piecesOf("Issue #12 is fixed")).toEqual([{ kind: "text", text: "Issue #12 is fixed" }]);
  });
});
