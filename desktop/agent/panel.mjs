// Which apps the panel should show, and whether that differs from what it shows now.

/**
 * @param {{ id: string, default?: boolean }[]} catalog  apps.json, whose order is the panel's order
 * @param {string[]} wanted                              the environment's apps
 * @param {string[] | undefined} shown                   what the panel was last written for; nothing means the image's default panel
 * @returns {{ apps: string[], changed: boolean }}
 */
export function panelFor(catalog, wanted, shown) {
  const inOrder = (ids) => catalog.filter((app) => ids.includes(app.id)).map((app) => app.id);
  const apps = inOrder(wanted ?? []);
  const current = shown ? inOrder(shown) : catalog.filter((app) => app.default).map((app) => app.id);
  return { apps, changed: apps.join(" ") !== current.join(" ") };
}
