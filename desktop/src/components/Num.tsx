import type { ReactNode } from "react";

/**
 * Wraps a formatted number/unit/timestamp so it stays left-to-right and
 * isolated from surrounding bidi text, even inside an RTL (Persian) layout
 * (Part H: "numbers, units, technical identifiers... must remain readable").
 */
export function Num({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <span dir="ltr" className={`inline-block [unicode-bidi:isolate] ${className ?? ""}`}>
      {children}
    </span>
  );
}
