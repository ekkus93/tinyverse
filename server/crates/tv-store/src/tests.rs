use std::path::{Path, PathBuf};
use std::time::Instant;

use tv_world::{Color, Material, ObjectSpec, Quat, Shape, Vec3};

use super::*;

const SHAPES: [Shape; 6] =
    [Shape::Box, Shape::Sphere, Shape::Cylinder, Shape::Cone, Shape::Wedge, Shape::Plane];
const MATERIALS: [Material; 5] =
    [Material::Matte, Material::Glossy, Material::Metal, Material::Glass, Material::Emissive];

fn temp_db() -> (tempfile::TempDir, PathBuf) {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("world.db");
    (dir, path)
}

fn object(id: EntityId) -> Object {
    Object {
        id,
        owner: UserId::from("owner-1"),
        spec: ObjectSpec {
            name: format!("object {id}"),
            position: Vec3::new(id as f32, 0.5, -(id as f32)),
            ..ObjectSpec::default()
        },
    }
}

/// One object per shape/material pair, with awkward values throughout.
fn awkward_objects() -> Vec<Object> {
    let mut objects = Vec::new();
    let mut id = 1;
    for (si, &shape) in SHAPES.iter().enumerate() {
        for (mi, &material) in MATERIALS.iter().enumerate() {
            let k = (si * MATERIALS.len() + mi) as f32;
            objects.push(Object {
                id,
                owner: UserId::from(format!("user-{}", si % 3).as_str()),
                spec: ObjectSpec {
                    name: match mi {
                        0 => String::new(),
                        1 => "Ünïcødé ⛩ 漢字 🏠".to_owned(),
                        2 => "quote ' \" ; DROP TABLE objects; --".to_owned(),
                        3 => "line\nbreak\ttab\0nul".to_owned(),
                        _ => "x".repeat(64),
                    },
                    shape,
                    position: Vec3::new(-127.999_99 + k, -15.5, 1.0e-7 * k),
                    rotation: Quat::from_euler(glam::EulerRot::YXZ, 0.3 * k, -1.1, 2.7),
                    scale: Vec3::new(0.01, 64.0, 0.123_456_79),
                    color: Color::new(0.0, 1.0, 0.333_333_34, 0.5),
                    material,
                    solid: si % 2 == 0,
                    locked: mi % 2 == 1,
                },
            });
            id += 1;
        }
    }
    // Ids need not be dense.
    objects.push(Object { id: 1_000_000, ..object(1_000_000) });
    objects
}

#[test]
fn fresh_database_is_empty() {
    let store = Store::open_in_memory().unwrap();
    let world = store.load_world().unwrap();
    assert!(world.objects.is_empty());
    assert_eq!(world.next_entity_id, 1);
    assert!(store.users().unwrap().is_empty());

    let (_dir, path) = temp_db();
    let store = Store::open(&path).unwrap();
    assert_eq!(store.load_world().unwrap(), LoadedWorld { objects: vec![], next_entity_id: 1 });
}

#[test]
fn objects_round_trip_through_disk() {
    let (_dir, path) = temp_db();
    let objects = awkward_objects();
    {
        let mut store = Store::open(&path).unwrap();
        store.save_objects(&objects, &[], 1_000_001).unwrap();
    }
    let store = Store::open(&path).unwrap();
    let world = store.load_world().unwrap();
    // Exact equality: f32 survives a round trip through SQLite REAL (f64).
    assert_eq!(world.objects, objects);
    assert_eq!(world.next_entity_id, 1_000_001);
}

#[test]
fn objects_are_loaded_in_id_order() {
    let mut store = Store::open_in_memory().unwrap();
    let objects: Vec<_> = [5, 3, 9, 1].into_iter().map(object).collect();
    store.save_objects(&objects, &[], 10).unwrap();
    let ids: Vec<_> = store.load_world().unwrap().objects.iter().map(|o| o.id).collect();
    assert_eq!(ids, [1, 3, 5, 9]);
}

