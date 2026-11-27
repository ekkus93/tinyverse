//! Join passwords and secret files.
//!
//! Login tokens are generated, hashed and checked by `tv-store`; this module
//! only deals with the world's join password (an Argon2 PHC string kept in
//! `world.toml`) and with writing secrets to disk.

use std::io::Write;
use std::path::Path;

use argon2::Argon2;
use argon2::password_hash::phc::PasswordHash;
use argon2::password_hash::{PasswordHasher, PasswordVerifier};

/// Hashes a password into an Argon2id PHC string with a random salt.
pub fn hash_password(password: &str) -> anyhow::Result<String> {
    let hash: PasswordHash = Argon2::default()
        .hash_password(password.as_bytes())
        .map_err(|e| anyhow::anyhow!("cannot hash password: {e}"))?;
    Ok(hash.to_string())
}

/// Whether `password` matches the PHC string. A malformed hash matches nothing.
///
/// This is deliberately slow (tens of milliseconds); call it from a blocking
/// thread.
pub fn verify_password(password: &str, phc: &str) -> bool {
    match PasswordHash::new(phc) {
        Ok(hash) => Argon2::default().verify_password(password.as_bytes(), &hash).is_ok(),
        Err(_) => false,
    }
}

/// Checks that `phc` is a PHC string of an Argon2 hash.
pub fn validate_phc(phc: &str) -> Result<(), String> {
    let hash = PasswordHash::new(phc).map_err(|e| e.to_string())?;
    let alg = hash.algorithm.as_str();
    if alg.starts_with("argon2") { Ok(()) } else { Err(format!("algorithm is {alg}, not argon2")) }
}

/// Creates `path` with mode 0600 and writes `contents`. Fails if it exists.
pub fn write_secret_file(path: &Path, contents: &str) -> std::io::Result<()> {
    let mut options = std::fs::OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut file = options.open(path)?;
    file.write_all(contents.as_bytes())?;
    file.sync_all()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn password_round_trip() {
        let hash = hash_password("correct horse").unwrap();
        assert!(hash.starts_with("$argon2id$"));
        validate_phc(&hash).unwrap();
        assert!(verify_password("correct horse", &hash));
        assert!(!verify_password("wrong", &hash));
        assert!(!verify_password("", &hash));
        assert!(!verify_password("correct horse", "not a hash"));
    }

    #[test]
    fn non_argon2_hashes_are_refused() {
        assert!(validate_phc("plain").is_err());
        assert!(validate_phc("$pbkdf2-sha256$i=1000$c2FsdA$aGFzaA").is_err());
    }

    #[test]
    fn secret_file_is_private_and_not_overwritten() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("owner.token");
        write_secret_file(&path, "abc\n").unwrap();
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "abc\n");
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let mode = std::fs::metadata(&path).unwrap().permissions().mode();
            assert_eq!(mode & 0o777, 0o600);
        }
        assert!(write_secret_file(&path, "x").is_err());
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "abc\n");
    }
}
