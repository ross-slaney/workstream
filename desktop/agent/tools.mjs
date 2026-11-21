// The desktop's own tools: how the agent looks at the screen and uses the mouse and keyboard, for anything that
// is not a web page (the browser has its own tools, which read the page instead of pixels). Each is a thin
// call to xdotool or scrot on the display the person is watching, so everything the agent does here is seen.

import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { promisify } from "node:util";
import { createSdkMcpServer, tool } from "@anthropic-ai/claude-agent-sdk";
import { z } from "zod";

const run = promisify(execFile);
const display = { ...process.env, DISPLAY: process.env.DISPLAY ?? ":1" };
const x = (args) => run("xdotool", args, { env: display, timeout: 15_000 });
const done = (text) => ({ content: [{ type: "text", text }] });

const BUTTONS = { left: "1", middle: "2", right: "3" };
const SHOT = "/tmp/workstream-screen.jpg";

export function desktopTools() {
  return createSdkMcpServer({
    name: "desktop",
    version: "1.0.0",
    tools: [
      tool("screenshot", "Take a picture of the whole desktop screen. Look before you click: coordinates in the other tools are pixels in this picture, from the top left.", {}, async () => {
        await run("scrot", ["--overwrite", "--quality", "70", SHOT], { env: display, timeout: 15_000 });
        return { content: [{ type: "image", data: (await readFile(SHOT)).toString("base64"), mimeType: "image/jpeg" }] };
      }),
      tool("click", "Move the mouse to a point on the screen and click.", {
        x: z.number().int().min(0).describe("Pixels from the left edge."),
        y: z.number().int().min(0).describe("Pixels from the top edge."),
        button: z.enum(["left", "middle", "right"]).default("left"),
        double: z.boolean().default(false).describe("Double-click."),
      }, async (args) => {
        await x(["mousemove", String(args.x), String(args.y), "click", "--repeat", args.double ? "2" : "1", BUTTONS[args.button]]);
        return done(`Clicked at ${args.x}, ${args.y}.`);
      }),
      tool("type", "Type text on the keyboard, into whatever has focus.", {
        text: z.string().max(5000),
      }, async (args) => {
        await x(["type", "--delay", "8", "--", args.text]);
        return done("Typed.");
      }),
      tool("key", "Press a key or a combination, such as Return, Escape, Tab, ctrl+s, alt+F4, ctrl+shift+t.", {
        keys: z.string().max(60).describe("xdotool key names, joined with + for a combination."),
      }, async (args) => {
        await x(["key", "--", args.keys]);
        return done(`Pressed ${args.keys}.`);
      }),
      tool("scroll", "Scroll the mouse wheel at a point on the screen.", {
        x: z.number().int().min(0),
        y: z.number().int().min(0),
        direction: z.enum(["up", "down"]).default("down"),
        amount: z.number().int().min(1).max(30).default(5).describe("Wheel notches."),
      }, async (args) => {
        await x(["mousemove", String(args.x), String(args.y), "click", "--repeat", String(args.amount), args.direction === "up" ? "4" : "5"]);
        return done(`Scrolled ${args.direction}.`);
      }),
      tool("open", "Open a file, a folder or a web address with the desktop's default program for it.", {
        target: z.string().max(2000).describe("A path or a URL."),
      }, async (args) => {
        await run("xdg-open", [args.target], { env: display, timeout: 15_000 });
        return done(`Opened ${args.target}.`);
      }),
    ],
  });
}