#[test]
fn upsert_replaces_and_delete_removes() {
    let mut store = Store::open_in_memory().unwrap();
    store.save_objects(&[object(1), object(2), object(3)], &[], 4).unwrap();

    let mut changed = object(2);
    changed.owner = UserId::from("someone-else");
    changed.spec = ObjectSpec {
        name: "changed".to_owned(),
        shape: Shape::Cone,
        position: Vec3::new(-1.0, -2.0, -3.0),
        rotation: Quat::from_rotation_z(1.0),
        scale: Vec3::splat(3.0),
        color: Color::new(0.1, 0.2, 0.3, 0.4),
        material: Material::Glass,
        solid: false,
        locked: true,
    };
    // Unknown ids (including one SQLite cannot even hold) are ignored.
    store.save_objects(&[changed.clone()], &[3, 77, u64::MAX], 4).unwrap();

    let world = store.load_world().unwrap();
    assert_eq!(world.objects, vec![object(1), changed]);
    assert_eq!(world.next_entity_id, 4);
}

#[test]
fn delete_wins_over_upsert_in_one_batch() {
    let mut store = Store::open_in_memory().unwrap();
    store.save_objects(&[object(1)], &[1], 2).unwrap();
    assert!(store.load_world().unwrap().objects.is_empty());
}

#[test]
fn counter_never_decreases() {
    let mut store = Store::open_in_memory().unwrap();
    store.save_objects(&[], &[], 50).unwrap();
    assert_eq!(store.load_world().unwrap().next_entity_id, 50);
    store.save_objects(&[], &[], 10).unwrap();
    assert_eq!(store.load_world().unwrap().next_entity_id, 50);
    store.save_objects(&[], &[], 51).unwrap();
    assert_eq!(store.load_world().unwrap().next_entity_id, 51);
}

#[test]
fn counter_is_repaired_when_too_low() {
    let mut store = Store::open_in_memory().unwrap();
    store.save_objects(&[object(1), object(40), object(7)], &[], 3).unwrap();
    assert_eq!(store.load_world().unwrap().next_entity_id, 41);
    // Equal to an existing id is too low as well.
    store.save_objects(&[], &[], 40).unwrap();
    assert_eq!(store.load_world().unwrap().next_entity_id, 41);
    // A counter of 0 on an empty world still yields 1.
    let mut store = Store::open_in_memory().unwrap();
    store.save_objects(&[], &[], 0).unwrap();
    assert_eq!(store.load_world().unwrap().next_entity_id, 1);
}

#[test]
fn failed_batch_changes_nothing() {
    let mut store = Store::open_in_memory().unwrap();
    store.save_objects(&[object(1), object(2)], &[], 3).unwrap();
    let before = store.load_world().unwrap();

    // Fails at the second upsert: id 0 violates the CHECK constraint.
    let mut edited = object(1);
    edited.spec.name = "edited".to_owned();
    let err = store.save_objects(&[edited.clone(), object(0)], &[2], 100).unwrap_err();
    assert!(matches!(err, StoreError::Sqlite(_)), "{err:?}");
    assert_eq!(store.load_world().unwrap(), before);

    // Fails at a NaN coordinate, which SQLite would store as NULL.
    let mut nan = object(5);
    nan.spec.position.y = f32::NAN;
    store.save_objects(&[edited.clone(), nan], &[2], 100).unwrap_err();
    assert_eq!(store.load_world().unwrap(), before);

    // Fails in the delete phase, after all upserts went through.
    store
        .conn
        .execute_batch(
            "CREATE TEMP TRIGGER refuse_delete BEFORE DELETE ON objects WHEN old.id = 2 \
             BEGIN SELECT RAISE(ABORT, 'refused'); END;",
        )
        .unwrap();
    store.save_objects(&[edited.clone(), object(9)], &[1, 2], 100).unwrap_err();
    assert_eq!(store.load_world().unwrap(), before);

    // The store is still usable afterwards.
    store.conn.execute_batch("DROP TRIGGER refuse_delete").unwrap();
    store.save_objects(&[edited.clone()], &[2], 100).unwrap();
    let after = store.load_world().unwrap();
    assert_eq!(after.objects, vec![edited]);
    assert_eq!(after.next_entity_id, 100);
}

