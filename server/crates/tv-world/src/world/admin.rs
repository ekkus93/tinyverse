//! Appearance changes and the owner-only moderation commands.

use super::validate::{self, invalid};
use super::{BAN_REASON, KICK_REASON, World, ack};
use crate::error::WorldError;
use crate::event::{Audience, Event, EventKind};
use crate::types::{Appearance, EntityId, Role, UserId};

fn reason_or(reason: String, default: &str) -> String {
    let reason = reason.trim();
    if reason.is_empty() { default.to_owned() } else { reason.to_owned() }
}

impl World {
    pub(super) fn set_appearance(
        &mut self,
        actor: EntityId,
        appearance: Appearance,
    ) -> Result<Vec<Event>, WorldError> {
        let appearance = validate::appearance(appearance)?;
        let presence = self.avatars.get_mut(&actor).ok_or(WorldError::NotJoined)?;
        presence.avatar.appearance = appearance;
        Ok(vec![Event::new(Audience::All, EventKind::AvatarUpdated(presence.avatar.clone()))])
    }

    /// Checks that `actor` is an owner and `target` names a user, and returns
    /// whether the target is the actor's own user.
    fn check_admin(&self, actor: EntityId, target: &UserId) -> Result<bool, WorldError> {
        let admin = self.actor(actor)?;
        if admin.role != Role::Owner {
            return Err(WorldError::PermissionDenied("only world owners may do this".to_owned()));
        }
        if target.as_str().trim().is_empty() {
            return Err(invalid("the user id is empty"));
        }
        Ok(&admin.user_id == target)
    }

    pub(super) fn kick(
        &mut self,
        actor: EntityId,
        user_id: &UserId,
        reason: String,
    ) -> Result<Vec<Event>, WorldError> {
        if self.check_admin(actor, user_id)? {
            return Err(invalid("you cannot kick yourself"));
        }
        let target = self
            .avatar_of_user(user_id)
            .map(|a| a.id)
            .ok_or_else(|| WorldError::NotFound(format!("user {user_id} is not present")))?;
        let mut events = self.remove_avatar(target, Some(reason_or(reason, KICK_REASON)));
        events.push(ack(actor));
        Ok(events)
    }

    pub(super) fn ban(
        &mut self,
        actor: EntityId,
        user_id: UserId,
        reason: String,
    ) -> Result<Vec<Event>, WorldError> {
        if self.check_admin(actor, &user_id)? {
            return Err(invalid("you cannot ban yourself"));
        }
        let reason = reason_or(reason, BAN_REASON);
        let target = self.avatar_of_user(&user_id).map(|a| a.id);
        let mut events = vec![Event::new(
            Audience::Host,
            EventKind::UserBanned { user_id, reason: reason.clone() },
        )];
        if let Some(target) = target {
            events.extend(self.remove_avatar(target, Some(reason)));
        }
        events.push(ack(actor));
        Ok(events)
    }

    pub(super) fn unban(
        &mut self,
        actor: EntityId,
        user_id: UserId,
    ) -> Result<Vec<Event>, WorldError> {
        self.check_admin(actor, &user_id)?;
        Ok(vec![Event::new(Audience::Host, EventKind::UserUnbanned { user_id }), ack(actor)])
    }

    pub(super) fn set_role(
        &mut self,
        actor: EntityId,
        user_id: UserId,
        role: Role,
    ) -> Result<Vec<Event>, WorldError> {
        if self.check_admin(actor, &user_id)? && role < Role::Owner {
            return Err(invalid("you cannot demote yourself"));
        }
        let target = self.avatar_of_user(&user_id).map(|a| a.id);
        let mut events =
            vec![Event::new(Audience::Host, EventKind::UserRoleChanged { user_id, role })];
        if let Some(presence) = target.and_then(|id| self.avatars.get_mut(&id)) {
            presence.avatar.role = role;
            events
                .push(Event::new(Audience::All, EventKind::AvatarUpdated(presence.avatar.clone())));
        }
        events.push(ack(actor));
        Ok(events)
    }
}
