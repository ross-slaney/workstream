import { useEffect, useRef, useState } from "react";
import { workstream, type Message } from "@/lib/workstream/api";

type Kept = { asked: Set<number>; made: string[]; open: boolean };

/**
 * The pictures of a conversation as addresses an image can show, by message id. Each is read once, with the
 * person's token, when its message arrives, and kept while the screen is open on this task. A picture that
 * cannot be read stays without an address, and the view draws its place empty.
 */
export function usePictures(taskId: string, messages: Message[]): ReadonlyMap<number, string> {
  const [pictures, setPictures] = useState<ReadonlyMap<number, string>>(new Map());
  const kept = useRef<Kept>({ asked: new Set(), made: [], open: false });

  useEffect(() => {
    const mine: Kept = { asked: new Set(), made: [], open: true };
    kept.current = mine;
    setPictures(new Map());
    return () => {
      mine.open = false;
      for (const address of mine.made) URL.revokeObjectURL(address);
    };
  }, [taskId]);

  useEffect(() => {
    const mine = kept.current;
    for (const message of messages) {
      if (message.kind !== "picture" || mine.asked.has(message.id)) continue;
      mine.asked.add(message.id);
      workstream
        .picture(taskId, message.id)
        .then((blob) => {
          if (!mine.open) return;
          const address = URL.createObjectURL(blob);
          mine.made.push(address);
          setPictures((current) => new Map(current).set(message.id, address));
        })
        .catch(() => {
          // Left without a picture.
        });
    }
  }, [taskId, messages]);

  return pictures;
}
