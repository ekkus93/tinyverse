//! The world task: the single owner of the [`World`].
//!
//! Sessions send it [`ToWorld`] messages over one channel. It applies them,
//! ticks the world at 20 Hz and fans the resulting events out to every
//! session's bounded outbound queue as ready-to-send frames. There is no lock
//! around world state (SPEC 7). A session whose queue is full is dropped
//! rather than slowing the world down.

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use tokio::sync::{mpsc, oneshot, watch};
use tokio::time::{Instant, MissedTickBehavior};
use tv_proto::v1::{self, server_message::Body as S};
use tv_proto::{Encoding, Frame};
use tv_world::{Audience, Command, EntityId, Event, EventKind, JoinRequest, Role, UserId, World};

use crate::convert::{self, Refusal};
use crate::now_ms;
use crate::persist::{Dirty, SaveBatch};
use crate::store::StoreHandle;

/// What the world task puts into a session's outbound queue.
#[derive(Debug)]
pub enum Outbound {
    Frame(Frame),
    /// Several frames that must arrive together and in order (the join
    /// sequence), taking only one slot of the bounded queue.
    Frames(Vec<Frame>),
    /// Send nothing more and close the socket.
    Close,
}

/// A message from a session to the world task.
#[derive(Debug)]
pub enum ToWorld {
    Join(Box<JoinMsg>),
    Command { avatar: EntityId, seq: u32, command: Command },
    Leave { avatar: EntityId },
}

/// A request to put an authenticated user into the world.
#[derive(Debug)]
pub struct JoinMsg {
    pub request: JoinRequest,
    /// Goes into `Welcome.session_token` (empty for token logins).
    pub session_token: String,
    pub encoding: Encoding,
    pub hello_seq: u32,
    pub outbound: mpsc::Sender<Outbound>,
    pub reply: oneshot::Sender<Result<Joined, Refusal>>,
}

/// A successful join.
#[derive(Clone, Debug)]
pub struct Joined {
    pub avatar: EntityId,
    pub user_id: UserId,
    pub role: Role,
}

/// A server message encoded at most once per encoding.
struct Encoded {
    msg: v1::ServerMessage,
    binary: Option<Frame>,
    json: Option<Frame>,
}

impl Encoded {
    fn new(reply_to: u32, body: S) -> Self {
        Encoded { msg: v1::ServerMessage { reply_to, body: Some(body) }, binary: None, json: None }
    }

    fn frame(&mut self, encoding: Encoding) -> Frame {
        let slot = match encoding {
            Encoding::Binary => &mut self.binary,
            Encoding::Json => &mut self.json,
        };
        slot.get_or_insert_with(|| tv_proto::encode(&self.msg, encoding)).clone()
    }
}

struct SessionTx {
    tx: mpsc::Sender<Outbound>,
    encoding: Encoding,
}

/// What [`Fanout::deliver`] could not handle itself.
#[derive(Debug, Default, PartialEq)]
pub struct Delivery {
    /// Events addressed to the host process.
    pub host: Vec<EventKind>,
    /// Avatars whose session was dropped because its queue was full or
    /// closed. They are no longer registered; the caller removes them from
    /// the world.
    pub dropped: Vec<EntityId>,
}

/// The outbound queues of all joined sessions, by avatar id.
#[derive(Default)]
pub struct Fanout {
    sessions: HashMap<EntityId, SessionTx>,
}

impl Fanout {
    pub fn insert(&mut self, avatar: EntityId, tx: mpsc::Sender<Outbound>, encoding: Encoding) {
        self.sessions.insert(avatar, SessionTx { tx, encoding });
    }

    pub fn remove(&mut self, avatar: EntityId) -> bool {
        self.sessions.remove(&avatar).is_some()
    }

    pub fn contains(&self, avatar: EntityId) -> bool {
        self.sessions.contains_key(&avatar)
    }

    pub fn len(&self) -> usize {
        self.sessions.len()
    }

    pub fn is_empty(&self) -> bool {
        self.sessions.is_empty()
    }

    /// Closes every session.
    pub fn close_all(&mut self) {
        for (_, s) in self.sessions.drain() {
            let _ = s.tx.try_send(Outbound::Close);
        }
    }

