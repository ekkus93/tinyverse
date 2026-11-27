//! The `bot circle` and `bot wander` behaviours.

use std::{io::Write, time::Duration};

use rand::{RngExt, SeedableRng, rngs::StdRng};
use tokio::time::{Instant, MissedTickBehavior};
use tv_proto::v1::{
    AvatarState, AvatarTransform, Chat, ChatChannel, MoveMode, MoveTo, ServerMessage,
    client_message::Body as CBody, server_message::Body as SBody,
};

use crate::{
    cli::{Failure, emit},
    client::Client,
    format::{Roster, describe, vec},
    motion::{CirclePlan, SPEED_MARGIN, V3, Walker, speed_limit},
};

/// Rate at which the bots stream `AvatarState` while moving.
pub const STATE_INTERVAL: Duration = Duration::from_millis(100);

/// How far from spawn the wander bot goes.
pub const WANDER_RADIUS: f32 = 20.0;

const PHRASES: &[&str] = &[
    "Nice day for a walk.",
    "Anyone around?",
    "Just wandering.",
    "Hello from the bot!",
    "I like it here.",
];

/// Where the avatar starts: its own transform from Welcome, else the spawn point.
fn start_of(client: &Client) -> (V3, f32) {
    let w = client.welcome();
    match w.avatar.as_ref().and_then(|a| a.transform.as_ref()) {
        Some(t) if t.position.is_some() => (t.position.into(), t.yaw),
        _ => (w.world.as_ref().and_then(|wi| wi.spawn_point).into(), 0.0),
    }
}

fn spawn_of(client: &Client) -> V3 {
    let w = client.welcome();
    match w.world.as_ref().and_then(|wi| wi.spawn_point) {
        Some(p) => p.into(),
        None => start_of(client).0,
    }
}

fn state(t: AvatarTransform) -> CBody {
    CBody::AvatarState(AvatarState { transform: Some(t) })
}

fn deadline(d: Option<Duration>) -> Option<Instant> {
    d.map(|d| Instant::now() + d)
}

async fn until(d: Option<Instant>) {
    match d {
        Some(d) => tokio::time::sleep_until(d).await,
        None => std::future::pending().await,
    }
}

/// Prints chat and errors, which are what a bot operator cares about.
fn report(m: &ServerMessage, roster: &mut Roster, out: &mut dyn Write) {
    roster.apply(m);
    match &m.body {
        Some(SBody::ChatMessage(_))
        | Some(SBody::Error(_))
        | Some(SBody::Kicked(_))
        | Some(SBody::AvatarCorrection(_))
        | Some(SBody::AvatarJoined(_))
        | Some(SBody::AvatarLeft(_)) => {
            for l in describe(m, roster) {
                emit(out, l);
            }
        }
        _ => {}
    }
    roster.forget_left(m);
}

/// Streams `AvatarState` at 10 Hz around a circle of `radius` centred on the
/// spawn point.
pub async fn circle(
    mut client: Client,
    radius: f32,
    mode: MoveMode,
    duration: Option<Duration>,
    out: &mut dyn Write,
) -> Result<(), Failure> {
    let limit = speed_limit(client.welcome().limits.as_ref(), mode);
    let speed = limit * SPEED_MARGIN;
    let (start, yaw) = start_of(&client);
    let center = spawn_of(&client);
    let mut plan = CirclePlan::new(center, start, yaw, radius, speed, mode);
    let mut roster = Roster::from_client(&client);
    emit(
        out,
        format!(
            "circling {} at radius {radius} m, {speed:.2} m/s (limit {limit} m/s)",
            vec(&Some(plan.center().into()))
        ),
    );

    let end = deadline(duration);
    let mut tick = tokio::time::interval(STATE_INTERVAL);
    tick.set_missed_tick_behavior(MissedTickBehavior::Delay);
    let mut last = Instant::now();
    let ctrl_c = tokio::signal::ctrl_c();
    tokio::pin!(ctrl_c);
    loop {
        tokio::select! {
            now = tick.tick() => {
                let dt = (now - last).as_secs_f32();
                last = now;
                client.send(state(plan.step(dt)))?;
            }
            m = client.recv() => {
                let m = m?;
                if let Some(SBody::AvatarCorrection(c)) = &m.body {
                    let p = c.transform.as_ref().map(|t| V3::from(t.position)).unwrap_or(plan.position());
                    plan.correct(p);
                }
                report(&m, &mut roster, out);
            }
            _ = until(end) => break,
            _ = &mut ctrl_c => break,
        }
    }
    client.send(state(plan.idle()))?;
    client.close().await;
    Ok(())
}

