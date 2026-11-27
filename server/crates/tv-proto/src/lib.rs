//! The Tinyverse wire protocol.
//!
//! The schema lives in `proto/tinyverse/v1/` at the repository root and is the
//! single source of truth. This crate holds the Rust types generated from it
//! ([`v1`]), their proto3 JSON mapping, and helpers to turn messages into
//! WebSocket frames and back.
//!
//! One message travels per WebSocket frame. A **binary** frame holds the
//! protobuf encoding; a **text** frame holds the same message as proto3 JSON.
//! Unity clients use binary, agents normally use JSON.
//!
//! ```
//! use tv_proto::{decode, encode, v1, Encoding, Frame};
//!
//! let msg = v1::ClientMessage {
//!     seq: 1,
//!     body: Some(v1::client_message::Body::Ping(v1::Ping { client_time_ms: 42 })),
//! };
//! let frame = encode(&msg, Encoding::Json);
//! assert!(matches!(frame, Frame::Text(_)));
//! assert_eq!(decode::<v1::ClientMessage>(&frame).unwrap(), msg);
//! ```

use prost::Message;
use serde::{Serialize, de::DeserializeOwned};

/// Generated types for package `tinyverse.v1`.
#[allow(clippy::all, missing_docs)]
pub mod v1 {
    include!(concat!(env!("OUT_DIR"), "/tinyverse.v1.rs"));
    include!(concat!(env!("OUT_DIR"), "/tinyverse.v1.serde.rs"));
}

/// The protocol version this crate implements, sent in `Hello` and `Welcome`.
pub const PROTOCOL_VERSION: u32 = 1;

/// The TCP port a server listens on unless told otherwise.
pub const DEFAULT_PORT: u16 = 7878;

/// The HTTP path of the WebSocket endpoint.
pub const WS_PATH: &str = "/ws";

/// How a message is written into a WebSocket frame.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Encoding {
    /// Protobuf in a binary frame.
    Binary,
    /// proto3 JSON in a text frame.
    Json,
}

/// The payload of one WebSocket data frame.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Frame {
    /// A binary frame holding a protobuf-encoded message.
    Binary(Vec<u8>),
    /// A text frame holding a proto3 JSON message.
    Text(String),
}

impl Frame {
    /// The encoding this frame uses.
    pub fn encoding(&self) -> Encoding {
        match self {
            Frame::Binary(_) => Encoding::Binary,
            Frame::Text(_) => Encoding::Json,
        }
    }

    /// The size of the payload in bytes.
    pub fn len(&self) -> usize {
        match self {
            Frame::Binary(bytes) => bytes.len(),
            Frame::Text(text) => text.len(),
        }
    }

