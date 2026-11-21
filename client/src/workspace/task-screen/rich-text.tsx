import { Fragment } from "react";

/*
 * The agent's words as it wrote them, with the few things that read badly as plain text made right: a fenced
 * block of code becomes a code block, `inline code` is set in a fixed-width face, **bold** is bold, and a
 * heading line is a bold line. Nothing else is interpreted: lists, links and the rest are shown as written.
 */

type Block = { code: boolean; text: string };

export function blocksOf(text: string): Block[] {
  const blocks: Block[] = [];
  const fence = /```[^\n]*\n([\s\S]*?)(?:```|$)/g;
  let at = 0;
  for (let match = fence.exec(text); match; match = fence.exec(text)) {
    if (match.index > at) blocks.push({ code: false, text: text.slice(at, match.index) });
    blocks.push({ code: true, text: match[1].replace(/\n$/, "") });
    at = match.index + match[0].length;
  }
  if (at < text.length) blocks.push({ code: false, text: text.slice(at) });
  return blocks.filter((block) => block.text.trim().length > 0);
}

export type Piece = { kind: "text" | "code" | "bold"; text: string };

/** A paragraph in pieces: plain words, inline code and bold. A heading line counts as bold. */
export function piecesOf(text: string): Piece[] {
  const headed = text.replace(/^#{1,4}[ \t]+(.+?)[ \t]*$/gm, (_, title: string) => `**${title.replaceAll("**", "")}**`);
  return headed
    .split(/(`[^`\n]+`|\*\*[^\s*](?:[^\n]*?[^\s*])?\*\*)/g)
    .filter((part) => part.length > 0)
    .map((part): Piece => {
      if (part.length > 2 && part.startsWith("`") && part.endsWith("`")) return { kind: "code", text: part.slice(1, -1) };
      if (part.length > 4 && part.startsWith("**") && part.endsWith("**")) return { kind: "bold", text: part.slice(2, -2) };
      return { kind: "text", text: part };
    });
}

export function RichText({ text }: { text: string }) {
  return (
    <>
      {blocksOf(text).map((block, index) =>
        block.code ? (
          <pre key={index} className="my-1 overflow-x-auto rounded-md bg-muted px-3 py-2 font-mono text-[12.5px] leading-relaxed text-foreground">
            {block.text}
          </pre>
        ) : (
          <p key={index} className="whitespace-pre-wrap break-words">
            {piecesOf(block.text.trim()).map((piece, at) =>
              piece.kind === "code" ? (
                <code key={at} className="rounded bg-muted px-1 py-0.5 font-mono text-[0.92em]">
                  {piece.text}
                </code>
              ) : piece.kind === "bold" ? (
                <strong key={at} className="font-semibold">
                  {piece.text}
                </strong>
              ) : (
                <Fragment key={at}>{piece.text}</Fragment>
              ),
            )}
          </p>
        ),
      )}
    </>
  );
}
