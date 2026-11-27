//! Write-behind of world changes.
//!
//! The world task notes which objects the events of each step touched. Every
//! few seconds, and on shutdown, it turns those ids into one
//! [`tv_store::Store::save_objects`] batch: the current state of the objects
//! that still exist, the ids of those that are gone, and the id counter.

use std::collections::BTreeSet;

use tv_world::{EntityId, Event, EventKind, Object};

/// One `save_objects` call.
#[derive(Clone, Debug, PartialEq)]
pub struct SaveBatch {
    pub upserts: Vec<Object>,
    pub deletes: Vec<EntityId>,
    pub next_entity_id: EntityId,
}

/// Which objects changed since the last flush.
#[derive(Debug)]
pub struct Dirty {
    ids: BTreeSet<EntityId>,
    saved_next_id: EntityId,
}

impl Dirty {
    /// `saved_next_id` is the counter as the store already has it.
    pub fn new(saved_next_id: EntityId) -> Self {
        Dirty { ids: BTreeSet::new(), saved_next_id }
    }

    /// Notes the objects an event touched.
    pub fn note(&mut self, event: &Event) {
        match &event.kind {
            EventKind::ObjectCreated { object, .. } | EventKind::ObjectUpdated { object, .. } => {
                self.ids.insert(object.id);
            }
            EventKind::ObjectDeleted { id, .. } => {
                self.ids.insert(*id);
            }
            _ => {}
        }
    }

    pub fn note_all<'a>(&mut self, events: impl IntoIterator<Item = &'a Event>) {
        for e in events {
            self.note(e);
        }
    }

    /// The batch to save, or `None` if nothing changed. `lookup` gives the
    /// current state of an object, or `None` if it no longer exists. Clears
    /// the dirty set: the caller must hand the batch to the store.
    pub fn take(
        &mut self,
        next_entity_id: EntityId,
        lookup: impl Fn(EntityId) -> Option<Object>,
    ) -> Option<SaveBatch> {
        if self.ids.is_empty() && next_entity_id == self.saved_next_id {
            return None;
        }
        let mut batch = SaveBatch { upserts: Vec::new(), deletes: Vec::new(), next_entity_id };
        for id in std::mem::take(&mut self.ids) {
            match lookup(id) {
                Some(object) => batch.upserts.push(object),
                None => batch.deletes.push(id),
            }
        }
        self.saved_next_id = next_entity_id;
        Some(batch)
    }

    /// Puts a batch that could not be saved back, so the next flush retries it.
    pub fn restore(&mut self, batch: &SaveBatch, saved_next_id: EntityId) {
        self.ids.extend(batch.upserts.iter().map(|o| o.id));
        self.ids.extend(batch.deletes.iter().copied());
        self.saved_next_id = saved_next_id;
    }

    pub fn is_clean(&self, next_entity_id: EntityId) -> bool {
        self.ids.is_empty() && next_entity_id == self.saved_next_id
    }
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;

    use tv_world::{Audience, ObjectSpec, UserId};

    use super::*;

    fn object(id: EntityId) -> Object {
        Object { id, owner: UserId("u".into()), spec: ObjectSpec::default() }
    }

    fn ev(kind: EventKind) -> Event {
        Event::new(Audience::All, kind)
    }

    #[test]
    fn nothing_to_save() {
        let mut d = Dirty::new(5);
        d.note(&ev(EventKind::AvatarLeft { id: 3 }));
        d.note(&ev(EventKind::Ack));
        assert!(d.is_clean(5));
        assert_eq!(d.take(5, |_| None), None);
    }

    #[test]
    fn counter_alone_is_saved() {
        let mut d = Dirty::new(5);
        let batch = d.take(6, |_| None).unwrap();
        assert_eq!(batch, SaveBatch { upserts: vec![], deletes: vec![], next_entity_id: 6 });
        assert_eq!(d.take(6, |_| None), None);
    }

    #[test]
    fn current_state_and_deletions() {
        let mut d = Dirty::new(1);
        d.note_all(&[
            ev(EventKind::ObjectCreated { object: object(1), by: 9 }),
            ev(EventKind::ObjectCreated { object: object(2), by: 9 }),
            ev(EventKind::ObjectUpdated { object: object(1), by: 9 }),
            ev(EventKind::ObjectDeleted { id: 2, by: 9 }),
            ev(EventKind::ObjectDeleted { id: 3, by: 9 }),
        ]);
        let mut world = HashMap::new();
        let mut updated = object(1);
        updated.spec.name = "now".into();
        world.insert(1, updated.clone());
        let batch = d.take(10, |id| world.get(&id).cloned()).unwrap();
        assert_eq!(batch.upserts, vec![updated]);
        assert_eq!(batch.deletes, vec![2, 3]);
        assert_eq!(batch.next_entity_id, 10);
        assert!(d.is_clean(10));
    }

    #[test]
    fn failed_batch_is_retried() {
        let mut d = Dirty::new(1);
        d.note(&ev(EventKind::ObjectCreated { object: object(4), by: 9 }));
        let batch = d.take(5, |id| Some(object(id))).unwrap();
        d.restore(&batch, 1);
        assert!(!d.is_clean(5));
        let again = d.take(5, |id| Some(object(id))).unwrap();
        assert_eq!(again, batch);
    }
}
