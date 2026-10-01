//! WattScope's native host (Tauri). Three things live here, deliberately
//! separate from one another (docs/ARCHITECTURE.md, "Process model"):
//!
//! 1. The **window** -- only a view. Closing it hides it to the tray; it
//!    never stops monitoring. While hidden, tick events are not even
//!    forwarded to the webview, so a hidden dashboard does no work.
//! 2. The **sidecar supervisor** -- owns exactly one `wattscope-probe
//!    --serve` child, by *handle* (never by process name or PID): spawns it,
//!    detects crashes, restarts within a hard bound, and stops it on Exit.
//! 3. The **tray** -- native status/controls (open, start/stop monitoring,
//!    settings, exit), the only way out of a hidden window.
//!
//! Whether monitoring is running is decided inside the sidecar
//! (`MonitoringController`); the host only relays start/stop requests.

mod labels;
mod lifecycle;

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use serde_json::{json, Value};
use tauri::menu::{IsMenuItem, Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIcon, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Emitter, Manager, Wry};
use tauri_plugin_shell::process::{CommandChild, CommandEvent};
use tauri_plugin_shell::ShellExt;
use tokio::sync::oneshot;

use lifecycle::{spawn_args, Decision, GiveUpReason, RestartPolicy};

/// Longest a request waits for its response before failing -- bounded so a
/// wedged sidecar can never hang the UI.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(15);

/// How long Exit waits for the sidecar to flush and stop itself before the
/// host kills the child it owns. Normal shutdown is well under a second.
const EXIT_GRACE: Duration = Duration::from_secs(5);

/// Shorter grace for the last-resort path at process teardown, so a Windows
/// logoff/shutdown is never held up.
const TEARDOWN_GRACE: Duration = Duration::from_secs(2);

/// Tray text refresh interval for the live power/energy lines -- a native
/// menu item update every few seconds, not per sample.
const TRAY_REFRESH: Duration = Duration::from_secs(5);

const SHUTDOWN_LINE: &[u8] = b"{\"id\":0,\"cmd\":\"shutdown\"}\n";

#[derive(Clone, Copy, PartialEq, Eq, Default, Debug)]
enum TrayState {
    #[default]
    Stopped,
    Starting,
    Complete,
    Partial,
    SensorUnavailable,
    Unavailable,
}

#[derive(Clone, Default)]
struct TrayModel {
    state: TrayState,
    watts: Option<f64>,
    kwh: f64,
    cost: Option<f64>,
    currency: String,
}

struct TrayUi {
    icon: TrayIcon,
    status: MenuItem<Wry>,
    power: MenuItem<Wry>,
    session: MenuItem<Wry>,
    open: MenuItem<Wry>,
    start: MenuItem<Wry>,
    stop: MenuItem<Wry>,
    settings: MenuItem<Wry>,
    exit: MenuItem<Wry>,
}

struct SidecarState {
    /// The one owned sidecar. Identity is this handle, never a PID or a name.
    child: Mutex<Option<CommandChild>>,
    alive: AtomicBool,
    /// Identifies the current spawn so a late exit event from an older one is ignored.
    generation: AtomicU64,
    pending: Mutex<HashMap<u64, oneshot::Sender<Value>>>,
    next_id: AtomicU64,
    /// Set once WE are ending the sidecar (Exit): its exit is then not a crash.
    exit_requested: AtomicBool,
    /// Whether the user wants monitoring running (drives what a crash restart does).
    monitoring_wanted: AtomicBool,
    ui_visible: AtomicBool,
    policy: Mutex<RestartPolicy>,
    tray: Mutex<TrayModel>,
    tray_ui: Mutex<Option<TrayUi>>,
    last_tray_refresh: Mutex<Instant>,
    lang: Mutex<String>,
}

impl SidecarState {
    fn new() -> Self {
        Self {
            child: Mutex::new(None),
            alive: AtomicBool::new(false),
            generation: AtomicU64::new(0),
            pending: Mutex::new(HashMap::new()),
            next_id: AtomicU64::new(1),
            exit_requested: AtomicBool::new(false),
            monitoring_wanted: AtomicBool::new(true),
            ui_visible: AtomicBool::new(true),
            policy: Mutex::new(RestartPolicy::new()),
            tray: Mutex::new(TrayModel::default()),
            tray_ui: Mutex::new(None),
            last_tray_refresh: Mutex::new(Instant::now()),
            lang: Mutex::new("en".to_string()),
        }
    }
}

// ------------------------------------------------------------------ window

fn show_main(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
    set_ui_visible(app, true);
}

