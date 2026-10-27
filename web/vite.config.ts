import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://localhost:5103", changeOrigin: true },
    },
  },
  build: { outDir: "../src/Trackdub.Director.Server/wwwroot", emptyOutDir: true },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/setupTests.ts"],
    globals: true,
  },
} as any);
