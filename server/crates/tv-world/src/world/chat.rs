//! Local and world chat, and the in-memory history of world chat.

use super::World;
use super::validate::invalid;
use crate::error::WorldError;
use crate::event::{Audience, Event, EventKind};
use crate::types::{ChatChannel, ChatEntry, EntityId};

impl World {
    pub(super) fn chat(
        &mut self,
        actor: EntityId,
        channel: ChatChannel,
        text: &str,
        now_ms: i64,
    ) -> Result<Vec<Event>, WorldError> {
        let text = text.trim();
        let max = self.config.limits.max_chat_chars;
        if text.is_empty() {
            return Err(invalid("the message is empty"));
        }
        if text.chars().count() > max {
            return Err(invalid(format!("the message is longer than {max} characters")));
        }

        let speaker = self.actor(actor)?;
        let entry = ChatEntry {
            sender_avatar: speaker.id,
            sender_user: speaker.user_id.clone(),
            sender_name: speaker.name.clone(),
            sender_kind: speaker.kind,
            channel,
            text: text.to_owned(),
            timestamp_ms: now_ms,
        };

        let audience = match channel {
            ChatChannel::Local => {
                let origin = speaker.transform.position;
                let radius = self.config.limits.local_chat_radius;
                Audience::Set(
                    self.avatars
                        .values()
                        .filter(|p| p.avatar.transform.position.distance(origin) <= radius)
                        .map(|p| p.avatar.id)
                        .collect(),
                )
            }
            ChatChannel::World => {
                self.retain_chat(entry.clone(), now_ms);
                Audience::All
            }
        };
        Ok(vec![Event::new(audience, EventKind::Chat(entry))])
    }

    fn retain_chat(&mut self, entry: ChatEntry, now_ms: i64) {
        if self.config.chat_retention_ms <= 0 {
            return;
        }
        self.chat.push_back(entry);
        self.prune_chat(now_ms);
    }

    /// Drops world chat older than the retention period and beyond the
    /// newest `chat_history_max`.
    pub(super) fn prune_chat(&mut self, now_ms: i64) {
        let retention = self.config.chat_retention_ms;
        if retention <= 0 {
            self.chat.clear();
            return;
        }
        self.chat.retain(|entry| !expired(entry, retention, now_ms));
        let excess = self.chat.len().saturating_sub(self.config.chat_history_max);
        self.chat.drain(..excess);
    }

    pub(super) fn retained_chat(&self, since_ms: i64, limit: usize, now_ms: i64) -> Vec<ChatEntry> {
        let retention = self.config.chat_retention_ms;
        if retention <= 0 {
            return Vec::new();
        }
        let matching: Vec<&ChatEntry> = self
            .chat
            .iter()
            .filter(|entry| since_ms == 0 || entry.timestamp_ms > since_ms)
            .filter(|entry| !expired(entry, retention, now_ms))
            .collect();
        let skip = if limit == 0 { 0 } else { matching.len().saturating_sub(limit) };
        matching.into_iter().skip(skip).cloned().collect()
    }
}

fn expired(entry: &ChatEntry, retention_ms: i64, now_ms: i64) -> bool {
    now_ms.saturating_sub(entry.timestamp_ms) > retention_ms
}