fn set_ui_visible(app: &AppHandle, visible: bool) {
    let state = app.state::<SidecarState>();
    let changed = state.ui_visible.swap(visible, Ordering::SeqCst) != visible;
    if changed {
        let _ = app.emit("ui-visibility", visible);
    }
}

// -------------------------------------------------------------------- tray

fn refresh_tray(app: &AppHandle) {
    let state = app.state::<SidecarState>();
    let model = state.tray.lock().map(|m| m.clone()).unwrap_or_default();
    let lang = state.lang.lock().map(|l| l.clone()).unwrap_or_else(|_| "en".into());
    let l = labels::labels(&lang);
    *state.last_tray_refresh.lock().unwrap() = Instant::now();

    let guard = state.tray_ui.lock().unwrap();
    let Some(ui) = guard.as_ref() else { return };

    let status_text = match model.state {
        TrayState::Complete => l.state_complete,
        TrayState::Partial => l.state_partial,
        TrayState::Starting => l.state_starting,
        TrayState::Stopped => l.state_stopped,
        TrayState::SensorUnavailable => l.state_sensor_unavailable,
        TrayState::Unavailable => l.state_unavailable,
    };
    let live = matches!(model.state, TrayState::Complete | TrayState::Partial | TrayState::SensorUnavailable);
    let power_text = match (live, model.watts) {
        (true, Some(w)) => format!("{}: {:.1} W", l.power, w),
        _ => format!("{}: --", l.power),
    };
    let cost_text = match model.cost {
        Some(c) if live => format!("{:.4} {}", c, if model.currency.is_empty() { "EUR" } else { &model.currency }),
        _ => l.cost_unavailable.to_string(),
    };
    let session_text = if live || model.kwh > 0.0 {
        format!("{}: {:.3} kWh, {}", l.session, model.kwh, cost_text)
    } else {
        format!("{}: --", l.session)
    };

    let _ = ui.status.set_text(status_text);
    let _ = ui.power.set_text(power_text);
    let _ = ui.session.set_text(session_text);
    let _ = ui.open.set_text(l.open);
    let _ = ui.start.set_text(l.start);
    let _ = ui.stop.set_text(l.stop);
    let _ = ui.settings.set_text(l.settings);
    let _ = ui.exit.set_text(l.exit);
    let running = matches!(
        model.state,
        TrayState::Complete | TrayState::Partial | TrayState::SensorUnavailable | TrayState::Starting
    );
    let _ = ui.start.set_enabled(!running);
    let _ = ui.stop.set_enabled(running);
    let _ = ui.icon.set_tooltip(Some(format!("WattScope - {status_text}")));
}

fn build_tray(app: &AppHandle) -> tauri::Result<()> {
    let state = app.state::<SidecarState>();
    let l = labels::labels("en");
    let status = MenuItem::with_id(app, "status", l.state_stopped, false, None::<&str>)?;
    let power = MenuItem::with_id(app, "power", format!("{}: --", l.power), false, None::<&str>)?;
    let session = MenuItem::with_id(app, "session", format!("{}: --", l.session), false, None::<&str>)?;
    let open = MenuItem::with_id(app, "open", l.open, true, None::<&str>)?;
    let start = MenuItem::with_id(app, "start", l.start, true, None::<&str>)?;
    let stop = MenuItem::with_id(app, "stop", l.stop, true, None::<&str>)?;
    let settings = MenuItem::with_id(app, "settings", l.settings, true, None::<&str>)?;
    let exit = MenuItem::with_id(app, "exit", l.exit, true, None::<&str>)?;
    let sep1 = PredefinedMenuItem::separator(app)?;
    let sep2 = PredefinedMenuItem::separator(app)?;
    let sep3 = PredefinedMenuItem::separator(app)?;

    let items: Vec<&dyn IsMenuItem<Wry>> = vec![
        &status, &power, &session, &sep1, &open, &start, &stop, &sep2, &settings, &sep3, &exit,
    ];
    let menu = Menu::with_items(app, &items)?;

    let mut builder = TrayIconBuilder::with_id("wattscope")
        .menu(&menu)
        .tooltip("WattScope")
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id.as_ref() {
            "open" => show_main(app),
            "settings" => {
                show_main(app);
                let _ = app.emit("navigate", "settings");
            }
            "start" => {
                let app = app.clone();
                tauri::async_runtime::spawn(async move { tray_start(&app).await });
            }
            "stop" => {
                let app = app.clone();
                tauri::async_runtime::spawn(async move {
                    let _ = request_sidecar(&app, json!({ "cmd": "stop_monitoring" })).await;
                });
            }
            "exit" => {
                let app = app.clone();
                tauri::async_runtime::spawn(async move { graceful_exit(app).await });
            }
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = event {
                show_main(tray.app_handle());
            }
        });
    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    }
    let icon = builder.build(app)?;

    *state.tray_ui.lock().unwrap() = Some(TrayUi { icon, status, power, session, open, start, stop, settings, exit });
    refresh_tray(app);
    Ok(())
}

