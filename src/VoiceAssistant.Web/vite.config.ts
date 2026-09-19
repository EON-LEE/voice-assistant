import { defineConfig } from "vite";

export default defineConfig({
  server: { host: "127.0.0.1", proxy: { "/api": { target: process.env.VOICE_ASSISTANT_DEV_API ?? "http://localhost:5080", ws: true },
    "/health": { target: process.env.VOICE_ASSISTANT_DEV_API ?? "http://localhost:5080" } } },
  build: { target: "es2022" },
});
