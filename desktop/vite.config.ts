import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "node:path";

// Tauri expects a fixed dev-server port and a relative base so the built
// index.html works when loaded from the webview's local asset protocol.
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "src"),
    },
  },
  clearScreen: false,
  server: {
    port: 1420,
    strictPort: true,
  },
  envPrefix: ["VITE_", "TAURI_"],
  build: {
    // Tauri's webview targets are recent Chromium/WebView2 on Windows.
    target: "es2022",
    outDir: "dist",
    sourcemap: !!process.env.TAURI_DEBUG,
  },
});
