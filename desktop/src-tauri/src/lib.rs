//! WattScope's native shell: owns the one `wattscope-probe --serve` sidecar
//! process for the app's whole lifetime (Part I -- no duplicate monitoring
//! processes, no shell string injection since every argument is a fixed
//! literal or a validated number, never arbitrary user text). Stdout lines
//! are JSON (one object per line): "tick"/"status" are forwarded to the
//! frontend as Tauri events; "response" lines are matched back to whichever
//! `sidecar_request` call is waiting for that id. All stderr (diagnostics
//! from the .NET side) is just printed to this process's own stderr --
//! useful in `tauri dev`, harmless otherwise.

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::Duration;

use serde_json::{json, Value};
use tauri::{AppHandle, Emitter, Manager, State};
use tauri_plugin_shell::process::{CommandChild, CommandEvent};
use tauri_plugin_shell::ShellExt;
use tokio::sync::oneshot;

/// How long a `sidecar_request` waits for a matching "response" line before
/// giving up -- generous for a DB-backed report query, but still bounded so
/// a wedged sidecar can never hang the UI forever.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(15);

/// How long `restart_sidecar` waits after asking the old process to stop
/// before starting the new one. Both hold the same `MonitorLock` file, so
/// the new one must not start until the old one has actually released it;
/// a fixed delay is a pragmatic bound here (observed shutdown is well under
/// a second), not a guarantee -- if it is ever too short, the new process
/// fails fast with a clear "another recording is already writing" error
/// rather than corrupting anything.
const RESTART_SETTLE: Duration = Duration::from_millis(1500);

struct SidecarState {
    child: Mutex<Option<CommandChild>>,
    pending: Mutex<HashMap<u64, oneshot::Sender<Value>>>,
    next_id: AtomicU64,
}

impl SidecarState {
    fn new() -> Self {
        Self {
            child: Mutex::new(None),
            pending: Mutex::new(HashMap::new()),
            next_id: AtomicU64::new(1),
        }
    }
}

fn handle_sidecar_line(handle: &AppHandle, value: Value) {
    match value.get("type").and_then(|t| t.as_str()).unwrap_or("") {
        "tick" => {
            let _ = handle.emit("sidecar-tick", value);
        }
        "status" => {
            let _ = handle.emit("sidecar-status", value);
        }
        "response" => {
            let Some(id) = value.get("id").and_then(|v| v.as_u64()) else { return };
            let state = handle.state::<SidecarState>();
            if let Ok(mut pending) = state.pending.lock() {
                if let Some(sender) = pending.remove(&id) {
                    let _ = sender.send(value);
                }
            };
        }
        other => {
            eprintln!("wattscope-probe: unrecognized message type '{other}'");
        }
    }
}

/// Spawns the sidecar with the given arguments, replacing any previously
/// tracked child handle and (re)starting the stdout reader task. Does not
/// stop a previously running process itself -- callers that are replacing a
/// live sidecar (see `restart_sidecar`) must ask it to stop first.
fn spawn_sidecar(handle: &AppHandle, args: Vec<String>) -> Result<(), String> {
    let (mut rx, child) = handle
        .shell()
        .sidecar("wattscope-probe")
        .map_err(|e| format!("wattscope-probe sidecar binary was not bundled: {e}"))?
        .args(args)
        .spawn()
        .map_err(|e| format!("failed to spawn the wattscope-probe sidecar process: {e}"))?;

    *handle.state::<SidecarState>().child.lock().unwrap() = Some(child);

    let event_handle = handle.clone();
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
                        Ok(value) => handle_sidecar_line(&event_handle, value),
                        Err(err) => eprintln!("wattscope-probe: malformed JSON line ({err}): {line}"),
                    }
                }
                CommandEvent::Stderr(bytes) => {
                    eprint!("{}", String::from_utf8_lossy(&bytes));
                }
                CommandEvent::Terminated(payload) => {
                    let _ = event_handle.emit("sidecar-exited", payload.code);
                }
                _ => {}
            }
        }
    });

    Ok(())
}

