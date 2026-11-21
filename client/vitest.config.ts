import path from "node:path";
import { defineConfig } from "vitest/config";

export default defineConfig({
  esbuild: { jsx: "automatic" },
  test: {
    environment: "node",
    globals: false,
    include: ["**/__tests__/**/*.test.ts", "**/__tests__/**/*.test.tsx"],
    exclude: ["node_modules/**", "dist/**"],
  },
  resolve: {
    alias: { "@": path.resolve(__dirname) },
  },
});
