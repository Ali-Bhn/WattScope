/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  darkMode: "class",
  theme: {
    extend: {
      colors: {
        surface: {
          0: "#0b0d10",
          1: "#12151a",
          2: "#191d24",
          3: "#232830",
          border: "#2a3039",
        },
        accent: {
          green: "#3ecf8e",
          blue: "#4c9aff",
          purple: "#a78bfa",
        },
        ink: {
          primary: "#e7eaef",
          secondary: "#9aa3b2",
          // Raised from #5f6774 (3.1:1 on the card surface) so small muted text meets 4.5:1.
          muted: "#8b94a3",
        },
      },
      fontFamily: {
        sans: [
          "Inter",
          "-apple-system",
          "Segoe UI",
          "Roboto",
          "Helvetica Neue",
          "Arial",
          "sans-serif",
        ],
        mono: ["JetBrains Mono", "Consolas", "monospace"],
      },
      borderRadius: {
        card: "0.75rem",
      },
    },
  },
  plugins: [],
};
