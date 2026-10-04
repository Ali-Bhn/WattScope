import { describe, expect, it, vi } from "vitest";
import type { MouseEvent, ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ActionButton } from "./ActionButton";

/** The click handler React would attach, called the way the browser calls it. */
function click(element: ReactElement) {
  const rendered = ActionButton(element.props) as ReactElement<{ onClick: (event: MouseEvent<HTMLButtonElement>) => void }>;
  const event = { preventDefault: vi.fn() };
  rendered.props.onClick(event as unknown as MouseEvent<HTMLButtonElement>);
  return event;
}

describe("ActionButton (a button that becomes unavailable through its own activation)", () => {
  it("is never natively disabled while inactive, so the browser leaves the keyboard focus on it", () => {
    // Chromium moves focus to <body> when a focused button gets the `disabled` attribute and does not return it.
    const html = renderToStaticMarkup(
      <ActionButton className="btn" inactive onClick={() => undefined}>
        Save
      </ActionButton>,
    );
    expect(html).toContain('aria-disabled="true"');
    expect(html).not.toMatch(/\sdisabled(=|\s|>)/);
  });

  it("is an ordinary button when active: no aria-disabled, type=button", () => {
    const html = renderToStaticMarkup(<ActionButton className="btn">Save</ActionButton>);
    expect(html).toBe('<button class="btn" type="button">Save</button>');
  });

  it("ignores activation while inactive (click, Enter and Space all arrive as a click) and blocks a form submit", () => {
    const onClick = vi.fn();
    const event = click(
      <ActionButton type="submit" inactive onClick={onClick}>
        Start
      </ActionButton>,
    );
    expect(onClick).not.toHaveBeenCalled();
    expect(event.preventDefault).toHaveBeenCalledTimes(1);
  });

  it("runs the action when active", () => {
    const onClick = vi.fn();
    const event = click(<ActionButton onClick={onClick}>Start</ActionButton>);
    expect(onClick).toHaveBeenCalledTimes(1);
    expect(event.preventDefault).not.toHaveBeenCalled();
  });

  it("keeps the native attribute for a reason that is not its own action (invalid input, monitor down)", () => {
    const html = renderToStaticMarkup(
      <ActionButton disabled inactive={false}>
        Start
      </ActionButton>,
    );
    expect(html).toMatch(/\sdisabled=""/);
    expect(html).not.toContain("aria-disabled");
  });
});

describe("busy buttons across the app", () => {
  const sources = import.meta.glob(["../**/*.tsx", "!../**/*.test.tsx"], { query: "?raw", import: "default", eager: true }) as Record<string, string>;

  it("never natively disable a plain <button> on a busy/saving flag (use ActionButton's `inactive`)", () => {
    expect(Object.keys(sources).length).toBeGreaterThan(10);
    const offenders: string[] = [];
    for (const [file, text] of Object.entries(sources)) {
      for (const tag of text.match(/<button\b.*>$/gm) ?? []) {
        if (/disabled=\{[^}]*\b(busy|saving)\b/.test(tag)) offenders.push(`${file}: ${tag}`);
      }
    }
    expect(offenders).toEqual([]);
  });
});
