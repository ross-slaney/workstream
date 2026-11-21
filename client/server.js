#!/usr/bin/env node
/*
 * The web app's server: renders every page per request (app.json web.output "server") and answers the API
 * routes, through expo-server's Express adapter, from the `expo export --platform web` output. Built into
 * the web image by Dockerfile; `npm run web:serve` runs it locally after `npm run export:web`.
 *
 * Hashed client assets are cached for a year; nothing else is.
 */
const path = require("path");
const compression = require("compression");
const express = require("express");
const { createRequestHandler } = require("expo-server/adapter/express");

const CLIENT_BUILD_DIR = path.join(__dirname, "dist/client");
const SERVER_BUILD_DIR = path.join(__dirname, "dist/server");

process.env.NODE_ENV = "production";

const app = express();
app.disable("x-powered-by");
app.use(compression());

// File names under _expo/ and assets/ carry a content hash; nothing else is served as an unchanging file.
app.use("/_expo", express.static(path.join(CLIENT_BUILD_DIR, "_expo"), { immutable: true, maxAge: "1y" }));
app.use("/assets", express.static(path.join(CLIENT_BUILD_DIR, "assets"), { immutable: true, maxAge: "1y" }));
app.use(express.static(CLIENT_BUILD_DIR, { index: false, maxAge: "1h" }));

app.all("/{*all}", createRequestHandler({ build: SERVER_BUILD_DIR }));

const port = Number(process.env.PORT ?? 8080);
app.listen(port, () => {
  console.log(`Workstream web listening on ${port}`);
});
