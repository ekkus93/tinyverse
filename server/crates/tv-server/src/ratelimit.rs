//! Per-connection rate limits: one token bucket per client message type.

use std::time::Instant;

/// The client message types that are rate limited. `Hello` is not: it is
/// only valid once.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum MessageType {
    AvatarState,
    MoveTo,
    Stop,
    Chat,
    ChatHistoryRequest,
    ObjectCreate,
    ObjectUpdate,
    ObjectDelete,
    Observe,
    SetAppearance,
    Admin,
    Ping,
}

impl MessageType {
    pub const ALL: [MessageType; 12] = [
        MessageType::AvatarState,
        MessageType::MoveTo,
        MessageType::Stop,
        MessageType::Chat,
        MessageType::ChatHistoryRequest,
        MessageType::ObjectCreate,
        MessageType::ObjectUpdate,
        MessageType::ObjectDelete,
        MessageType::Observe,
        MessageType::SetAppearance,
        MessageType::Admin,
        MessageType::Ping,
    ];

    /// The protocol name of the message, for error texts.
    pub fn name(self) -> &'static str {
        match self {
            MessageType::AvatarState => "AvatarState",
            MessageType::MoveTo => "MoveTo",
            MessageType::Stop => "Stop",
            MessageType::Chat => "Chat",
            MessageType::ChatHistoryRequest => "ChatHistoryRequest",
            MessageType::ObjectCreate => "ObjectCreate",
            MessageType::ObjectUpdate => "ObjectUpdate",
            MessageType::ObjectDelete => "ObjectDelete",
            MessageType::Observe => "Observe",
            MessageType::SetAppearance => "SetAppearance",
            MessageType::Admin => "Admin",
            MessageType::Ping => "Ping",
        }
    }

    fn index(self) -> usize {
        MessageType::ALL.iter().position(|t| *t == self).expect("listed in ALL")
    }
}

/// A sustained rate and a burst size.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Rate {
    pub per_second: f64,
    pub burst: f64,
}

impl Rate {
    pub const fn new(per_second: f64, burst: f64) -> Self {
        Rate { per_second, burst }
    }
}

/// The limit for each message type.
#[derive(Clone, Debug, PartialEq)]
pub struct RateLimits {
    rates: [Rate; 12],
}

impl Default for RateLimits {
    fn default() -> Self {
        let mut rates = [Rate::new(10.0, 10.0); 12];
        rates[MessageType::AvatarState.index()] = Rate::new(30.0, 30.0);
        rates[MessageType::Chat.index()] = Rate::new(3.0, 10.0);
        for t in [MessageType::ObjectCreate, MessageType::ObjectUpdate, MessageType::ObjectDelete] {
            rates[t.index()] = Rate::new(20.0, 60.0);
        }
        rates[MessageType::Observe.index()] = Rate::new(5.0, 5.0);
        RateLimits { rates }
    }
}

impl RateLimits {
    pub fn get(&self, t: MessageType) -> Rate {
        self.rates[t.index()]
    }

    pub fn set(&mut self, t: MessageType, rate: Rate) {
        self.rates[t.index()] = rate;
    }
}

#[derive(Clone, Debug)]
struct Bucket {
    rate: Rate,
    tokens: f64,
    last: Instant,
}

impl Bucket {
    fn new(rate: Rate, now: Instant) -> Self {
        Bucket { rate, tokens: rate.burst, last: now }
    }

    fn take(&mut self, now: Instant) -> bool {
        let elapsed = now.saturating_duration_since(self.last).as_secs_f64();
        self.last = now;
        self.tokens = (self.tokens + elapsed * self.rate.per_second).min(self.rate.burst);
        if self.tokens >= 1.0 {
            self.tokens -= 1.0;
            true
        } else {
            false
        }
    }
}

/// The buckets of one connection.
#[derive(Clone, Debug)]
pub struct RateLimiter {
    buckets: Vec<Bucket>,
}

impl RateLimiter {
    pub fn new(limits: &RateLimits, now: Instant) -> Self {
        RateLimiter {
            buckets: MessageType::ALL.iter().map(|t| Bucket::new(limits.get(*t), now)).collect(),
        }
    }

    /// Takes one token for a message of this type. False means "refuse it".
    pub fn allow(&mut self, t: MessageType, now: Instant) -> bool {
        self.buckets[t.index()].take(now)
    }

    /// The limit applied to this type.
    pub fn rate(&self, t: MessageType) -> Rate {
        self.buckets[t.index()].rate
    }
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::*;

    #[test]
    fn burst_then_refill() {
        let start = Instant::now();
        let mut rl = RateLimiter::new(&RateLimits::default(), start);
        for i in 0..10 {
            assert!(rl.allow(MessageType::Chat, start), "chat {i} within burst");
        }
        assert!(!rl.allow(MessageType::Chat, start));
        // 3 per second: 0.9 tokens after 300 ms, 2.1 after 700 ms.
        assert!(!rl.allow(MessageType::Chat, start + Duration::from_millis(300)));
        let t = start + Duration::from_millis(700);
        assert!(rl.allow(MessageType::Chat, t));
        assert!(rl.allow(MessageType::Chat, t));
        assert!(!rl.allow(MessageType::Chat, t));
    }

    #[test]
    fn buckets_are_independent_per_type() {
        let start = Instant::now();
        let mut rl = RateLimiter::new(&RateLimits::default(), start);
        for _ in 0..5 {
            assert!(rl.allow(MessageType::Observe, start));
        }
        assert!(!rl.allow(MessageType::Observe, start));
        assert!(rl.allow(MessageType::Chat, start));
        for _ in 0..60 {
            assert!(rl.allow(MessageType::ObjectCreate, start));
        }
        assert!(!rl.allow(MessageType::ObjectCreate, start));
        assert!(rl.allow(MessageType::ObjectUpdate, start));
    }

    #[test]
    fn refill_is_capped_at_burst() {
        let start = Instant::now();
        let mut rl = RateLimiter::new(&RateLimits::default(), start);
        let later = start + Duration::from_secs(3600);
        for _ in 0..30 {
            assert!(rl.allow(MessageType::AvatarState, later));
        }
        assert!(!rl.allow(MessageType::AvatarState, later));
    }

    #[test]
    fn default_rates() {
        let l = RateLimits::default();
        assert_eq!(l.get(MessageType::AvatarState), Rate::new(30.0, 30.0));
        assert_eq!(l.get(MessageType::Chat), Rate::new(3.0, 10.0));
        assert_eq!(l.get(MessageType::ObjectDelete), Rate::new(20.0, 60.0));
        assert_eq!(l.get(MessageType::Observe), Rate::new(5.0, 5.0));
        assert_eq!(l.get(MessageType::Admin), Rate::new(10.0, 10.0));
        assert_eq!(l.get(MessageType::Ping), Rate::new(10.0, 10.0));
    }
}
