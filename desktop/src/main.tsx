import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import i18n from "./i18n";
import "./index.css";

async function start() {
  // Development only: `vite build --mode mock` renders the UI against synthetic data in a plain browser.
  // In a normal build this condition is a compile-time constant false, so none of the mock is bundled.
  if (import.meta.env.VITE_MOCK === "1") {
    const { installMock } = await import("./dev/mock");
    await installMock();
    const lang = new URLSearchParams(location.search).get("lang");
    if (lang) await i18n.changeLanguage(lang);
  }
  ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
    <React.StrictMode>
      <App />
    </React.StrictMode>,
  );
}

void start();