/// Sends one JSON request to the sidecar and awaits its correlated response.
/// `request` need not include an "id" -- one is assigned here.
#[tauri::command]
async fn sidecar_request(state: State<'_, SidecarState>, mut request: Value) -> Result<Value, String> {
    let id = state.next_id.fetch_add(1, Ordering::SeqCst);
    request["id"] = json!(id);
    let mut line = serde_json::to_string(&request).map_err(|e| e.to_string())?;
    line.push('\n');

    let (tx, rx) = oneshot::channel();
    state
        .pending
        .lock()
        .map_err(|_| "sidecar state poisoned".to_string())?
        .insert(id, tx);

    let write_result = {
        let mut guard = state.child.lock().map_err(|_| "sidecar state poisoned".to_string())?;
        let child = guard.as_mut().ok_or_else(|| "the sidecar is not running".to_string())?;
        child.write(line.as_bytes()).map_err(|e| e.to_string())
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

/// Restarts the sidecar with a new sampling interval, resuming the same
/// session (a short "Paused" gap is recorded for the restart itself, the
/// same honest gap `--record --resume` already produces -- no energy is
/// invented across it). Changing the interval has no live "reconfigure"
/// path in the .NET engine (it is fixed for a session's sampling loop), so
/// a restart is the actual, honest mechanism, not a workaround.
#[tauri::command]
async fn restart_sidecar(app: AppHandle, state: State<'_, SidecarState>, interval_ms: u64) -> Result<(), String> {
    if interval_ms < 250 {
        return Err("interval_ms must be at least 250.".into());
    }

    {
        let mut guard = state.child.lock().map_err(|_| "sidecar state poisoned".to_string())?;
        if let Some(child) = guard.as_mut() {
            let _ = child.write(b"{\"id\":0,\"cmd\":\"shutdown\"}\n");
        }
        *guard = None;
    }
    tokio::time::sleep(RESTART_SETTLE).await;

    spawn_sidecar(
        &app,
        vec!["--serve".into(), "--interval".into(), interval_ms.to_string(), "--resume".into()],
    )
}

/// Restarts the sidecar elevated (a real Windows UAC prompt), for a user who
/// explicitly wants complete CPU package power on a machine where it reads
/// 0 W unelevated. Only this small background process restarts elevated --
/// never the UI window itself (Part I: "do not run the entire desktop UI
/// elevated").
#[tauri::command]
async fn restart_sidecar_elevated(state: State<'_, SidecarState>) -> Result<(), String> {
    {
        let mut guard = state.child.lock().map_err(|_| "sidecar state poisoned".to_string())?;
        if let Some(child) = guard.as_mut() {
            let _ = child.write(b"{\"id\":0,\"cmd\":\"shutdown\"}\n");
        }
        *guard = None;
    }
    tokio::time::sleep(RESTART_SETTLE).await;

    // Tauri places the resolved sidecar binary next to this app's own exe,
    // under its plain name (no target-triple suffix) in both `tauri dev`
    // and a bundled build -- resolved directly rather than through the
    // shell plugin's command builder, so this does not depend on an
    // internal API surface that may change between plugin versions.
    let exe_dir = std::env::current_exe()
        .map_err(|e| format!("could not resolve this app's own path: {e}"))?
        .parent()
        .ok_or("this app's exe has no parent directory")?
        .to_path_buf();
    let sidecar_path = exe_dir.join("wattscope-probe.exe");
    if !sidecar_path.exists() {
        return Err(format!("sidecar binary not found at {}", sidecar_path.display()));
    }

    // Windows' `runas` verb is the standard, explicit way to request a real
    // UAC elevation prompt; PowerShell's Start-Process exposes it directly.
    // This still only elevates the small Probe process, never this window
    // (Part I: "do not run the entire desktop UI elevated").
    let quoted_path = sidecar_path.to_string_lossy().replace('\'', "''");
    std::process::Command::new("powershell")
        .args([
            "-NoProfile",
            "-Command",
            &format!("Start-Process -FilePath '{quoted_path}' -ArgumentList '--serve','--resume' -Verb RunAs"),
        ])
        .spawn()
        .map_err(|e| format!("failed to request elevation: {e}"))?;

    // The elevated process is now separate and untracked (Windows gives no
    // handle back across a UAC elevation boundary) -- this app can only
    // observe its effect via the next sidecar_request/tick showing complete
    // CPU data. Documented limitation; see PROJECT_STATE.md.
    Ok(())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .manage(SidecarState::new())
        .setup(|app| {
            spawn_sidecar(&app.handle().clone(), vec!["--serve".into()])
                .map_err(|e| std::io::Error::new(std::io::ErrorKind::Other, e))?;
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![sidecar_request, restart_sidecar, restart_sidecar_elevated])
        .on_window_event(|window, event| {
            // Ask the sidecar to stop cleanly (flush + close the session) as
            // soon as the window starts closing -- the same graceful path
            // --record's own Ctrl+C uses.
            if let tauri::WindowEvent::CloseRequested { .. } = event {
                let state = window.app_handle().state::<SidecarState>();
                if let Ok(mut guard) = state.child.lock() {
                    if let Some(child) = guard.as_mut() {
                        let _ = child.write(b"{\"id\":0,\"cmd\":\"shutdown\"}\n");
                    }
                };
            }
        })
        .run(tauri::generate_context!())
        .expect("error while running the WattScope application");
}
