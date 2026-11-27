//! Generates the Rust protobuf types and their proto3 JSON mapping from the
//! schema in `proto/` at the repository root. Requires `protoc` on the PATH.

use std::{env, error::Error, fs, path::PathBuf};

const PROTOS: [&str; 3] =
    ["tinyverse/v1/common.proto", "tinyverse/v1/client.proto", "tinyverse/v1/server.proto"];

fn main() -> Result<(), Box<dyn Error>> {
    let root = PathBuf::from(env::var("CARGO_MANIFEST_DIR")?).join("../../../proto");
    let files: Vec<PathBuf> = PROTOS.iter().map(|p| root.join(p)).collect();
    for file in &files {
        println!("cargo:rerun-if-changed={}", file.display());
    }

    let descriptors = PathBuf::from(env::var("OUT_DIR")?).join("tinyverse_descriptors.bin");
    prost_build::Config::new()
        .file_descriptor_set_path(&descriptors)
        .compile_protos(&files, &[&root])?;

    // Always write every field, including zeros, so that an agent reading the
    // JSON sees `"x": 0` rather than a missing key.
    pbjson_build::Builder::new()
        .register_descriptors(&fs::read(&descriptors)?)?
        .emit_fields()
        .build(&[".tinyverse.v1"])?;
    Ok(())
}