fn set_tray_state(app: &AppHandle, new_state: TrayState) {
    let state = app.state::<SidecarState>();
    let changed = {
        let mut model = state.tray.lock().unwrap();
        let changed = model.state != new_state;
        model.state = new_state;
        if matches!(new_state, TrayState::Stopped | TrayState::Unavailable) {
            model.watts = None;
        }
        changed
    };
    if changed {
        refresh_tray(app);
    }
}

fn tray_apply_tick(app: &AppHandle, tick: &Value) {
    let state = app.state::<SidecarState>();
    let new_state = match tick.get("estimateCompleteness").and_then(|v| v.as_str()) {
        Some("complete") => TrayState::Complete,
        Some("partial") => TrayState::Partial,
        _ => TrayState::SensorUnavailable,
    };
    let state_changed = {
        let mut model = state.tray.lock().unwrap();
        let changed = model.state != new_state;
        model.state = new_state;
        model.watts = tick.get("estimatedTotalWatts").and_then(|v| v.as_f64());
        model.kwh = tick.get("sessionKwh").and_then(|v| v.as_f64()).unwrap_or(0.0);
        model.cost = tick.get("sessionCostApprox").and_then(|v| v.as_f64());
        model.currency = tick.get("currency").and_then(|v| v.as_str()).unwrap_or("").to_string();
        changed
    };
    let due = state.last_tray_refresh.lock().map(|t| t.elapsed() >= TRAY_REFRESH).unwrap_or(true);
    if state_changed || due {
        refresh_tray(app);
    }
}

fn tray_apply_status(app: &AppHandle, status: &Value) {
    let state = app.state::<SidecarState>();
    match status.get("state").and_then(|v| v.as_str()).unwrap_or("") {
        "recording" => {
            state.monitoring_wanted.store(true, Ordering::SeqCst);
            set_tray_state(app, TrayState::Starting);
        }
        "idle" | "stopped" | "exiting" => {
            state.monitoring_wanted.store(false, Ordering::SeqCst);
            set_tray_state(app, TrayState::Stopped);
        }
        "failed" => set_tray_state(app, TrayState::Unavailable),
        _ => {}
    }
}

// ----------------------------------------------------------------- sidecar

fn handle_sidecar_line(app: &AppHandle, value: Value) {
    match value.get("type").and_then(|t| t.as_str()).unwrap_or("") {
        "tick" => {
            tray_apply_tick(app, &value);
            // A hidden dashboard does no work: ticks are not even delivered to the webview.
            if app.state::<SidecarState>().ui_visible.load(Ordering::SeqCst) {
                let _ = app.emit("sidecar-tick", value);
            }
        }
        "status" => {
            tray_apply_status(app, &value);
            let _ = app.emit("sidecar-status", value);
        }
        "response" => {
            let Some(id) = value.get("id").and_then(|v| v.as_u64()) else { return };
            let state = app.state::<SidecarState>();
            if let Ok(mut pending) = state.pending.lock() {
                if let Some(sender) = pending.remove(&id) {
                    let _ = sender.send(value);
                }
            };
        }
        other => eprintln!("wattscope-probe: unrecognized message type '{other}'"),
    }
}

/// Spawns the sidecar -- but only if none is owned already, so there can
/// never be two. The check and the store happen under one lock.
fn spawn_sidecar(app: &AppHandle, args: Vec<String>) -> Result<(), String> {
    let state = app.state::<SidecarState>();
    let mut guard = state.child.lock().map_err(|_| "sidecar state poisoned".to_string())?;
    if guard.is_some() || state.alive.load(Ordering::SeqCst) {
        return Err("a sidecar is already running".into());
    }

    let (mut rx, child) = app
        .shell()
        .sidecar("wattscope-probe")
        .map_err(|e| format!("wattscope-probe sidecar binary was not bundled: {e}"))?
        .args(args)
        .spawn()
        .map_err(|e| format!("failed to spawn the wattscope-probe sidecar process: {e}"))?;

    let generation = state.generation.fetch_add(1, Ordering::SeqCst) + 1;
    *guard = Some(child);
    state.alive.store(true, Ordering::SeqCst);
    drop(guard);

    let event_app = app.clone();
    tauri::async_runtime::spawn(async move {
        while let Some(event) = rx.recv().await {
            match event {
                CommandEvent::Stdout(bytes) => {
                    let text = String::from_utf8_lossy(&bytes);
                    let line = text.trim();
                    if line.is_empty() {
                        continue;
                    }
                    match serde_json::from_str::<Value>(line) {
                        Ok(value) => handle_sidecar_line(&event_app, value),
                        Err(err) => eprintln!("wattscope-probe: malformed JSON line ({err}): {line}"),
                    }
                }
                CommandEvent::Stderr(bytes) => eprint!("{}", String::from_utf8_lossy(&bytes)),
                CommandEvent::Error(err) => eprintln!("wattscope-probe: {err}"),
                CommandEvent::Terminated(payload) => handle_terminated(&event_app, generation, payload.code),
                _ => {}
            }
        }
    });
    Ok(())
}