    fn recipients(&self, audience: &Audience) -> Vec<EntityId> {
        let mut ids: Vec<EntityId> = match audience {
            Audience::All => self.sessions.keys().copied().collect(),
            Audience::AllExcept(x) => self.sessions.keys().copied().filter(|id| id != x).collect(),
            Audience::Only(x) => vec![*x],
            Audience::Set(xs) => xs.clone(),
            Audience::Host => Vec::new(),
        };
        ids.sort_unstable();
        ids.dedup();
        ids
    }

    /// Queues `item` for `avatar`. On a full or closed queue the session is
    /// unregistered and `false` returned.
    fn push(&mut self, avatar: EntityId, item: Outbound) -> bool {
        let Some(s) = self.sessions.get(&avatar) else { return true };
        match s.tx.try_send(item) {
            Ok(()) => true,
            Err(mpsc::error::TrySendError::Full(_)) => {
                tracing::warn!(avatar, "outbound queue full; dropping the client");
                self.sessions.remove(&avatar);
                false
            }
            Err(mpsc::error::TrySendError::Closed(_)) => {
                self.sessions.remove(&avatar);
                false
            }
        }
    }

    /// Sends one message to one avatar. Returns the avatar if it was dropped.
    pub fn send(&mut self, avatar: EntityId, reply_to: u32, body: S) -> Option<EntityId> {
        let encoding = self.sessions.get(&avatar)?.encoding;
        let frame = tv_proto::encode(&v1::ServerMessage { reply_to, body: Some(body) }, encoding);
        (!self.push(avatar, Outbound::Frame(frame))).then_some(avatar)
    }

    /// Delivers events to their audiences. `cause` is the actor and `seq` of
    /// the command that produced them: the actor's copy of each event carries
    /// that `seq` as `reply_to`, everyone else's carries 0.
    pub fn deliver(
        &mut self,
        events: &[Event],
        cause: Option<(EntityId, u32)>,
        now_ms: i64,
    ) -> Delivery {
        let mut out = Delivery::default();
        for event in events {
            if event.audience == Audience::Host {
                out.host.push(event.kind.clone());
                continue;
            }
            let recipients = self.recipients(&event.audience);
            let reply_to_for = |id: EntityId| match cause {
                Some((actor, seq)) if actor == id => seq,
                _ => 0,
            };
            match &event.kind {
                EventKind::AvatarStates(updates) => {
                    let mut shared: HashMap<u32, Option<Encoded>> = HashMap::new();
                    for id in recipients {
                        let Some(encoding) = self.sessions.get(&id).map(|s| s.encoding) else {
                            continue;
                        };
                        let reply_to = reply_to_for(id);
                        let own = updates.iter().any(|u| u.id == id && !u.server_driven);
                        let frame = if own {
                            convert::avatar_states_to(updates, Some(id), now_ms)
                                .map(|b| Encoded::new(reply_to, b).frame(encoding))
                        } else {
                            shared
                                .entry(reply_to)
                                .or_insert_with(|| {
                                    convert::avatar_states_to(updates, None, now_ms)
                                        .map(|b| Encoded::new(reply_to, b))
                                })
                                .as_mut()
                                .map(|e| e.frame(encoding))
                        };
                        if let Some(frame) = frame
                            && !self.push(id, Outbound::Frame(frame))
                        {
                            out.dropped.push(id);
                        }
                    }
                }
                kind => {
                    let Some(body) = convert::event_body(kind, now_ms) else { continue };
                    let kicked = matches!(kind, EventKind::Kicked { .. });
                    let mut others = Encoded::new(0, body.clone());
                    let mut actor = cause.map(|(_, seq)| Encoded::new(seq, body));
                    for id in recipients {
                        let Some(encoding) = self.sessions.get(&id).map(|s| s.encoding) else {
                            continue;
                        };
                        let frame = match (&mut actor, cause) {
                            (Some(a), Some((actor_id, _))) if actor_id == id => a.frame(encoding),
                            _ => others.frame(encoding),
                        };
                        if !self.push(id, Outbound::Frame(frame)) {
                            out.dropped.push(id);
                        } else if kicked {
                            // The world has already removed the avatar.
                            let _ = self.push(id, Outbound::Close);
                            self.sessions.remove(&id);
                        }
                    }
                }
            }
        }
        out
    }
}

/// Timing knobs of the world task.
#[derive(Clone, Debug)]
pub struct HubTiming {
    pub tick: Duration,
    pub flush: Duration,
}

impl Default for HubTiming {
    fn default() -> Self {
        HubTiming {
            tick: Duration::from_secs(1) / convert::TICK_RATE_HZ,
            flush: Duration::from_secs(5),
        }
    }
}