/// A random point within `radius` of `center` (horizontally), kept inside
/// the world bounds with a 1 m margin.
fn random_point(rng: &mut StdRng, client: &Client, center: V3, radius: f32) -> V3 {
    let r = radius * rng.random_range(0.0f32..1.0).sqrt();
    let a = rng.random_range(0.0f32..std::f32::consts::TAU);
    let mut p = center + V3::new(a.sin() * r, 0.0, a.cos() * r);
    if let Some(w) = &client.welcome().world
        && let (Some(lo), Some(hi)) = (w.bounds_min, w.bounds_max)
        && hi.x - lo.x > 2.0
        && hi.z - lo.z > 2.0
    {
        p.x = p.x.clamp(lo.x + 1.0, hi.x - 1.0);
        p.z = p.z.clamp(lo.z + 1.0, hi.z - 1.0);
    }
    p
}

/// Walks to random points near spawn, pausing between them and sometimes
/// saying something in local chat.
pub async fn wander(
    mut client: Client,
    duration: Option<Duration>,
    use_moveto: bool,
    out: &mut dyn Write,
) -> Result<(), Failure> {
    let mut rng = StdRng::seed_from_u64(rand::random());
    let limit = speed_limit(client.welcome().limits.as_ref(), MoveMode::Walk);
    let speed = limit * SPEED_MARGIN;
    let spawn = spawn_of(&client);
    let (start, yaw) = start_of(&client);
    let me = client.avatar_id();
    let mut walker = Walker::new(start, yaw, speed, MoveMode::Walk);
    let mut roster = Roster::from_client(&client);
    let end = deadline(duration);
    let ctrl_c = tokio::signal::ctrl_c();
    tokio::pin!(ctrl_c);

    let mut tick = tokio::time::interval(STATE_INTERVAL);
    tick.set_missed_tick_behavior(MissedTickBehavior::Delay);
    let mut last = Instant::now();
    let mut last_sent = Instant::now() - Duration::from_secs(10);
    // When paused, the time to set off again.
    let mut pause_until: Option<Instant> = Some(Instant::now());
    // For MoveTo: the target and when to give up on it.
    let mut moveto: Option<(V3, Instant, u32)> = None;

    loop {
        tokio::select! {
            now = tick.tick() => {
                let dt = (now - last).as_secs_f32();
                last = now;
                if let Some(p) = pause_until {
                    if now < p {
                        if !use_moveto && now - last_sent >= Duration::from_secs(1) {
                            client.send(state(walker.idle()))?;
                            last_sent = now;
                        }
                        continue;
                    }
                    pause_until = None;
                    let target = random_point(&mut rng, &client, spawn, WANDER_RADIUS);
                    emit(out, format!("walking to {}", vec(&Some(target.into()))));
                    if use_moveto {
                        let seq = client.send(CBody::MoveTo(MoveTo {
                            target: Some(target.into()),
                            mode: MoveMode::Walk as i32,
                        }))?;
                        let eta = walker.pos.hdist(target) / limit;
                        let give_up = now + Duration::from_secs_f32(eta * 2.0 + 10.0);
                        moveto = Some((target, give_up, seq));
                    } else {
                        walker.target = Some(target);
                    }
                }
                let arrived = if use_moveto {
                    match moveto {
                        Some((target, _, _)) if walker.pos.hdist(target) < 0.6 => true,
                        Some((_, give_up, _)) if now > give_up => {
                            emit(out, "MoveTo did not arrive in time; picking another point");
                            true
                        }
                        _ => false,
                    }
                } else {
                    let (t, arrived) = walker.step(dt);
                    client.send(state(t))?;
                    last_sent = now;
                    arrived
                };
                if arrived {
                    moveto = None;
                    if rng.random_range(0.0f32..1.0) < 0.25 {
                        let text = PHRASES[rng.random_range(0..PHRASES.len())];
                        client.send(CBody::Chat(Chat {
                            channel: ChatChannel::Local as i32,
                            text: text.to_string(),
                        }))?;
                    }
                    let secs = rng.random_range(1.0f32..4.0);
                    pause_until = Some(now + Duration::from_secs_f32(secs));
                }
            }
            m = client.recv() => {
                let m = m?;
                match &m.body {
                    Some(SBody::AvatarCorrection(c)) => {
                        if let Some(t) = &c.transform {
                            walker.pos = t.position.into();
                            walker.yaw = t.yaw;
                        }
                    }
                    // Our own state only comes back while the server moves us.
                    Some(SBody::AvatarStates(s)) if use_moveto => {
                        for e in s.states.iter().filter(|e| e.avatar_id == me) {
                            if let Some(t) = &e.transform {
                                walker.pos = t.position.into();
                                walker.yaw = t.yaw;
                            }
                        }
                    }
                    Some(SBody::Error(_)) => {
                        if let Some((_, _, seq)) = moveto && m.reply_to == seq {
                            moveto = None;
                            pause_until = Some(Instant::now() + Duration::from_secs(1));
                        }
                    }
                    _ => {}
                }
                report(&m, &mut roster, out);
            }
            _ = until(end) => break,
            _ = &mut ctrl_c => break,
        }
    }
    if use_moveto {
        client.send(CBody::Stop(tv_proto::v1::Stop {}))?;
    } else {
        walker.target = None;
        client.send(state(walker.idle()))?;
    }
    client.close().await;
    Ok(())
}