    /// Whether the payload is empty.
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

/// A frame that could not be turned into a message.
#[derive(Debug, thiserror::Error)]
pub enum DecodeError {
    /// The binary frame is not a valid protobuf encoding of the message.
    #[error("invalid protobuf: {0}")]
    Binary(#[from] prost::DecodeError),
    /// The text frame is not valid proto3 JSON for the message. The inner
    /// error names the offending field and position, so it is fit to show to
    /// whoever wrote the JSON.
    #[error("invalid JSON: {0}")]
    Json(#[from] serde_json::Error),
}

/// Writes a message into a frame using the given encoding.
pub fn encode<M>(message: &M, encoding: Encoding) -> Frame
where
    M: Message + Serialize,
{
    match encoding {
        Encoding::Binary => Frame::Binary(message.encode_to_vec()),
        Encoding::Json => Frame::Text(
            serde_json::to_string(message).expect("protocol messages always serialise to JSON"),
        ),
    }
}

/// Reads a message from a frame. The encoding is taken from the frame type.
pub fn decode<M>(frame: &Frame) -> Result<M, DecodeError>
where
    M: Message + Default + DeserializeOwned,
{
    match frame {
        Frame::Binary(bytes) => Ok(M::decode(bytes.as_slice())?),
        Frame::Text(text) => Ok(serde_json::from_str(text)?),
    }
}

#[cfg(test)]
mod tests {
    use super::v1::{client_message::Body as C, server_message::Body as S, *};
    use super::*;

    fn vec3(x: f32, y: f32, z: f32) -> Vec3 {
        Vec3 { x, y, z }
    }

    fn color() -> Color {
        Color { r: 0.25, g: 0.5, b: 0.75, a: 1.0 }
    }

    fn appearance() -> Appearance {
        Appearance {
            body_color: Some(color()),
            body_preset: "average".into(),
            skin_color: Some(color()),
            hair_color: Some(color()),
            hair_style: "short".into(),
            top_color: Some(color()),
            bottom_color: Some(color()),
        }
    }

    fn transform() -> AvatarTransform {
        AvatarTransform {
            position: Some(vec3(1.0, 0.0, -2.5)),
            yaw: 90.0,
            velocity: Some(vec3(0.0, 0.0, 4.0)),
            move_mode: MoveMode::Walk as i32,
            animation: "walk".into(),
        }
    }

    fn avatar() -> AvatarInfo {
        AvatarInfo {
            id: u64::MAX - 1,
            user_id: "6f1c2a52-6f0e-4c53-9f0b-0d7a7a1f2b11".into(),
            name: "Ada".into(),
            kind: ClientKind::Human as i32,
            role: Role::Builder as i32,
            appearance: Some(appearance()),
            transform: Some(transform()),
        }
    }

    fn spec() -> ObjectSpec {
        ObjectSpec {
            name: "door".into(),
            shape: Shape::Box as i32,
            position: Some(vec3(0.0, 1.0, 0.0)),
            rotation: Some(Quat { x: 0.0, y: 0.707, z: 0.0, w: 0.707 }),
            scale: Some(vec3(1.0, 2.0, 0.1)),
            color: Some(color()),
            material: Material::Glossy as i32,
            solid: true,
            locked: true,
        }
    }

    fn object() -> ObjectInfo {
        ObjectInfo { id: 7, owner_id: "owner".into(), spec: Some(spec()) }
    }

    fn chat_entry() -> ChatEntry {
        ChatEntry {
            sender_avatar_id: 3,
            sender_user_id: "u".into(),
            sender_name: "Ada".into(),
            sender_kind: ClientKind::Agent as i32,
            channel: ChatChannel::World as i32,
            text: "hello \"world\" ☃".into(),
            timestamp_ms: 1_795_000_000_123,
        }
    }

    /// One fully populated sample of every client message body.
    fn client_bodies() -> Vec<C> {
        vec![
            C::Hello(Hello {
                protocol_version: PROTOCOL_VERSION,
                kind: ClientKind::Agent as i32,
                name: "scout".into(),
                password: "pw".into(),
                token: "tok".into(),
                appearance: Some(appearance()),
                client_name: "tv-cli/0.1".into(),
            }),
            C::AvatarState(AvatarState { transform: Some(transform()) }),
            C::MoveTo(MoveTo { target: Some(vec3(5.0, 0.0, 5.0)), mode: MoveMode::Fly as i32 }),
            C::Stop(Stop {}),
            C::Chat(Chat { channel: ChatChannel::Local as i32, text: "hi".into() }),
            C::ChatHistoryRequest(ChatHistoryRequest { since_ms: 1_795_000_000_000, limit: 50 }),
            C::ObjectCreate(ObjectCreate { spec: Some(spec()) }),
            C::ObjectUpdate(ObjectUpdate {
                id: 7,
                name: Some(String::new()),
                shape: Some(Shape::Wedge as i32),
                position: Some(vec3(1.0, 2.0, 3.0)),
                rotation: None,
                scale: Some(vec3(1.0, 1.0, 1.0)),
                color: Some(color()),
                material: Some(Material::Glass as i32),
                solid: Some(false),
                locked: None,
            }),
            C::ObjectDelete(ObjectDelete { id: 7 }),
            C::Observe(Observe { radius: 30.0 }),
            C::SetAppearance(SetAppearance { appearance: Some(appearance()) }),
            C::Admin(Admin {
                action: Some(admin::Action::Kick(admin::Kick {
                    user_id: "u".into(),
                    reason: "bye".into(),
                })),
            }),
            C::Admin(Admin {
                action: Some(admin::Action::Ban(admin::Ban {
                    user_id: "u".into(),
                    reason: "spam".into(),
                })),
            }),
            C::Admin(Admin {
                action: Some(admin::Action::Unban(admin::Unban { user_id: "u".into() })),
            }),
            C::Admin(Admin {
                action: Some(admin::Action::SetRole(admin::SetRole {
                    user_id: "u".into(),
                    role: Role::Owner as i32,
                })),
            }),
            C::Ping(Ping { client_time_ms: 12345 }),
        ]
    }

    /// One fully populated sample of every server message body.
    fn server_bodies() -> Vec<S> {
        vec![
            S::Welcome(Welcome {
                protocol_version: PROTOCOL_VERSION,
                user_id: "u".into(),
                avatar_id: 3,
                role: Role::Builder as i32,
                session_token: "secret".into(),
                world: Some(WorldInfo {
                    name: "Home".into(),
                    description: "a small island".into(),
                    bounds_min: Some(vec3(-128.0, -16.0, -128.0)),
                    bounds_max: Some(vec3(128.0, 256.0, 128.0)),
                    spawn_point: Some(vec3(0.0, 0.0, 0.0)),
                    ground_size: 256.0,
                    sky: "day".into(),
                    time_of_day: 13.5,
                }),
                limits: Some(Limits {
                    max_message_bytes: 65536,
                    max_chat_chars: 500,
                    max_name_chars: 64,
                    max_objects: 10_000,
                    local_chat_radius: 20.0,
                    min_scale: 0.01,
                    max_scale: 64.0,
                    walk_speed: 4.0,
                    run_speed: 8.0,
                    fly_speed: 16.0,
                    max_observe_radius: 100.0,
                }),
                tick_rate_hz: 20,
                server_time_ms: 1_795_000_000_000,
                server_version: "0.1.0".into(),
                avatar: Some(avatar()),
            }),
            S::Snapshot(Snapshot { avatars: vec![avatar()], objects: vec![object()], last: true }),
            S::AvatarJoined(AvatarJoined { avatar: Some(avatar()) }),
            S::AvatarLeft(AvatarLeft { avatar_id: 3 }),
            S::AvatarStates(AvatarStates {
                server_time_ms: 1_795_000_000_050,
                states: vec![avatar_states::Entry { avatar_id: 3, transform: Some(transform()) }],
            }),
            S::AvatarCorrection(AvatarCorrection {
                transform: Some(transform()),
                reason: "too fast".into(),
            }),
            S::AvatarUpdated(AvatarUpdated { avatar: Some(avatar()) }),
            S::ChatMessage(ChatMessage { entry: Some(chat_entry()) }),
            S::ChatHistory(ChatHistory { entries: vec![chat_entry(), chat_entry()] }),
            S::ObjectCreated(ObjectCreated { object: Some(object()), by_avatar_id: 3 }),
            S::ObjectUpdated(ObjectUpdated { object: Some(object()), by_avatar_id: 3 }),
            S::ObjectDeleted(ObjectDeleted { id: 7, by_avatar_id: 3 }),
            S::Observation(Observation {
                self_: Some(avatar()),
                avatars: vec![observation::SeenAvatar {
                    avatar: Some(avatar()),
                    distance: 2.5,
                    bearing: -45.0,
                }],
                objects: vec![observation::SeenObject {
                    object: Some(object()),
                    distance: 4.0,
                    bearing: 170.0,
                }],
                summary: "Ada is 2.5 m ahead to the left.".into(),
                truncated: false,
                radius: 30.0,
            }),
            S::Error(Error { code: ErrorCode::PermissionDenied as i32, message: "locked".into() }),
            S::Kicked(Kicked { reason: "bye".into() }),
            S::Pong(Pong { client_time_ms: 12345, server_time_ms: 1_795_000_000_000 }),
            S::Ack(Ack {}),
        ]
    }

    #[test]
    fn every_client_message_round_trips_in_both_encodings() {
        // 13 message kinds; Admin appears once per action.
        let bodies = client_bodies();
        assert_eq!(bodies.len(), 16);
        for (i, body) in bodies.into_iter().enumerate() {
            let msg = ClientMessage { seq: i as u32 + 1, body: Some(body) };
            for encoding in [Encoding::Binary, Encoding::Json] {
                let frame = encode(&msg, encoding);
                assert_eq!(frame.encoding(), encoding);
                let back: ClientMessage = decode(&frame).unwrap_or_else(|e| {
                    panic!("{encoding:?} decode failed for {msg:?}: {e}\nframe: {frame:?}")
                });
                assert_eq!(back, msg, "{encoding:?} round trip changed the message");
            }
        }
    }

    #[test]
    fn every_server_message_round_trips_in_both_encodings() {
        let bodies = server_bodies();
        assert_eq!(bodies.len(), 17);
        for (i, body) in bodies.into_iter().enumerate() {
            let msg = ServerMessage { reply_to: i as u32, body: Some(body) };
            for encoding in [Encoding::Binary, Encoding::Json] {
                let frame = encode(&msg, encoding);
                let back: ServerMessage = decode(&frame).unwrap_or_else(|e| {
                    panic!("{encoding:?} decode failed for {msg:?}: {e}\nframe: {frame:?}")
                });
                assert_eq!(back, msg, "{encoding:?} round trip changed the message");
            }
        }
    }

    #[test]
    fn json_uses_proto3_names_and_writes_zero_fields() {
        let msg =
            ServerMessage { reply_to: 0, body: Some(S::AvatarLeft(AvatarLeft { avatar_id: 3 })) };
        let Frame::Text(json) = encode(&msg, Encoding::Json) else { panic!("expected text") };
        let value: serde_json::Value = serde_json::from_str(&json).unwrap();
        // camelCase field names, 64-bit integers as strings, zero fields present.
        assert_eq!(value["avatarLeft"]["avatarId"], "3");
        assert_eq!(value["replyTo"], 0);

        let msg = ClientMessage {
            seq: 2,
            body: Some(C::MoveTo(MoveTo { target: Some(vec3(0.0, 0.0, 1.0)), mode: 1 })),
        };
        let Frame::Text(json) = encode(&msg, Encoding::Json) else { panic!("expected text") };
        let value: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert_eq!(value["moveTo"]["target"]["x"], 0.0);
        assert_eq!(value["moveTo"]["mode"], "MOVE_MODE_WALK");
    }

    #[test]
    fn json_written_by_hand_is_accepted() {
        // What an agent author would plausibly type: snake_case or camelCase
        // names, enum names or numbers, 64-bit integers as numbers or strings,
        // and fields left out.
        let cases = [
            r#"{"seq":1,"hello":{"protocol_version":1,"kind":"CLIENT_KIND_AGENT","name":"scout","token":"abc"}}"#,
            r#"{"seq":1,"hello":{"protocolVersion":1,"kind":2,"name":"scout","token":"abc"}}"#,
        ];
        for json in cases {
            let msg: ClientMessage = decode(&Frame::Text(json.into())).unwrap();
            let Some(C::Hello(hello)) = msg.body else { panic!("expected hello") };
            assert_eq!(hello.protocol_version, 1);
            assert_eq!(hello.kind, ClientKind::Agent as i32);
            assert_eq!(hello.name, "scout");
            assert_eq!(hello.token, "abc");
            assert_eq!(hello.password, "");
        }

        for json in [r#"{"objectDelete":{"id":7}}"#, r#"{"object_delete":{"id":"7"}}"#] {
            let msg: ClientMessage = decode(&Frame::Text(json.into())).unwrap();
            assert_eq!(msg.seq, 0);
            assert_eq!(msg.body, Some(C::ObjectDelete(ObjectDelete { id: 7 })));
        }
    }

    #[test]
    fn absent_optional_fields_stay_absent() {
        let json = r#"{"seq":4,"objectUpdate":{"id":7,"solid":false}}"#;
        let msg: ClientMessage = decode(&Frame::Text(json.into())).unwrap();
        let Some(C::ObjectUpdate(update)) = msg.body else { panic!("expected update") };
        assert_eq!(update.solid, Some(false));
        assert_eq!(update.locked, None);
        assert_eq!(update.name, None);
        assert_eq!(update.position, None);
    }

    #[test]
    fn malformed_frames_are_errors_not_panics() {
        let garbage = Frame::Binary(vec![0xff, 0xff, 0xff, 0xff]);
        assert!(matches!(decode::<ClientMessage>(&garbage), Err(DecodeError::Binary(_))));

        for json in ["", "{", r#"{"seq":"x"}"#, r#"{"nope":{}}"#, r#"{"chat":{"channel":"LOUD"}}"#]
        {
            let err = decode::<ClientMessage>(&Frame::Text(json.into()));
            assert!(matches!(err, Err(DecodeError::Json(_))), "{json:?} gave {err:?}");
        }
    }

    #[test]
    fn empty_binary_frame_is_an_empty_message() {
        // An empty protobuf payload is valid and means "no body"; callers must
        // treat a missing body as a bad request.
        let msg: ClientMessage = decode(&Frame::Binary(Vec::new())).unwrap();
        assert_eq!(msg, ClientMessage::default());
    }
}