/// The world task's state.
pub struct Hub {
    world: World,
    fanout: Fanout,
    dirty: Dirty,
    store: StoreHandle,
    users: Arc<AtomicUsize>,
    failed_tx: mpsc::UnboundedSender<SaveBatch>,
    failed_rx: mpsc::UnboundedReceiver<SaveBatch>,
}

impl Hub {
    /// `saved_next_id` is the id counter the store already holds.
    pub fn new(
        world: World,
        saved_next_id: EntityId,
        store: StoreHandle,
        users: Arc<AtomicUsize>,
    ) -> Self {
        let (failed_tx, failed_rx) = mpsc::unbounded_channel();
        Hub {
            world,
            fanout: Fanout::default(),
            dirty: Dirty::new(saved_next_id),
            store,
            users,
            failed_tx,
            failed_rx,
        }
    }

    /// Runs until `shutdown` turns true (or its sender is dropped), then
    /// closes every session and writes the last changes to disk.
    pub async fn run(
        mut self,
        mut rx: mpsc::Receiver<ToWorld>,
        mut shutdown: watch::Receiver<bool>,
        timing: HubTiming,
    ) {
        let mut tick = tokio::time::interval(timing.tick);
        tick.set_missed_tick_behavior(MissedTickBehavior::Delay);
        let mut flush = tokio::time::interval_at(Instant::now() + timing.flush, timing.flush);
        flush.set_missed_tick_behavior(MissedTickBehavior::Delay);
        let mut last_tick = Instant::now();
        loop {
            if *shutdown.borrow() {
                break;
            }
            tokio::select! {
                biased;
                changed = shutdown.changed() => {
                    if changed.is_err() {
                        break;
                    }
                }
                _ = tick.tick() => {
                    let now = Instant::now();
                    let dt = now.duration_since(last_tick).as_secs_f32();
                    last_tick = now;
                    let events = self.world.tick(dt, now_ms());
                    self.deliver(events, None);
                }
                _ = flush.tick() => self.flush(),
                Some(batch) = self.failed_rx.recv() => self.dirty.restore(&batch, 0),
                msg = rx.recv() => match msg {
                    Some(msg) => self.handle(msg),
                    None => break,
                },
            }
        }
        self.fanout.close_all();
        self.users.store(0, Ordering::Relaxed);
        self.final_flush().await;
    }

    fn handle(&mut self, msg: ToWorld) {
        match msg {
            ToWorld::Join(join) => self.join(*join),
            ToWorld::Command { avatar, seq, command } => {
                if !self.fanout.contains(avatar) {
                    return; // dropped or kicked meanwhile
                }
                match self.world.apply(avatar, command, now_ms()) {
                    Ok(events) => self.deliver(events, Some((avatar, seq))),
                    Err(error) => {
                        let body = Refusal::from(&error).body();
                        if let Some(dropped) = self.fanout.send(avatar, seq, body) {
                            self.drop_avatar(dropped);
                        }
                    }
                }
            }
            ToWorld::Leave { avatar } => self.drop_avatar(avatar),
        }
    }

    fn join(&mut self, join: JoinMsg) {
        let JoinMsg { request, session_token, encoding, hello_seq, outbound, reply } = join;
        let now = now_ms();
        let (id, events) = match self.world.join(request, now) {
            Ok(joined) => joined,
            Err(error) => {
                let _ = reply.send(Err(Refusal::from(&error)));
                return;
            }
        };
        let Some(avatar) = self.world.avatar(id).cloned() else {
            tracing::error!(id, "world did not keep the avatar it just created");
            let _ = reply.send(Err(Refusal::internal("join failed")));
            let events = self.world.leave(id, now);
            self.deliver(events, None);
            return;
        };

        // Welcome, the snapshot and the chat history go into the queue as
        // one item before anything else can, so no event is lost or sent
        // ahead of them.
        let config = self.world.config();
        let encode = |reply_to: u32, body: S| {
            tv_proto::encode(&v1::ServerMessage { reply_to, body: Some(body) }, encoding)
        };
        let mut frames = vec![encode(
            hello_seq,
            S::Welcome(convert::welcome(config, &avatar, session_token, now)),
        )];
        let others: Vec<_> = self
            .world
            .avatars()
            .into_iter()
            .filter(|a| a.id != id)
            .map(convert::avatar_to)
            .collect();
        let objects: Vec<_> = self.world.objects().into_iter().map(convert::object_to).collect();
        for chunk in convert::snapshot_chunks(others, objects) {
            frames.push(encode(0, S::Snapshot(chunk)));
        }
        let history = self.world.chat_history(0, 0, now);
        frames.push(encode(
            0,
            S::ChatHistory(v1::ChatHistory {
                entries: history.iter().map(convert::chat_entry_to).collect(),
            }),
        ));

        let joined = Joined { avatar: id, user_id: avatar.user_id.clone(), role: avatar.role };
        let queued = outbound.try_send(Outbound::Frames(frames)).is_ok();
        if queued && reply.send(Ok(joined)).is_ok() {
            self.fanout.insert(id, outbound, encoding);
            tracing::info!(avatar = id, user = %avatar.user_id, name = %avatar.name, "joined");
            self.deliver(events, None);
        } else {
            // The session went away while we were joining it.
            self.deliver(events, None);
            let events = self.world.leave(id, now);
            self.deliver(events, None);
        }
    }

