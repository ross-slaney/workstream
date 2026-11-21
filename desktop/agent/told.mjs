// What a desktop is told: its API's address and its key, and nothing else.
//
// A container is given them as variables (WORKSTREAM_API, WORKSTREAM_KEY). A machine reads them from its
// user data, which Azure serves to the machine alone at an address only it can reach, and which the API set
// when it made the machine or gave it to a task (api/Desktops/DesktopHosts.cs). They are never written to
// the machine's disk, so a disk can be copied without carrying anyone's key.

import { existsSync } from "node:fs";

const USER_DATA = "http://169.254.169.254/metadata/instance/compute/userData?api-version=2021-01-01&format=text";
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** The two values out of `NAME=value` lines; empty strings for what is not there. */
export function parseTold(text) {
  const values = {};
  for (const line of String(text ?? "").split("\n")) {
    const at = line.indexOf("=");
    if (at > 0) values[line.slice(0, at).trim()] = line.slice(at + 1).trim();
  }
  return { api: (values.WORKSTREAM_API ?? "").replace(/\/+$/, ""), key: values.WORKSTREAM_KEY ?? "" };
}

/** User data arrives encoded; a machine told nothing has none. */
export const decodeUserData = (encoded) => parseTold(Buffer.from(String(encoded ?? "").trim(), "base64").toString("utf8"));

async function fromUserData() {
  try {
    const response = await fetch(USER_DATA, { headers: { Metadata: "true" }, signal: AbortSignal.timeout(2000) });
    return response.ok ? decodeUserData(await response.text()) : null;
  } catch {
    // The network is not up yet.
    return null;
  }
}

/**
 * Answers once the desktop has been told both things. A machine that has not been told yet asks again,
 * quickly at first because it has usually just started, then once a second.
 */
export async function told({ log }) {
  const given = { api: (process.env.WORKSTREAM_API ?? "").replace(/\/+$/, ""), key: process.env.WORKSTREAM_KEY ?? "" };
  if (given.api && given.key) return given;
  if (existsSync("/.dockerenv")) {
    log("This container was not given its API or its key (WORKSTREAM_API, WORKSTREAM_KEY). Nothing to do.");
    process.exit(1);
  }
  for (let asked = 0; ; asked++) {
    const read = await fromUserData();
    if (read?.api && read.key) return read;
    if (asked === 50) log("This machine has not been told its API and its key yet. Waiting.");
    await sleep(asked < 50 ? 100 : 1000);
  }
}
