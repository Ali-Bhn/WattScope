//! Pure sidecar-lifecycle decisions, kept free of Tauri/process types so they
//! can be unit-tested exhaustively: when to restart a crashed sidecar, how
//! often, and with which arguments.

use std::time::{Duration, Instant};

/// A crashed sidecar is restarted at most this many times within
/// [`RESTART_WINDOW`]; after that the host gives up and says so, instead of
/// looping forever against a persistent fault.
pub const MAX_RESTARTS: usize = 3;
pub const RESTART_WINDOW: Duration = Duration::from_secs(60);
const BACKOFF: [Duration; MAX_RESTARTS] = [Duration::from_secs(1), Duration::from_secs(2), Duration::from_secs(4)];

/// `wattscope-probe --serve` exits with this code when another process
/// already holds the recording lock for the database (see
/// `ServeCommand.cs`). Restarting cannot fix that, so it is never retried.
pub const RECORDER_LOCK_HELD_EXIT_CODE: i32 = 3;

#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum GiveUpReason {
    /// Another process owns the recording lock. The host must never kill
    /// processes by executable name to take it over.
    RecorderLockHeld,
    TooManyRestarts,
}

#[derive(Debug, PartialEq, Eq)]
pub enum Decision {
    Restart { delay: Duration },
    GiveUp(GiveUpReason),
}

#[derive(Default)]
pub struct RestartPolicy {
    restarts: Vec<Instant>,
}

impl RestartPolicy {
    pub fn new() -> Self {
        Self::default()
    }

    /// Decides what to do about an exit the host did not ask for.
    pub fn on_unexpected_exit(&mut self, now: Instant, exit_code: Option<i32>) -> Decision {
        if exit_code == Some(RECORDER_LOCK_HELD_EXIT_CODE) {
            return Decision::GiveUp(GiveUpReason::RecorderLockHeld);
        }
        self.restarts.retain(|t| now.saturating_duration_since(*t) < RESTART_WINDOW);
        if self.restarts.len() >= MAX_RESTARTS {
            return Decision::GiveUp(GiveUpReason::TooManyRestarts);
        }
        let delay = BACKOFF[self.restarts.len()];
        self.restarts.push(now);
        Decision::Restart { delay }
    }

    /// An explicit user action (tray Start / Open after a give-up) starts
    /// afresh: it is not part of an automatic crash loop.
    pub fn reset(&mut self) {
        self.restarts.clear();
    }
}

/// Arguments for (re)spawning the sidecar. All fixed literals -- nothing is
/// ever built from user-supplied text.
///
/// - Monitoring was wanted: `--resume` continues the interrupted session
///   (the crash is recorded as an honest "Interrupted" gap).
/// - The user had stopped monitoring: come back idle, never start recording
///   behind their back.
pub fn spawn_args(monitoring_wanted: bool, first_launch: bool) -> Vec<String> {
    let mut args = vec!["--serve".to_string()];
    if first_launch {
        return args; // a fresh launch starts a fresh session
    }
    args.push(if monitoring_wanted { "--resume" } else { "--paused" }.to_string());
    args
}

#[cfg(test)]
mod tests {
    use super::*;

    fn t0() -> Instant {
        Instant::now()
    }

    #[test]
    fn first_crashes_are_restarted_with_growing_backoff() {
        let mut p = RestartPolicy::new();
        let now = t0();
        assert_eq!(p.on_unexpected_exit(now, Some(-1)), Decision::Restart { delay: Duration::from_secs(1) });
        assert_eq!(p.on_unexpected_exit(now, Some(-1)), Decision::Restart { delay: Duration::from_secs(2) });
        assert_eq!(p.on_unexpected_exit(now, Some(-1)), Decision::Restart { delay: Duration::from_secs(4) });
    }

    #[test]
    fn a_crash_loop_is_bounded() {
        let mut p = RestartPolicy::new();
        let now = t0();
        for _ in 0..MAX_RESTARTS {
            assert!(matches!(p.on_unexpected_exit(now, Some(1)), Decision::Restart { .. }));
        }
        assert_eq!(p.on_unexpected_exit(now, Some(1)), Decision::GiveUp(GiveUpReason::TooManyRestarts));
        // and it stays given-up while the failures keep arriving inside the window
        assert_eq!(p.on_unexpected_exit(now + Duration::from_secs(5), Some(1)), Decision::GiveUp(GiveUpReason::TooManyRestarts));
    }

    #[test]
    fn old_restarts_age_out_of_the_window() {
        let mut p = RestartPolicy::new();
        let now = t0();
        for _ in 0..MAX_RESTARTS {
            p.on_unexpected_exit(now, None);
        }
        let later = now + RESTART_WINDOW + Duration::from_secs(1);
        assert_eq!(p.on_unexpected_exit(later, None), Decision::Restart { delay: Duration::from_secs(1) });
    }

    #[test]
    fn a_held_recorder_lock_is_never_retried() {
        let mut p = RestartPolicy::new();
        assert_eq!(
            p.on_unexpected_exit(t0(), Some(RECORDER_LOCK_HELD_EXIT_CODE)),
            Decision::GiveUp(GiveUpReason::RecorderLockHeld)
        );
        // and it did not consume restart budget
        assert!(matches!(p.on_unexpected_exit(t0(), Some(1)), Decision::Restart { .. }));
    }

    #[test]
    fn an_explicit_user_start_resets_the_budget() {
        let mut p = RestartPolicy::new();
        let now = t0();
        for _ in 0..MAX_RESTARTS {
            p.on_unexpected_exit(now, None);
        }
        p.reset();
        assert_eq!(p.on_unexpected_exit(now, None), Decision::Restart { delay: Duration::from_secs(1) });
    }

    #[test]
    fn a_fresh_launch_starts_a_fresh_session() {
        assert_eq!(spawn_args(true, true), vec!["--serve"]);
        assert_eq!(spawn_args(false, true), vec!["--serve"]);
    }

    #[test]
    fn a_restart_resumes_only_if_monitoring_was_wanted() {
        assert_eq!(spawn_args(true, false), vec!["--serve", "--resume"]);
        assert_eq!(spawn_args(false, false), vec!["--serve", "--paused"]);
    }

    #[test]
    fn spawn_args_are_fixed_literals() {
        for wanted in [true, false] {
            for first in [true, false] {
                for arg in spawn_args(wanted, first) {
                    assert!(matches!(arg.as_str(), "--serve" | "--resume" | "--paused"));
                }
            }
        }
    }
}
