//! Creating, editing and deleting objects, and who may do so.

use super::World;
use super::validate;
use crate::error::WorldError;
use crate::event::{Audience, Event, EventKind};
use crate::types::{Avatar, EntityId, Object, ObjectPatch, ObjectSpec, Role};

fn denied(message: &str) -> WorldError {
    WorldError::PermissionDenied(message.to_owned())
}

/// May `actor` edit or delete `object` at all?
fn may_touch(actor: &Avatar, object: &Object) -> Result<(), WorldError> {
    match actor.role {
        Role::Visitor => Err(denied("visitors cannot change objects")),
        Role::Owner => Ok(()),
        Role::Builder if object.owner == actor.user_id || !object.spec.locked => Ok(()),
        Role::Builder => Err(denied("the object is locked by its owner")),
    }
}

/// May `actor` change the `locked` flag of `object`?
fn may_lock(actor: &Avatar, object: &Object) -> Result<(), WorldError> {
    if actor.role == Role::Owner || object.owner == actor.user_id {
        Ok(())
    } else {
        Err(denied("only the object's owner or a world owner may lock or unlock it"))
    }
}

fn not_found(id: EntityId) -> WorldError {
    WorldError::NotFound(format!("object {id}"))
}

impl World {
    pub(super) fn object_create(
        &mut self,
        actor: EntityId,
        spec: ObjectSpec,
    ) -> Result<Vec<Event>, WorldError> {
        let creator = self.actor(actor)?;
        if creator.role < Role::Builder {
            return Err(denied("visitors cannot build"));
        }
        let owner = creator.user_id.clone();
        let spec = validate::object_spec(&self.config, spec)?;
        let max = self.config.limits.max_objects;
        if self.objects.len() >= max {
            return Err(WorldError::LimitExceeded(format!(
                "the world already holds {max} objects"
            )));
        }
        let id = self.allocate_id();
        let object = Object { id, owner, spec };
        self.objects.insert(id, object.clone());
        Ok(vec![Event::new(Audience::All, EventKind::ObjectCreated { object, by: actor })])
    }

    pub(super) fn object_update(
        &mut self,
        actor: EntityId,
        id: EntityId,
        patch: ObjectPatch,
    ) -> Result<Vec<Event>, WorldError> {
        let editor = self.actor(actor)?;
        let object = self.objects.get(&id).ok_or_else(|| not_found(id))?;
        may_touch(editor, object)?;
        if patch.locked.is_some_and(|locked| locked != object.spec.locked) {
            may_lock(editor, object)?;
        }

        let old = &object.spec;
        let merged = ObjectSpec {
            name: patch.name.unwrap_or_else(|| old.name.clone()),
            shape: patch.shape.unwrap_or(old.shape),
            position: patch.position.unwrap_or(old.position),
            rotation: patch.rotation.unwrap_or(old.rotation),
            scale: patch.scale.unwrap_or(old.scale),
            color: patch.color.unwrap_or(old.color),
            material: patch.material.unwrap_or(old.material),
            solid: patch.solid.unwrap_or(old.solid),
            locked: patch.locked.unwrap_or(old.locked),
        };
        let spec = validate::object_spec(&self.config, merged)?;

        let object = self.objects.get_mut(&id).ok_or_else(|| not_found(id))?;
        object.spec = spec;
        Ok(vec![Event::new(
            Audience::All,
            EventKind::ObjectUpdated { object: object.clone(), by: actor },
        )])
    }

    pub(super) fn object_delete(
        &mut self,
        actor: EntityId,
        id: EntityId,
    ) -> Result<Vec<Event>, WorldError> {
        let editor = self.actor(actor)?;
        let object = self.objects.get(&id).ok_or_else(|| not_found(id))?;
        may_touch(editor, object)?;
        self.objects.remove(&id);
        Ok(vec![Event::new(Audience::All, EventKind::ObjectDeleted { id, by: actor })])
    }
}