#[test]
fn committed_batches_survive_a_crash() {
    let (_dir, path) = temp_db();
    let mut store = Store::open(&path).unwrap();
    store.save_objects(&[object(1), object(2)], &[], 3).unwrap();
    let mut moved = object(1);
    moved.spec.position = Vec3::new(9.0, 9.0, 9.0);
    store.save_objects(&[moved.clone(), object(3)], &[2], 4).unwrap();
    // Batch C is built but never handed to save_objects.
    let _never_saved = [object(4), object(5)];
    // No close, no checkpoint, no destructor: the connection is leaked as a
    // killed process would leave it, with the data only in the WAL.
    assert!(path.with_extension("db-wal").exists());
    std::mem::forget(store);

    let store = Store::open(&path).unwrap();
    let world = store.load_world().unwrap();
    assert_eq!(world.objects, vec![moved.clone(), object(3)]);
    assert_eq!(world.next_entity_id, 4);
    drop(store);

    // And after a normal drop, too.
    let store = Store::open(&path).unwrap();
    assert_eq!(store.load_world().unwrap().objects, vec![moved, object(3)]);
}

#[test]
fn two_handles_on_one_file() {
    let (_dir, path) = temp_db();
    let mut server = Store::open(&path).unwrap();
    let mut cli = Store::open(&path).unwrap();

    server.save_objects(&[object(1)], &[], 2).unwrap();
    let (user, token) = cli.create_user("scout", ClientKind::Agent, Role::Builder, 10).unwrap();

    assert_eq!(cli.load_world().unwrap().objects, vec![object(1)]);
    assert_eq!(server.user_by_token(&token).unwrap(), Some(user.clone()));

    assert!(cli.ban(&user.id, "spam").unwrap());
    assert_eq!(server.user(&user.id).unwrap().unwrap().banned.as_deref(), Some("spam"));
    cli.save_objects(&[object(2)], &[1], 3).unwrap();
    server.save_objects(&[object(3)], &[], 4).unwrap();
    assert_eq!(cli.load_world().unwrap().objects, vec![object(2), object(3)]);
}

#[test]
fn wal_and_pragmas_are_set() {
    let (_dir, path) = temp_db();
    let store = Store::open(&path).unwrap();
    let pragma = |name: &str| -> String {
        store
            .conn
            .pragma_query_value(None, name, |r| {
                r.get::<_, rusqlite::types::Value>(0).map(|v| format!("{v:?}"))
            })
            .unwrap()
    };
    assert!(pragma("journal_mode").contains("wal"));
    assert_eq!(pragma("synchronous"), "Integer(1)"); // NORMAL
    assert_eq!(pragma("foreign_keys"), "Integer(1)");
    assert_eq!(pragma("busy_timeout"), "Integer(5000)");
    assert_eq!(pragma("user_version"), format!("Integer({})", schema::MIGRATIONS.len()));
}

// ---- users ----

#[test]
fn create_user_and_log_in() {
    let mut store = Store::open_in_memory().unwrap();
    let (user, token) = store.create_user("Ada", ClientKind::Human, Role::Builder, 1_000).unwrap();
    assert_eq!(token.len(), 64);
    assert!(token.bytes().all(|b| b.is_ascii_hexdigit()));
    assert!(uuid::Uuid::parse_str(user.id.as_str()).is_ok());
    assert_eq!(
        uuid::Uuid::parse_str(user.id.as_str()).unwrap().get_version(),
        Some(uuid::Version::Random)
    );
    assert_eq!(
        user,
        UserRecord {
            id: user.id.clone(),
            name: "Ada".to_owned(),
            kind: ClientKind::Human,
            role: Role::Builder,
            created_ms: 1_000,
            last_seen_ms: 1_000,
            banned: None,
            has_token: true,
        }
    );
    assert_eq!(store.user_by_token(&token).unwrap(), Some(user.clone()));
    assert_eq!(store.user(&user.id).unwrap(), Some(user.clone()));

    let mut wrong = token.clone();
    wrong.replace_range(0..1, if wrong.starts_with('0') { "1" } else { "0" });
    assert_eq!(store.user_by_token(&wrong).unwrap(), None);
    assert_eq!(store.user_by_token("").unwrap(), None);
    assert_eq!(store.user_by_token(&token.to_uppercase()).unwrap(), None);

    let (other, other_token) =
        store.create_user("Ada", ClientKind::Human, Role::Builder, 1_000).unwrap();
    assert_ne!(other.id, user.id);
    assert_ne!(other_token, token);
}

