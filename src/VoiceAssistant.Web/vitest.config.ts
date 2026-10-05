import { defineConfig } from "vitest/config";
import { fileURLToPath } from "node:url";
import { normalizePath } from "vite";

export default defineConfig({
  test: { include: [normalizePath(fileURLToPath(new URL("../../tests/VoiceAssistant.Web.Tests/coach/**/*.test.ts", import.meta.url)))],
    environment: "node", restoreMocks: true, testTimeout: 20000, hookTimeout: 20000 },
});