fn emit_status(app: &AppHandle, state_name: &str, message: &str) {
    let payload = json!({ "type": "status", "state": state_name, "message": message });
    tray_apply_status(app, &payload);
    let _ = app.emit("sidecar-status", payload);
}

/// The sidecar process ended. If the host asked for that (Exit) it is
/// simply over; otherwise it is a crash and the bounded restart policy decides.
fn handle_terminated(app: &AppHandle, generation: u64, code: Option<i32>) {
    let state = app.state::<SidecarState>();
    if state.generation.load(Ordering::SeqCst) != generation {
        return; // a late event from an older spawn
    }
    if let Ok(mut child) = state.child.lock() {
        child.take();
    }
    state.alive.store(false, Ordering::SeqCst);
    if let Ok(mut pending) = state.pending.lock() {
        pending.clear(); // waiting requests fail fast instead of timing out
    }
    let _ = app.emit("sidecar-exited", code);
    if state.exit_requested.load(Ordering::SeqCst) {
        return;
    }

    let decision = state.policy.lock().unwrap().on_unexpected_exit(Instant::now(), code);
    match decision {
        Decision::Restart { delay } => {
            emit_status(app, "restarting", &format!("The monitor stopped unexpectedly (exit code {code:?}); restarting in {} s.", delay.as_secs()));
            set_tray_state(app, TrayState::Unavailable);
            let app = app.clone();
            tauri::async_runtime::spawn(async move {
                tokio::time::sleep(delay).await;
                let state = app.state::<SidecarState>();
                if state.exit_requested.load(Ordering::SeqCst) {
                    return;
                }
                let args = spawn_args(state.monitoring_wanted.load(Ordering::SeqCst), false);
                if let Err(err) = spawn_sidecar(&app, args) {
                    emit_status(&app, "failed", &err);
                }
            });
        }
        Decision::GiveUp(reason) => {
            let message = match reason {
                GiveUpReason::RecorderLockHeld => {
                    "Another WattScope recorder already owns this database. Close it (or use Exit WattScope from its tray icon) and try again.".to_string()
                }
                GiveUpReason::TooManyRestarts => {
                    "The monitor keeps stopping unexpectedly; automatic restarts were stopped. Choose Start monitoring to try again.".to_string()
                }
            };
            emit_status(app, "failed", &message);
            set_tray_state(app, TrayState::Unavailable);
        }
    }
}

/// Sends one JSON request to the sidecar and awaits its correlated response.
async fn request_sidecar(app: &AppHandle, mut request: Value) -> Result<Value, String> {
    let state = app.state::<SidecarState>();
    let id = state.next_id.fetch_add(1, Ordering::SeqCst);
    request["id"] = json!(id);
    let mut line = serde_json::to_string(&request).map_err(|e| e.to_string())?;
    line.push('\n');

    let (tx, rx) = oneshot::channel();
    state.pending.lock().map_err(|_| "sidecar state poisoned".to_string())?.insert(id, tx);

    let write_result = {
        let mut guard = state.child.lock().map_err(|_| "sidecar state poisoned".to_string())?;
        match guard.as_mut() {
            Some(child) => child.write(line.as_bytes()).map_err(|e| e.to_string()),
            None => Err("the sidecar is not running".to_string()),
        }
    };
    if let Err(err) = write_result {
        if let Ok(mut pending) = state.pending.lock() {
            pending.remove(&id);
        }
        return Err(err);
    }

    match tokio::time::timeout(REQUEST_TIMEOUT, rx).await {
        Ok(Ok(value)) => Ok(value),
        Ok(Err(_)) => Err("the sidecar closed before responding".into()),
        Err(_) => {
            if let Ok(mut pending) = state.pending.lock() {
                pending.remove(&id);
            }
            Err("the sidecar did not respond in time".into())
        }
    }
}