#[test]
fn clear_text_token_is_never_written() {
    let (dir, path) = temp_db();
    let mut tokens = Vec::new();
    {
        let mut store = Store::open(&path).unwrap();
        let (user, token) = store.create_user("Ada", ClientKind::Human, Role::Owner, 1).unwrap();
        tokens.push(token);
        tokens.push(store.reissue_token(&user.id).unwrap().unwrap());
        let (_, token) = store.create_user("Bot", ClientKind::Agent, Role::Visitor, 2).unwrap();
        tokens.push(token);
        // Check the WAL before the connection checkpoints it away on close.
        assert_no_token_in(dir.path(), &tokens);
    }
    assert_no_token_in(dir.path(), &tokens);
}

fn assert_no_token_in(dir: &Path, tokens: &[String]) {
    let contains =
        |haystack: &[u8], needle: &[u8]| haystack.windows(needle.len()).any(|w| w == needle);
    let files: Vec<Vec<u8>> = std::fs::read_dir(dir)
        .unwrap()
        .map(|entry| std::fs::read(entry.unwrap().path()).unwrap())
        .collect();
    for token in tokens {
        let raw = hex::decode(token).unwrap();
        let upper = token.to_uppercase();
        for needle in [token.as_bytes(), upper.as_bytes(), &raw] {
            assert!(!files.iter().any(|f| contains(f, needle)), "token leaked");
        }
    }
    // Positive control: the search does find what is stored, the digest of
    // the current (last issued) token of the last user.
    let digest = token_digest(tokens.last().unwrap());
    assert!(files.iter().any(|f| contains(f, digest.as_bytes())));
}

#[test]
fn revoke_and_reissue() {
    let mut store = Store::open_in_memory().unwrap();
    let (user, token) = store.create_user("Ada", ClientKind::Human, Role::Builder, 1).unwrap();

    let new_token = store.reissue_token(&user.id).unwrap().unwrap();
    assert_ne!(new_token, token);
    assert_eq!(store.user_by_token(&token).unwrap(), None);
    assert_eq!(store.user_by_token(&new_token).unwrap().unwrap().id, user.id);

    assert!(store.revoke_token(&user.id).unwrap());
    assert!(!store.revoke_token(&user.id).unwrap(), "already revoked");
    assert_eq!(store.user_by_token(&new_token).unwrap(), None);
    let revoked = store.user(&user.id).unwrap().unwrap();
    assert!(!revoked.has_token);
    assert_eq!(revoked.name, "Ada");

    // Reissuing after a revoke gives the user a working token again.
    let again = store.reissue_token(&user.id).unwrap().unwrap();
    assert!(store.user(&user.id).unwrap().unwrap().has_token);
    assert_eq!(store.user_by_token(&again).unwrap().unwrap().id, user.id);
}

#[test]
fn role_ban_and_touch() {
    let mut store = Store::open_in_memory().unwrap();
    let (user, token) = store.create_user("Ada", ClientKind::Human, Role::Visitor, 1).unwrap();

    assert!(store.set_role(&user.id, Role::Owner).unwrap());
    assert_eq!(store.user(&user.id).unwrap().unwrap().role, Role::Owner);

    assert!(!store.unban(&user.id).unwrap(), "not banned yet");
    assert!(store.ban(&user.id, "griefing the spawn").unwrap());
    let banned = store.user_by_token(&token).unwrap().unwrap();
    assert_eq!(banned.banned.as_deref(), Some("griefing the spawn"));
    assert!(store.ban(&user.id, "").unwrap(), "re-ban replaces the reason");
    assert_eq!(store.user(&user.id).unwrap().unwrap().banned.as_deref(), Some(""));
    assert!(store.unban(&user.id).unwrap());
    assert!(!store.unban(&user.id).unwrap());
    assert_eq!(store.user(&user.id).unwrap().unwrap().banned, None);

    assert!(store.touch_user(&user.id, None, 50).unwrap());
    let touched = store.user(&user.id).unwrap().unwrap();
    assert_eq!((touched.name.as_str(), touched.last_seen_ms), ("Ada", 50));
    assert_eq!(touched.created_ms, 1);
    assert!(store.touch_user(&user.id, Some("Ada L. ✨"), 60).unwrap());
    let touched = store.user(&user.id).unwrap().unwrap();
    assert_eq!((touched.name.as_str(), touched.last_seen_ms), ("Ada L. ✨", 60));
}

