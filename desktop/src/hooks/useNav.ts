import { createContext, useContext } from "react";

export type View = "dashboard" | "gaming" | "reports" | "settings";

/** Where in Settings to scroll to when navigating there (a section id), if anywhere in particular. */
export type SettingsSection = "monitoring" | "estimation" | "electricity" | "data" | "language" | "application";

export interface Nav {
  navigate: (view: View, section?: SettingsSection) => void;
}

export const NavContext = createContext<Nav>({ navigate: () => undefined });

export function useNav(): Nav {
  return useContext(NavContext);
}
