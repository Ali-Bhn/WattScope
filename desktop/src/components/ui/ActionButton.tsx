import type { ButtonHTMLAttributes, MouseEvent } from "react";

/**
 * A button that can become unavailable as a result of its own activation: while its action runs, or because
 * the action left nothing to do (Save once saved, "Today" once on today).
 *
 * The native `disabled` attribute makes the browser drop keyboard focus to the document the moment it is set,
 * and does not give it back when the button is enabled again, so a keyboard or screen-reader user loses their
 * place on every such action. `inactive` therefore leaves the button focusable: it is announced as unavailable
 * (`aria-disabled`), looks like a disabled button and ignores activation, but focus stays where it was.
 *
 * `disabled` remains the native attribute, for a button that is unavailable for a reason of its own (invalid
 * input, the monitor being down) and so cannot be holding the focus when that happens.
 */
export function ActionButton({ inactive = false, onClick, type = "button", ...rest }: ButtonHTMLAttributes<HTMLButtonElement> & { inactive?: boolean }) {
  const guarded = (event: MouseEvent<HTMLButtonElement>) => {
    if (inactive) {
      event.preventDefault(); // also stops a submit button from submitting its form
      return;
    }
    onClick?.(event);
  };
  return <button {...rest} type={type} aria-disabled={inactive || undefined} onClick={guarded} />;
}