#[test]
fn unknown_users() {
    let mut store = Store::open_in_memory().unwrap();
    store.create_user("Ada", ClientKind::Human, Role::Builder, 1).unwrap();
    let nobody = UserId::from("00000000-0000-4000-8000-000000000000");
    assert_eq!(store.user(&nobody).unwrap(), None);
    assert!(!store.touch_user(&nobody, Some("x"), 5).unwrap());
    assert!(!store.set_role(&nobody, Role::Owner).unwrap());
    assert!(!store.ban(&nobody, "x").unwrap());
    assert!(!store.unban(&nobody).unwrap());
    assert!(!store.revoke_token(&nobody).unwrap());
    assert_eq!(store.reissue_token(&nobody).unwrap(), None);
    assert_eq!(store.users().unwrap().len(), 1);
}

#[test]
fn users_are_listed_oldest_first() {
    let (_dir, path) = temp_db();
    let mut store = Store::open(&path).unwrap();
    let (c, _) = store.create_user("c", ClientKind::Human, Role::Builder, 300).unwrap();
    let (a, _) = store.create_user("a", ClientKind::Agent, Role::Visitor, 100).unwrap();
    // Same timestamp: creation order breaks the tie.
    let (b1, _) = store.create_user("b1", ClientKind::Human, Role::Owner, 200).unwrap();
    let (b2, _) = store.create_user("b2", ClientKind::Human, Role::Owner, 200).unwrap();
    // A later visit does not change the order.
    store.touch_user(&a.id, None, 1_000).unwrap();
    drop(store);

    let store = Store::open(&path).unwrap();
    let names: Vec<_> = store.users().unwrap().into_iter().map(|u| u.name).collect();
    assert_eq!(names, ["a", "b1", "b2", "c"]);
    assert_eq!(store.user(&c.id).unwrap().unwrap().kind, ClientKind::Human);
    assert_eq!(store.user(&a.id).unwrap().unwrap().kind, ClientKind::Agent);
    let _ = (b1, b2);
}

// ---- schema and corruption ----

/// The version 1 schema exactly as released. Never edit: it stands for
/// files written by version 1.
const RELEASED_V1: &str = "
CREATE TABLE meta (key TEXT PRIMARY KEY NOT NULL, value INTEGER NOT NULL) STRICT;
CREATE TABLE objects (
    id INTEGER PRIMARY KEY NOT NULL CHECK (id > 0), owner TEXT NOT NULL,
    name TEXT NOT NULL, shape TEXT NOT NULL,
    pos_x REAL NOT NULL, pos_y REAL NOT NULL, pos_z REAL NOT NULL,
    rot_x REAL NOT NULL, rot_y REAL NOT NULL, rot_z REAL NOT NULL, rot_w REAL NOT NULL,
    scale_x REAL NOT NULL, scale_y REAL NOT NULL, scale_z REAL NOT NULL,
    color_r REAL NOT NULL, color_g REAL NOT NULL, color_b REAL NOT NULL,
    color_a REAL NOT NULL, material TEXT NOT NULL,
    solid INTEGER NOT NULL CHECK (solid IN (0, 1)),
    locked INTEGER NOT NULL CHECK (locked IN (0, 1))
) STRICT;
CREATE INDEX objects_by_owner ON objects (owner);
CREATE TABLE users (
    id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL,
    role TEXT NOT NULL, created_ms INTEGER NOT NULL, last_seen_ms INTEGER NOT NULL,
    banned TEXT, token_sha256 TEXT UNIQUE
) STRICT;
";

fn raw(path: &Path) -> rusqlite::Connection {
    rusqlite::Connection::open(path).unwrap()
}

fn user_version(path: &Path) -> i64 {
    raw(path).pragma_query_value(None, "user_version", |r| r.get(0)).unwrap()
}

#[test]
fn version_0_file_is_upgraded() {
    let (_dir, path) = temp_db();
    // An existing, empty SQLite file at version 0.
    raw(&path).execute_batch("VACUUM").unwrap();
    assert!(path.exists());
    assert_eq!(user_version(&path), 0);

    let mut store = Store::open(&path).unwrap();
    store.save_objects(&[object(1)], &[], 2).unwrap();
    drop(store);
    assert_eq!(user_version(&path), schema::MIGRATIONS.len() as i64);
}

