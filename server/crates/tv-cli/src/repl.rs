//! The line commands understood by `tv-cli connect` on stdin.

use tv_proto::v1::{Color, Material, MoveMode, ObjectSpec, Quat, Shape, Vec3};

use crate::parse::{parse_color, parse_f32, parse_shape};

/// One parsed stdin line.
#[derive(Clone, Debug, PartialEq)]
pub enum Line {
    /// Blank line: do nothing.
    Empty,
    WorldChat(String),
    LocalChat(String),
    MoveTo {
        target: Vec3,
        mode: MoveMode,
    },
    Stop,
    Look {
        radius: f32,
    },
    Create(ObjectSpec),
    Move {
        id: u64,
        position: Vec3,
    },
    Recolor {
        id: u64,
        color: Color,
    },
    Delete {
        id: u64,
    },
    History,
    Who,
    Help,
    Quit,
}

pub const HELP: &str = "\
commands:
  <text>                                   say in world chat
  /local <text>                            say in local chat
  /goto x y z                              walk there (MoveTo)
  /fly x y z                               fly there (MoveTo)
  /stop                                    cancel MoveTo
  /look [radius]                           Observe
  /box x y z [name]                        create a 1 m box
  /create <shape> x y z [sx sy sz] [#rrggbb] [name]
  /move <id> x y z                         move an object
  /color <id> #rrggbb                      recolour an object
  /delete <id>                             delete an object
  /history                                 request world chat history
  /who                                     list present avatars
  /help, /quit";

/// The default look of objects created from the command line.
pub fn default_spec(shape: Shape, position: Vec3) -> ObjectSpec {
    ObjectSpec {
        name: String::new(),
        shape: shape as i32,
        position: Some(position),
        rotation: Some(Quat { x: 0.0, y: 0.0, z: 0.0, w: 1.0 }),
        scale: Some(Vec3 { x: 1.0, y: 1.0, z: 1.0 }),
        color: Some(Color { r: 0.8, g: 0.8, b: 0.8, a: 1.0 }),
        material: Material::Matte as i32,
        solid: true,
        locked: false,
    }
}

fn xyz(args: &[&str], usage: &str) -> Result<Vec3, String> {
    match args {
        [x, y, z] => Ok(Vec3 { x: parse_f32(x)?, y: parse_f32(y)?, z: parse_f32(z)? }),
        _ => Err(format!("usage: {usage}")),
    }
}

fn id(s: &str) -> Result<u64, String> {
    s.parse().map_err(|_| format!("'{s}' is not an object id"))
}

/// Parses one stdin line.
pub fn parse_line(line: &str) -> Result<Line, String> {
    let line = line.trim();
    if line.is_empty() {
        return Ok(Line::Empty);
    }
    let Some(cmd) = line.strip_prefix('/') else { return Ok(Line::WorldChat(line.to_string())) };
    let (name, rest) = match cmd.split_once(char::is_whitespace) {
        Some((n, r)) => (n, r.trim()),
        None => (cmd, ""),
    };
    let args: Vec<&str> = rest.split_whitespace().collect();
    match name.to_ascii_lowercase().as_str() {
        "local" | "l" => {
            if rest.is_empty() {
                Err("usage: /local <text>".into())
            } else {
                Ok(Line::LocalChat(rest.to_string()))
            }
        }
        "goto" => Ok(Line::MoveTo { target: xyz(&args, "/goto x y z")?, mode: MoveMode::Walk }),
        "run" => Ok(Line::MoveTo { target: xyz(&args, "/run x y z")?, mode: MoveMode::Run }),
        "fly" => Ok(Line::MoveTo { target: xyz(&args, "/fly x y z")?, mode: MoveMode::Fly }),
        "stop" => Ok(Line::Stop),
        "look" => match args.as_slice() {
            [] => Ok(Line::Look { radius: 0.0 }),
            [r] => Ok(Line::Look { radius: parse_f32(r)? }),
            _ => Err("usage: /look [radius]".into()),
        },
        "box" => {
            if args.len() < 3 {
                return Err("usage: /box x y z [name]".into());
            }
            let mut spec = default_spec(Shape::Box, xyz(&args[..3], "/box x y z [name]")?);
            spec.name = args[3..].join(" ");
            Ok(Line::Create(spec))
        }
        "create" => parse_create(&args),
        "move" => match args.as_slice() {
            [i, rest @ ..] => {
                Ok(Line::Move { id: id(i)?, position: xyz(rest, "/move <id> x y z")? })
            }
            _ => Err("usage: /move <id> x y z".into()),
        },
        "color" | "colour" => match args.as_slice() {
            [i, c] => Ok(Line::Recolor { id: id(i)?, color: parse_color(c)? }),
            _ => Err("usage: /color <id> #rrggbb".into()),
        },
        "delete" | "del" => match args.as_slice() {
            [i] => Ok(Line::Delete { id: id(i)? }),
            _ => Err("usage: /delete <id>".into()),
        },
        "history" => Ok(Line::History),
        "who" => Ok(Line::Who),
        "help" | "?" => Ok(Line::Help),
        "quit" | "exit" | "q" => Ok(Line::Quit),
        other => Err(format!("unknown command /{other}; /help lists them")),
    }
}

fn parse_create(args: &[&str]) -> Result<Line, String> {
    const USAGE: &str = "usage: /create <shape> x y z [sx sy sz] [#rrggbb] [name]";
    if args.len() < 4 {
        return Err(USAGE.into());
    }
    let shape = parse_shape(args[0])?;
    let mut spec = default_spec(shape, xyz(&args[1..4], USAGE)?);
    let mut rest = &args[4..];
    // Three numbers in a row are a scale.
    if rest.len() >= 3 && rest[..3].iter().all(|a| parse_f32(a).is_ok()) {
        spec.scale = Some(xyz(&rest[..3], USAGE)?);
        rest = &rest[3..];
    }
    if let Some(c) = rest.first().filter(|a| a.starts_with('#')) {
        spec.color = Some(parse_color(c)?);
        rest = &rest[1..];
    }
    spec.name = rest.join(" ");
    Ok(Line::Create(spec))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn v(x: f32, y: f32, z: f32) -> Vec3 {
        Vec3 { x, y, z }
    }

    #[test]
    fn plain_text_is_world_chat() {
        assert_eq!(parse_line("  hello there "), Ok(Line::WorldChat("hello there".into())));
        assert_eq!(parse_line(""), Ok(Line::Empty));
        assert_eq!(parse_line("   "), Ok(Line::Empty));
    }

    #[test]
    fn chat_and_movement() {
        assert_eq!(parse_line("/local hi  you"), Ok(Line::LocalChat("hi  you".into())));
        assert!(parse_line("/local").is_err());
        assert_eq!(
            parse_line("/goto 1 0 -2.5"),
            Ok(Line::MoveTo { target: v(1.0, 0.0, -2.5), mode: MoveMode::Walk })
        );
        assert_eq!(
            parse_line("/fly 1 10 2"),
            Ok(Line::MoveTo { target: v(1.0, 10.0, 2.0), mode: MoveMode::Fly })
        );
        assert!(parse_line("/goto 1 2").is_err());
        assert!(parse_line("/goto 1 2 x").is_err());
        assert_eq!(parse_line("/stop"), Ok(Line::Stop));
        assert_eq!(parse_line("/look"), Ok(Line::Look { radius: 0.0 }));
        assert_eq!(parse_line("/look 25"), Ok(Line::Look { radius: 25.0 }));
        assert!(parse_line("/look far").is_err());
    }

    #[test]
    fn box_and_create() {
        let Ok(Line::Create(s)) = parse_line("/box 1 2 3 my little box") else { panic!() };
        assert_eq!(s.shape, Shape::Box as i32);
        assert_eq!(s.position, Some(v(1.0, 2.0, 3.0)));
        assert_eq!(s.name, "my little box");
        assert_eq!(s.scale, Some(v(1.0, 1.0, 1.0)));

        let Ok(Line::Create(s)) = parse_line("/box 1 2 3") else { panic!() };
        assert_eq!(s.name, "");

        let Ok(Line::Create(s)) = parse_line("/create sphere 0 1 0 2 2 2 #ff0000 ball") else {
            panic!()
        };
        assert_eq!(s.shape, Shape::Sphere as i32);
        assert_eq!(s.scale, Some(v(2.0, 2.0, 2.0)));
        assert_eq!(s.color.unwrap().r, 1.0);
        assert_eq!(s.color.unwrap().g, 0.0);
        assert_eq!(s.name, "ball");

        let Ok(Line::Create(s)) = parse_line("/create cone 0 1 0 #00ff00") else { panic!() };
        assert_eq!(s.scale, Some(v(1.0, 1.0, 1.0)));
        assert_eq!(s.color.unwrap().g, 1.0);

        let Ok(Line::Create(s)) = parse_line("/create plane 0 0 0 10 1 10 floor") else { panic!() };
        assert_eq!(s.scale, Some(v(10.0, 1.0, 10.0)));
        assert_eq!(s.name, "floor");

        assert!(parse_line("/create cube 0 0 0").is_err());
        assert!(parse_line("/create box 0 0").is_err());
        assert!(parse_line("/create box 0 0 0 1 1 1 #zz0000").is_err());
        assert!(parse_line("/box").is_err());
    }

    #[test]
    fn object_edits() {
        assert_eq!(
            parse_line("/move 7 1 2 3"),
            Ok(Line::Move { id: 7, position: v(1.0, 2.0, 3.0) })
        );
        assert!(parse_line("/move x 1 2 3").is_err());
        assert!(parse_line("/move 7 1 2").is_err());
        let Ok(Line::Recolor { id: 9, color }) = parse_line("/color 9 #0000ff") else { panic!() };
        assert_eq!(color.b, 1.0);
        assert!(parse_line("/color 9").is_err());
        assert_eq!(parse_line("/delete 12"), Ok(Line::Delete { id: 12 }));
        assert!(parse_line("/delete -1").is_err());
    }

    #[test]
    fn misc() {
        assert_eq!(parse_line("/history"), Ok(Line::History));
        assert_eq!(parse_line("/who"), Ok(Line::Who));
        assert_eq!(parse_line("/QUIT"), Ok(Line::Quit));
        assert_eq!(parse_line("/help"), Ok(Line::Help));
        let err = parse_line("/dance").unwrap_err();
        assert!(err.contains("unknown command /dance"), "{err}");
    }
}
