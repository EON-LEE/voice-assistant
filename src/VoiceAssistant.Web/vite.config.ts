import { defineConfig } from "vite";

export default defineConfig({
  server: { host: "127.0.0.1", proxy: { "/api": { target: "http://localhost:5080", ws: true } } },
  build: { target: "es2022" },
});
