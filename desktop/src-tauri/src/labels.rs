//! Tray-menu strings. The tray is native (not React), so it carries its own
//! small translation table for the three supported languages; the frontend
//! tells the host which language is active (`set_ui_language`).

pub struct Labels {
    pub open: &'static str,
    pub start: &'static str,
    pub stop: &'static str,
    pub settings: &'static str,
    pub exit: &'static str,
    pub state_complete: &'static str,
    pub state_partial: &'static str,
    pub state_starting: &'static str,
    pub state_stopped: &'static str,
    pub state_sensor_unavailable: &'static str,
    pub state_unavailable: &'static str,
    pub power: &'static str,
    pub session: &'static str,
    pub cost_unavailable: &'static str,
}

const EN: Labels = Labels {
    open: "Open WattScope",
    start: "Start monitoring",
    stop: "Stop monitoring",
    settings: "Settings",
    exit: "Exit WattScope",
    state_complete: "Monitoring: complete",
    state_partial: "Monitoring: partial (CPU power unavailable)",
    state_starting: "Starting monitoring...",
    state_stopped: "Monitoring stopped",
    state_sensor_unavailable: "Sensors unavailable",
    state_unavailable: "Monitor not running",
    power: "Estimated power",
    session: "Session",
    cost_unavailable: "cost n/a",
};

const DE: Labels = Labels {
    open: "WattScope öffnen",
    start: "Überwachung starten",
    stop: "Überwachung stoppen",
    settings: "Einstellungen",
    exit: "WattScope beenden",
    state_complete: "Überwachung: vollständig",
    state_partial: "Überwachung: teilweise (CPU-Leistung nicht verfügbar)",
    state_starting: "Überwachung startet...",
    state_stopped: "Überwachung gestoppt",
    state_sensor_unavailable: "Sensoren nicht verfügbar",
    state_unavailable: "Monitor läuft nicht",
    power: "Geschätzte Leistung",
    session: "Sitzung",
    cost_unavailable: "Kosten n. v.",
};

const FA: Labels = Labels {
    open: "باز کردن WattScope",
    start: "شروع پایش",
    stop: "توقف پایش",
    settings: "تنظیمات",
    exit: "خروج از WattScope",
    state_complete: "پایش: کامل",
    state_partial: "پایش: ناقص (توان پردازنده در دسترس نیست)",
    state_starting: "در حال شروع پایش...",
    state_stopped: "پایش متوقف است",
    state_sensor_unavailable: "حسگرها در دسترس نیستند",
    state_unavailable: "پایشگر در حال اجرا نیست",
    power: "توان تخمینی",
    session: "نشست",
    cost_unavailable: "هزینه: در دسترس نیست",
};

pub fn labels(lang: &str) -> &'static Labels {
    match lang {
        "de" => &DE,
        "fa" => &FA,
        _ => &EN,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn all(l: &Labels) -> Vec<&'static str> {
        vec![
            l.open, l.start, l.stop, l.settings, l.exit, l.state_complete, l.state_partial, l.state_starting, l.state_stopped,
            l.state_sensor_unavailable, l.state_unavailable, l.power, l.session, l.cost_unavailable,
        ]
    }

    #[test]
    fn every_language_has_every_label_non_empty() {
        for lang in ["en", "de", "fa"] {
            for text in all(labels(lang)) {
                assert!(!text.trim().is_empty(), "empty label in {lang}");
            }
        }
    }

    #[test]
    fn languages_are_actually_translated() {
        let en = all(labels("en"));
        for lang in ["de", "fa"] {
            let other = all(labels(lang));
            // the settings/exit/etc. strings must differ from English for at least most entries
            let same = en.iter().zip(other.iter()).filter(|(a, b)| a == b).count();
            assert!(same <= 1, "{lang} looks untranslated ({same} identical labels)");
        }
    }

    #[test]
    fn unknown_language_falls_back_to_english() {
        assert_eq!(labels("xx").open, "Open WattScope");
    }

    #[test]
    fn persian_labels_are_persian_script() {
        assert!(labels("fa").stop.chars().any(|c| ('\u{0600}'..='\u{06FF}').contains(&c)));
    }
}