/// Tray "Start monitoring": if the sidecar is gone (it gave up after
/// crashing) bring it back -- an explicit user action, so the crash-loop
/// budget starts fresh; otherwise ask the running one to start.
async fn tray_start(app: &AppHandle) {
    let state = app.state::<SidecarState>();
    if state.alive.load(Ordering::SeqCst) {
        let _ = request_sidecar(app, json!({ "cmd": "start_monitoring" })).await;
    } else {
        state.exit_requested.store(false, Ordering::SeqCst);
        state.policy.lock().unwrap().reset();
        state.monitoring_wanted.store(true, Ordering::SeqCst);
        if let Err(err) = spawn_sidecar(app, spawn_args(true, true)) {
            emit_status(app, "failed", &err);
        }
    }
}

/// Stops the owned sidecar: ask it to shut down (it flushes and closes the
/// session itself), wait a bounded time, then kill the child *we hold the
/// handle of* if it is still alive. Nothing is ever killed by name or PID.
async fn stop_sidecar(app: &AppHandle, grace: Duration) {
    let state = app.state::<SidecarState>();
    state.exit_requested.store(true, Ordering::SeqCst);
    if let Ok(mut guard) = state.child.lock() {
        if let Some(child) = guard.as_mut() {
            let _ = child.write(SHUTDOWN_LINE);
        }
    };
    let deadline = Instant::now() + grace;
    while state.alive.load(Ordering::SeqCst) && Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    if state.alive.load(Ordering::SeqCst) {
        let child = state.child.lock().ok().and_then(|mut g| g.take());
        if let Some(child) = child {
            eprintln!("wattscope: sidecar did not stop within {grace:?}; killing the owned child");
            let _ = child.kill();
        }
        state.alive.store(false, Ordering::SeqCst);
    }
}

/// Explicit Exit: stop monitoring cleanly, end the owned sidecar, then end the app.
async fn graceful_exit(app: AppHandle) {
    stop_sidecar(&app, EXIT_GRACE).await;
    app.exit(0);
}

// ---------------------------------------------------------------- commands

#[tauri::command]
async fn sidecar_request(app: AppHandle, request: Value) -> Result<Value, String> {
    request_sidecar(&app, request).await
}

/// The UI tells the host which language is active so the native tray follows it.
#[tauri::command]
fn set_ui_language(app: AppHandle, lang: String) {
    let normalized = match lang.as_str() {
        "de" | "fa" => lang,
        _ => "en".to_string(),
    };
    *app.state::<SidecarState>().lang.lock().unwrap() = normalized;
    refresh_tray(&app);
}

/// Settings' "Exit WattScope" (closing the window only hides it to the tray).
#[tauri::command]
async fn exit_app(app: AppHandle) {
    graceful_exit(app).await;
}

#[tauri::command]
fn hide_to_tray(app: AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.hide();
    }
    set_ui_visible(&app, false);
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app = tauri::Builder::default()
        // Single instance: must be registered first. A second launch does not
        // start a second sidecar/session/engine -- it just brings this one forward.
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| show_main(app)))
        .plugin(tauri_plugin_shell::init())
        .manage(SidecarState::new())
        .setup(|app| {
            let handle = app.handle().clone();
            build_tray(&handle)?;
            spawn_sidecar(&handle, spawn_args(true, true)).map_err(|e| std::io::Error::new(std::io::ErrorKind::Other, e))?;
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![sidecar_request, set_ui_language, exit_app, hide_to_tray])
        .on_window_event(|window, event| {
            // Closing the window is not exiting: hide to the tray, keep monitoring.
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                let app = window.app_handle();
                if app.state::<SidecarState>().exit_requested.load(Ordering::SeqCst) {
                    return; // an Exit is in progress; let the window close
                }
                api.prevent_close();
                let _ = window.hide();
                set_ui_visible(app, false);
            }
        })
        .build(tauri::generate_context!())
        .expect("error while building the WattScope application");

    app.run(|app, event| {
        // Last resort when the process is being torn down by anything other
        // than the tray's Exit (e.g. the OS ending the session): still stop
        // the owned sidecar, with a short bound. If this does not finish,
        // the sidecar notices its stdin closing and stops itself, and crash
        // recovery (Milestone 002) is the final safety net.
        if let tauri::RunEvent::Exit = event {
            tauri::async_runtime::block_on(stop_sidecar(app, TEARDOWN_GRACE));
        }
    });
}
