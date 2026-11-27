//! Why the world refused a command.

/// A refused command. Each variant maps to one `ErrorCode` of the protocol.
/// The string, where present, is a human-readable explanation that is safe to
/// show to the user who sent the command.
#[derive(Clone, Debug, PartialEq, thiserror::Error)]
pub enum WorldError {
    /// The actor is not an avatar in this world.
    #[error("not in the world")]
    NotJoined,
    /// The world already holds [`crate::Limits::max_users`] avatars.
    #[error("the world is full")]
    WorldFull,
    /// The actor's role does not allow this, or the object is locked.
    #[error("permission denied: {0}")]
    PermissionDenied(String),
    /// No such object, avatar or user.
    #[error("not found: {0}")]
    NotFound(String),
    /// A value is missing, malformed or out of range.
    #[error("invalid argument: {0}")]
    InvalidArgument(String),
    /// A position lies outside the world bounds.
    #[error("out of bounds: {0}")]
    OutOfBounds(String),
    /// A world limit was reached.
    #[error("limit exceeded: {0}")]
    LimitExceeded(String),
}