    fn drop_avatar(&mut self, avatar: EntityId) {
        self.fanout.remove(avatar);
        let events = self.world.leave(avatar, now_ms());
        if !events.is_empty() {
            tracing::info!(avatar, "left");
        }
        self.deliver(events, None);
    }

    fn deliver(&mut self, events: Vec<Event>, cause: Option<(EntityId, u32)>) {
        let now = now_ms();
        let mut queue = vec![(events, cause)];
        while let Some((events, cause)) = queue.pop() {
            self.dirty.note_all(&events);
            let delivery = self.fanout.deliver(&events, cause, now);
            for kind in delivery.host {
                self.host_event(kind);
            }
            for id in delivery.dropped {
                queue.push((self.world.leave(id, now), None));
            }
        }
        self.users.store(self.fanout.len(), Ordering::Relaxed);
    }

    fn host_event(&mut self, kind: EventKind) {
        match kind {
            EventKind::UserBanned { user_id, reason } => {
                self.store.submit(move |s| match s.ban(&user_id, &reason) {
                    Ok(true) => tracing::info!(user = %user_id, %reason, "banned"),
                    Ok(false) => tracing::warn!(user = %user_id, "ban for an unknown user"),
                    Err(e) => tracing::error!(user = %user_id, "cannot store ban: {e}"),
                })
            }
            EventKind::UserUnbanned { user_id } => {
                self.store.submit(move |s| match s.unban(&user_id) {
                    Ok(true) => tracing::info!(user = %user_id, "unbanned"),
                    Ok(false) => tracing::warn!(user = %user_id, "unban: unknown or not banned"),
                    Err(e) => tracing::error!(user = %user_id, "cannot store unban: {e}"),
                })
            }
            EventKind::UserRoleChanged { user_id, role } => {
                self.store.submit(move |s| match s.set_role(&user_id, role) {
                    Ok(true) => tracing::info!(user = %user_id, ?role, "role changed"),
                    Ok(false) => tracing::warn!(user = %user_id, "role change for an unknown user"),
                    Err(e) => tracing::error!(user = %user_id, "cannot store role: {e}"),
                })
            }
            other => tracing::warn!(?other, "unexpected host event"),
        }
    }

    fn take_batch(&mut self) -> Option<SaveBatch> {
        let world = &self.world;
        self.dirty.take(world.next_entity_id(), |id| world.object(id).cloned())
    }

    /// Hands the changes to the store thread without waiting for the disk.
    fn flush(&mut self) {
        let Some(batch) = self.take_batch() else { return };
        let failed = self.failed_tx.clone();
        self.store.submit(move |s| {
            match s.save_objects(&batch.upserts, &batch.deletes, batch.next_entity_id) {
                Ok(()) => tracing::debug!(
                    upserts = batch.upserts.len(),
                    deletes = batch.deletes.len(),
                    "saved"
                ),
                Err(e) => {
                    tracing::error!("cannot save the world, will retry: {e}");
                    let _ = failed.send(batch);
                }
            }
        });
    }

    async fn final_flush(&mut self) {
        while let Ok(batch) = self.failed_rx.try_recv() {
            self.dirty.restore(&batch, 0);
        }
        let Some(batch) = self.take_batch() else { return };
        let saved = self
            .store
            .call(move |s| s.save_objects(&batch.upserts, &batch.deletes, batch.next_entity_id))
            .await;
        match saved {
            Ok(Ok(())) => tracing::info!("world saved"),
            Ok(Err(e)) => tracing::error!("cannot save the world on shutdown: {e}"),
            Err(e) => tracing::error!("cannot save the world on shutdown: {e}"),
        }
    }
}