#[test]
fn version_1_file_written_by_hand_opens() {
    let (_dir, path) = temp_db();
    let digest = token_digest("hand-made-token");
    {
        let conn = raw(&path);
        conn.execute_batch(RELEASED_V1).unwrap();
        conn.execute_batch(&format!(
            "INSERT INTO meta VALUES ('next_entity_id', 8);
             INSERT INTO objects VALUES (7, 'u-1', 'hut', 'wedge', 1.5, 0.0, -2.0,
                 0.0, 0.0, 0.0, 1.0, 2.0, 1.0, 3.0, 0.5, 0.25, 0.125, 1.0, 'metal', 1, 0);
             INSERT INTO users VALUES ('u-1', 'Ada', 'agent', 'owner', 5, 6, 'rude', '{digest}');
             PRAGMA user_version = 1;"
        ))
        .unwrap();
    }

    let store = Store::open(&path).unwrap();
    let world = store.load_world().unwrap();
    assert_eq!(world.next_entity_id, 8);
    assert_eq!(
        world.objects,
        vec![Object {
            id: 7,
            owner: UserId::from("u-1"),
            spec: ObjectSpec {
                name: "hut".to_owned(),
                shape: Shape::Wedge,
                position: Vec3::new(1.5, 0.0, -2.0),
                rotation: Quat::IDENTITY,
                scale: Vec3::new(2.0, 1.0, 3.0),
                color: Color::new(0.5, 0.25, 0.125, 1.0),
                material: Material::Metal,
                solid: true,
                locked: false,
            },
        }]
    );
    assert_eq!(
        store.user_by_token("hand-made-token").unwrap(),
        Some(UserRecord {
            id: UserId::from("u-1"),
            name: "Ada".to_owned(),
            kind: ClientKind::Agent,
            role: Role::Owner,
            created_ms: 5,
            last_seen_ms: 6,
            banned: Some("rude".to_owned()),
            has_token: true,
        })
    );
}

#[test]
fn migrations_run_in_order_and_only_once() {
    let (_dir, path) = temp_db();
    raw(&path).execute_batch(RELEASED_V1).unwrap();
    raw(&path).pragma_update(None, "user_version", 1).unwrap();
    raw(&path).execute("INSERT INTO meta VALUES ('next_entity_id', 3)", []).unwrap();

    let v2 = "CREATE TABLE notes (text TEXT NOT NULL) STRICT; \
              INSERT INTO notes VALUES ('from v2');";
    let v3 = "INSERT INTO notes SELECT text || ' then v3' FROM notes;";
    let mut conn = raw(&path);
    schema::migrate(&mut conn, &[RELEASED_V1, v2, v3]).unwrap();
    // Running again changes nothing: every migration ran exactly once.
    schema::migrate(&mut conn, &[RELEASED_V1, v2, v3]).unwrap();
    let notes: Vec<String> = conn
        .prepare("SELECT text FROM notes ORDER BY rowid")
        .unwrap()
        .query_map([], |r| r.get(0))
        .unwrap()
        .collect::<Result<_, _>>()
        .unwrap();
    assert_eq!(notes, ["from v2", "from v2 then v3"]);
    drop(conn);
    assert_eq!(user_version(&path), 3);
    // The v1 data is untouched.
    let value: i64 = raw(&path).query_row("SELECT value FROM meta", [], |r| r.get(0)).unwrap();
    assert_eq!(value, 3);
}

#[test]
fn failed_migration_leaves_old_version() {
    let (_dir, path) = temp_db();
    let mut conn = raw(&path);
    let broken = "CREATE TABLE half (x INTEGER); THIS IS NOT SQL;";
    assert!(schema::migrate(&mut conn, &[RELEASED_V1, broken]).is_err());
    assert_eq!(user_version(&path), 0);
    let tables: i64 =
        conn.query_row("SELECT count(*) FROM sqlite_master", [], |r| r.get(0)).unwrap();
    assert_eq!(tables, 0, "the whole upgrade was rolled back");
}

#[test]
fn future_version_is_corrupt() {
    let (_dir, path) = temp_db();
    raw(&path).pragma_update(None, "user_version", schema::MIGRATIONS.len() as i64 + 1).unwrap();
    assert!(matches!(Store::open(&path), Err(StoreError::Corrupt(_))));

    raw(&path).pragma_update(None, "user_version", -1).unwrap();
    assert!(matches!(Store::open(&path), Err(StoreError::Corrupt(_))));
}

