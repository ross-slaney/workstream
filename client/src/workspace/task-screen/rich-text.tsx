import { Fragment } from "react";

/*
 * The agent's words as it wrote them, with the two things that read badly as plain text made right: a fenced
 * block of code becomes a code block, and `inline code` is set in a fixed-width face. Nothing else is
 * interpreted, so what the agent wrote is what is shown.
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
            {block.text.trim().split(/(`[^`\n]+`)/g).map((part, at) =>
              part.length > 2 && part.startsWith("`") && part.endsWith("`") ? (
                <code key={at} className="rounded bg-muted px-1 py-0.5 font-mono text-[0.92em]">
                  {part.slice(1, -1)}
                </code>
              ) : (
                <Fragment key={at}>{part}</Fragment>
              ),
            )}
          </p>
        ),
      )}
    </>
  );
}
