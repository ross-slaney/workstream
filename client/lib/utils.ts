import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

/** A UUID the client makes for a row it creates, so a create that is tried again makes one row. */
export function newId(): string {
  return crypto.randomUUID();
}
