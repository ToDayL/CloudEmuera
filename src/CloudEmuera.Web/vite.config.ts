import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  server: {
    host: "0.0.0.0",
    port: 5173,
    // Vite reloads the whole page after its own WebSocket reconnects. Keep
    // that disabled by default so application realtime recovery has the same
    // in-page lifecycle as the production SPA; UI work can opt in explicitly.
    hmr: process.env.CLOUDEMUERA_VITE_HMR === "true",
    allowedHosts: ["web"],
    proxy: {
      "/api": { target: "http://api:28647", ws: true },
      "/health": "http://api:28647",
    },
  },
  preview: {
    host: "0.0.0.0",
    port: 5173,
    allowedHosts: ["web"],
    proxy: {
      "/api": { target: "http://api:28647", ws: true },
      "/health": "http://api:28647",
    },
  },
  test: {
    environment: "jsdom",
    setupFiles: "./src/test/setup.ts",
  },
});