#[test]
fn not_a_database_is_corrupt() {
    let (_dir, path) = temp_db();
    std::fs::write(&path, vec![b'x'; 4096]).unwrap();
    match Store::open(&path) {
        Err(StoreError::Corrupt(_)) => {}
        Err(e) => panic!("expected Corrupt, got {e:?}"),
        Ok(_) => panic!("expected Corrupt, got a store"),
    }
}

#[test]
fn undecodable_rows_are_corrupt() {
    let mut store = Store::open_in_memory().unwrap();
    store.save_objects(&[object(1), object(42)], &[], 43).unwrap();

    store.conn.execute("UPDATE objects SET shape = 'blob' WHERE id = 42", []).unwrap();
    match store.load_world() {
        Err(StoreError::Corrupt(msg)) => {
            assert!(msg.contains("42") && msg.contains("blob"), "{msg}")
        }
        other => panic!("expected Corrupt, got {other:?}"),
    }

    store
        .conn
        .execute("UPDATE objects SET shape = 'box', material = 'Matte' WHERE id = 42", [])
        .unwrap();
    match store.load_world() {
        Err(StoreError::Corrupt(msg)) => assert!(msg.contains("object 42"), "{msg}"),
        other => panic!("expected Corrupt, got {other:?}"),
    }

    store.conn.execute("UPDATE objects SET material = 'matte' WHERE id = 42", []).unwrap();
    store.conn.execute("UPDATE meta SET value = -5", []).unwrap();
    assert!(matches!(store.load_world(), Err(StoreError::Corrupt(_))));

    let (user, _) = store.create_user("Ada", ClientKind::Human, Role::Builder, 1).unwrap();
    store.conn.execute("UPDATE users SET role = 'admin'", []).unwrap();
    match store.user(&user.id) {
        Err(StoreError::Corrupt(msg)) => assert!(msg.contains(user.id.as_str()), "{msg}"),
        other => panic!("expected Corrupt, got {other:?}"),
    }
    assert!(matches!(store.users(), Err(StoreError::Corrupt(_))));
}

#[test]
fn enums_are_stored_as_names() {
    let mut store = Store::open_in_memory().unwrap();
    store.save_objects(&awkward_objects(), &[], 1).unwrap();
    store.create_user("a", ClientKind::Agent, Role::Visitor, 1).unwrap();
    let names = |sql: &str| -> Vec<String> {
        store
            .conn
            .prepare(sql)
            .unwrap()
            .query_map([], |r| r.get(0))
            .unwrap()
            .collect::<Result<_, _>>()
            .unwrap()
    };
    assert_eq!(
        names("SELECT DISTINCT shape FROM objects ORDER BY shape"),
        ["box", "cone", "cylinder", "plane", "sphere", "wedge"]
    );
    assert_eq!(
        names("SELECT DISTINCT material FROM objects ORDER BY material"),
        ["emissive", "glass", "glossy", "matte", "metal"]
    );
    assert_eq!(names("SELECT kind || ' ' || role FROM users"), ["agent visitor"]);
}

#[test]
fn ten_thousand_objects_save_and_load_quickly() {
    let (_dir, path) = temp_db();
    let mut store = Store::open(&path).unwrap();
    let objects: Vec<_> = (1..=10_000).map(object).collect();

    let start = Instant::now();
    store.save_objects(&objects, &[], 10_001).unwrap();
    let saved = start.elapsed();
    // Again, now every row is an update.
    let start = Instant::now();
    store.save_objects(&objects, &[], 10_001).unwrap();
    let resaved = start.elapsed();
    let start = Instant::now();
    let world = store.load_world().unwrap();
    let loaded = start.elapsed();

    assert_eq!(world.objects.len(), 10_000);
    assert_eq!(world.objects, objects);
    eprintln!("10,000 objects: insert {saved:?}, update {resaved:?}, load {loaded:?}");
    // Generous bounds: an unoptimised debug build on a slow CI machine.
    for elapsed in [saved, resaved, loaded] {
        assert!(elapsed.as_secs_f64() < 5.0, "{elapsed:?}");
    }
}