#[cfg(test)]
mod tests {
    use tv_world::{AvatarStateUpdate, AvatarTransform, Object, ObjectSpec};

    use super::*;

    fn session(
        f: &mut Fanout,
        id: EntityId,
        enc: Encoding,
        cap: usize,
    ) -> mpsc::Receiver<Outbound> {
        let (tx, rx) = mpsc::channel(cap);
        f.insert(id, tx, enc);
        rx
    }

    fn drain(rx: &mut mpsc::Receiver<Outbound>) -> Vec<Result<v1::ServerMessage, &'static str>> {
        let mut out = Vec::new();
        while let Ok(item) = rx.try_recv() {
            match item {
                Outbound::Frame(f) => out.push(Ok(tv_proto::decode(&f).unwrap())),
                Outbound::Frames(fs) => {
                    out.extend(fs.iter().map(|f| Ok(tv_proto::decode(f).unwrap())))
                }
                Outbound::Close => out.push(Err("close")),
            }
        }
        out
    }

    fn object(id: EntityId) -> Object {
        Object { id, owner: UserId("u".into()), spec: ObjectSpec::default() }
    }

    #[test]
    fn reply_to_only_on_the_actors_copy() {
        let mut f = Fanout::default();
        let mut a_rx = session(&mut f, 1, Encoding::Json, 8);
        let mut b_rx = session(&mut f, 2, Encoding::Binary, 8);
        let events = vec![
            Event::new(Audience::All, EventKind::ObjectCreated { object: object(9), by: 1 }),
            Event::new(Audience::Only(1), EventKind::Ack),
        ];
        let d = f.deliver(&events, Some((1, 42)), 0);
        assert_eq!(d, Delivery::default());
        let a = drain(&mut a_rx);
        let b = drain(&mut b_rx);
        assert_eq!(a.len(), 2);
        assert_eq!(a[0].as_ref().unwrap().reply_to, 42);
        assert!(matches!(a[0].as_ref().unwrap().body, Some(S::ObjectCreated(_))));
        assert_eq!(a[1].as_ref().unwrap().reply_to, 42);
        assert!(matches!(a[1].as_ref().unwrap().body, Some(S::Ack(_))));
        assert_eq!(b.len(), 1);
        assert_eq!(b[0].as_ref().unwrap().reply_to, 0);

        // Without a cause nobody gets a reply_to.
        f.deliver(&events, None, 0);
        let reply_tos: Vec<u32> = drain(&mut a_rx)
            .into_iter()
            .chain(drain(&mut b_rx))
            .map(|m| m.unwrap().reply_to)
            .collect();
        assert_eq!(reply_tos, vec![0, 0, 0]);
    }

    #[test]
    fn audiences() {
        let mut f = Fanout::default();
        let mut rx: Vec<_> = (1..=3).map(|id| session(&mut f, id, Encoding::Json, 8)).collect();
        let left = |id| EventKind::AvatarLeft { id };
        let events = vec![
            Event::new(Audience::AllExcept(2), left(10)),
            Event::new(Audience::Set(vec![2, 3, 99]), left(11)),
            Event::new(Audience::Only(3), left(12)),
            Event::new(Audience::Host, EventKind::UserUnbanned { user_id: UserId("u".into()) }),
        ];
        let d = f.deliver(&events, None, 0);
        assert_eq!(d.host, vec![EventKind::UserUnbanned { user_id: UserId("u".into()) }]);
        let got: Vec<Vec<u64>> = rx
            .iter_mut()
            .map(|r| {
                drain(r)
                    .into_iter()
                    .map(|m| match m.unwrap().body {
                        Some(S::AvatarLeft(l)) => l.avatar_id,
                        other => panic!("{other:?}"),
                    })
                    .collect()
            })
            .collect();
        assert_eq!(got, vec![vec![10], vec![11], vec![10, 11, 12]]);
    }

    #[test]
    fn avatar_states_leave_out_own_entry() {
        let mut f = Fanout::default();
        let mut a = session(&mut f, 1, Encoding::Json, 8);
        let mut b = session(&mut f, 2, Encoding::Binary, 8);
        let mut c = session(&mut f, 3, Encoding::Json, 8);
        let t = AvatarTransform::default();
        let events = vec![Event::new(
            Audience::All,
            EventKind::AvatarStates(vec![
                AvatarStateUpdate { id: 1, transform: t.clone(), server_driven: false },
                AvatarStateUpdate { id: 2, transform: t.clone(), server_driven: true },
            ]),
        )];
        f.deliver(&events, None, 5);
        let ids = |r: &mut mpsc::Receiver<Outbound>| -> Vec<Vec<u64>> {
            drain(r)
                .into_iter()
                .map(|m| match m.unwrap().body {
                    Some(S::AvatarStates(s)) => s.states.iter().map(|e| e.avatar_id).collect(),
                    other => panic!("{other:?}"),
                })
                .collect()
        };
        assert_eq!(ids(&mut a), vec![vec![2]]);
        assert_eq!(ids(&mut b), vec![vec![1, 2]]);
        assert_eq!(ids(&mut c), vec![vec![1, 2]]);

        // A batch holding only the recipient's own entry is not sent to it.
        let events = vec![Event::new(
            Audience::All,
            EventKind::AvatarStates(vec![AvatarStateUpdate {
                id: 1,
                transform: t,
                server_driven: false,
            }]),
        )];
        f.deliver(&events, None, 5);
        assert!(ids(&mut a).is_empty());
        assert_eq!(ids(&mut b), vec![vec![1]]);
    }

    #[test]
    fn kicked_is_followed_by_close() {
        let mut f = Fanout::default();
        let mut a = session(&mut f, 1, Encoding::Json, 8);
        let mut b = session(&mut f, 2, Encoding::Json, 8);
        let events = vec![
            Event::new(Audience::Only(1), EventKind::Kicked { reason: "bye".into() }),
            Event::new(Audience::AllExcept(1), EventKind::AvatarLeft { id: 1 }),
            Event::new(Audience::Only(2), EventKind::Ack),
        ];
        let d = f.deliver(&events, Some((2, 7)), 0);
        assert!(d.dropped.is_empty());
        let a = drain(&mut a);
        assert_eq!(a.len(), 2);
        match &a[0].as_ref().unwrap().body {
            Some(S::Kicked(k)) => assert_eq!(k.reason, "bye"),
            other => panic!("{other:?}"),
        }
        assert_eq!(a[0].as_ref().unwrap().reply_to, 0);
        assert_eq!(a[1], Err("close"));
        assert!(!f.contains(1));
        let b = drain(&mut b);
        assert_eq!(b.len(), 2);
        assert_eq!(b[1].as_ref().unwrap().reply_to, 7);
    }

    #[test]
    fn full_queue_drops_the_session() {
        let mut f = Fanout::default();
        let mut slow = session(&mut f, 1, Encoding::Json, 1);
        let mut fast = session(&mut f, 2, Encoding::Json, 16);
        let ev = vec![Event::new(Audience::All, EventKind::AvatarLeft { id: 9 })];
        assert!(f.deliver(&ev, None, 0).dropped.is_empty());
        let d = f.deliver(&ev, None, 0);
        assert_eq!(d.dropped, vec![1]);
        assert!(!f.contains(1));
        assert_eq!(f.len(), 1);
        assert_eq!(drain(&mut slow).len(), 1);
        assert_eq!(drain(&mut fast).len(), 2);
        // Further events skip the dropped session.
        assert!(f.deliver(&ev, None, 0).dropped.is_empty());
    }

    #[test]
    fn closed_queue_drops_the_session() {
        let mut f = Fanout::default();
        drop(session(&mut f, 1, Encoding::Json, 4));
        assert_eq!(f.send(1, 3, S::Ack(v1::Ack {})), Some(1));
        assert!(f.is_empty());
        assert_eq!(f.send(1, 3, S::Ack(v1::Ack {})), None);
    }

    #[test]
    fn encodings_follow_the_session() {
        let mut f = Fanout::default();
        let mut a = session(&mut f, 1, Encoding::Json, 4);
        let mut b = session(&mut f, 2, Encoding::Binary, 4);
        f.deliver(&[Event::new(Audience::All, EventKind::Ack)], None, 0);
        assert!(matches!(a.try_recv().unwrap(), Outbound::Frame(Frame::Text(_))));
        assert!(matches!(b.try_recv().unwrap(), Outbound::Frame(Frame::Binary(_))));
    }
}
