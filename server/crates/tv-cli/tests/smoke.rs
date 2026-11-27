//! `tv-cli smoke` against the in-test fake world.

mod common;

use std::time::Duration;

use common::fake_world::{self, OWNER_TOKEN, Quirks};
use tv_cli::{
    cli::{EXIT_CONNECT, EXIT_FAILED, EXIT_OK},
    smoke::{self, CHECKS, Outcome, SmokeOptions},
};

fn options(url: &str) -> SmokeOptions {
    let mut o = SmokeOptions::new(url);
    o.timeout = Duration::from_secs(3);
    o.quiet = Duration::from_millis(300);
    o
}

async fn run(o: SmokeOptions) -> (smoke::SmokeReport, String) {
    let mut out = Vec::new();
    let report = smoke::run(o, &mut out).await;
    (report, String::from_utf8(out).unwrap())
}

#[tokio::test]
async fn smoke_passes_against_a_well_behaved_world() {
    let url = fake_world::start(Quirks::default()).await;
    let mut o = options(&url);
    o.owner_token = Some(OWNER_TOKEN.into());
    let (report, out) = run(o).await;
    for name in CHECKS {
        assert!(report.passed(name), "{name} did not pass:\n{out}");
    }
    assert_eq!(report.exit_code(), EXIT_OK, "{out}");
    assert!(out.contains("PASS  observe"), "{out}");
    assert!(out.contains("17 passed, 0 failed, 0 skipped"), "{out}");
}

#[tokio::test]
async fn skipped_checks_and_missing_owner_token_are_skips() {
    let url = fake_world::start(Quirks::default()).await;
    let mut o = options(&url);
    o.skip = vec!["moveto".into(), "object-create".into()];
    let (report, out) = run(o).await;
    assert!(matches!(report.outcome("moveto"), Some(Outcome::Skip(_))), "{out}");
    assert!(matches!(report.outcome("admin"), Some(Outcome::Skip(_))), "{out}");
    // Depends on object-create.
    assert!(matches!(report.outcome("object-update"), Some(Outcome::Skip(_))), "{out}");
    // chat-local moves C itself when moveto was skipped.
    assert!(report.passed("chat-local"), "{out}");
    assert!(report.passed("observe"), "{out}");
    assert_eq!(report.exit_code(), EXIT_OK, "{out}");
}

#[tokio::test]
async fn smoke_fails_when_own_states_leak() {
    let url = fake_world::start(Quirks { leak_own_states: true, ..Default::default() }).await;
    let (report, out) = run(options(&url)).await;
    let Some(Outcome::Fail(why)) = report.outcome("avatar-state") else {
        panic!("avatar-state should fail:\n{out}")
    };
    assert!(why.contains("A's own avatar appeared"), "{why}");
    assert!(out.contains("FAIL  avatar-state"), "{out}");
    assert_eq!(report.exit_code(), EXIT_FAILED);
}

#[tokio::test]
async fn smoke_fails_when_reply_to_is_on_every_copy() {
    let url = fake_world::start(Quirks { reply_to_everyone: true, ..Default::default() }).await;
    let (report, out) = run(options(&url)).await;
    for name in ["chat-world", "object-create"] {
        let Some(Outcome::Fail(why)) = report.outcome(name) else {
            panic!("{name} should fail:\n{out}")
        };
        assert!(why.contains("reply_to"), "{name}: {why}");
    }
    assert_eq!(report.exit_code(), EXIT_FAILED);
}

#[tokio::test]
async fn smoke_cannot_join_exits_2() {
    let url = common::dead_url().await;
    let (report, out) = run(options(&url)).await;
    assert!(!report.joined);
    assert!(out.contains("FAIL  join"), "{out}");
    assert_eq!(report.exit_code(), EXIT_CONNECT);
}
