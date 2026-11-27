//! Reading tool arguments with messages that tell the model what to fix.
//! Lenient where it costs nothing (numbers as strings, `null` as absent),
//! strict about unknown names so typos do not pass silently.

use serde_json::{Map, Value};
use tv_proto::v1::Vec3;

pub struct Args<'a> {
    tool: &'a str,
    map: Map<String, Value>,
}

impl<'a> Args<'a> {
    /// Wraps the `arguments` of a call, rejecting names the tool does not know.
    pub fn new(tool: &'a str, value: Option<&Value>, allowed: &[&str]) -> Result<Self, String> {
        let map = match value {
            None | Some(Value::Null) => Map::new(),
            Some(Value::Object(m)) => m.clone(),
            Some(other) => {
                return Err(format!("{tool}: arguments must be a JSON object, got {other}"));
            }
        };
        let unknown: Vec<&str> =
            map.keys().map(String::as_str).filter(|k| !allowed.contains(k)).collect();
        if !unknown.is_empty() {
            return Err(format!(
                "{tool}: unknown argument{} {}; valid arguments are: {}",
                if unknown.len() == 1 { "" } else { "s" },
                unknown.iter().map(|k| format!("`{k}`")).collect::<Vec<_>>().join(", "),
                if allowed.is_empty() { "(none)".to_string() } else { allowed.join(", ") }
            ));
        }
        Ok(Args { tool, map })
    }

    fn get(&self, key: &str) -> Option<&Value> {
        self.map.get(key).filter(|v| !v.is_null())
    }

    pub fn has(&self, key: &str) -> bool {
        self.get(key).is_some()
    }

    pub fn err(&self, msg: impl std::fmt::Display) -> String {
        format!("{}: {msg}", self.tool)
    }

    pub fn missing(&self, key: &str) -> String {
        self.err(format!("`{key}` is required"))
    }

    pub fn f32(&self, key: &str) -> Result<Option<f32>, String> {
        let Some(v) = self.get(key) else { return Ok(None) };
        number(v).map(Some).ok_or_else(|| self.err(format!("`{key}` must be a number, got {v}")))
    }

    pub fn bool(&self, key: &str) -> Result<Option<bool>, String> {
        match self.get(key) {
            None => Ok(None),
            Some(Value::Bool(b)) => Ok(Some(*b)),
            Some(Value::String(s)) if s.eq_ignore_ascii_case("true") => Ok(Some(true)),
            Some(Value::String(s)) if s.eq_ignore_ascii_case("false") => Ok(Some(false)),
            Some(v) => Err(self.err(format!("`{key}` must be true or false, got {v}"))),
        }
    }

    pub fn string(&self, key: &str) -> Result<Option<String>, String> {
        match self.get(key) {
            None => Ok(None),
            Some(Value::String(s)) => Ok(Some(s.clone())),
            Some(v) => Err(self.err(format!("`{key}` must be a string, got {v}"))),
        }
    }

    /// One of a fixed set of lower-case words.
    pub fn choice(&self, key: &str, options: &[&str]) -> Result<Option<String>, String> {
        let Some(s) = self.string(key)? else { return Ok(None) };
        let t = s.trim().to_ascii_lowercase();
        if options.contains(&t.as_str()) {
            Ok(Some(t))
        } else {
            Err(self.err(format!("`{key}` must be one of {}, got {s:?}", options.join(", "))))
        }
    }

    pub fn u32(&self, key: &str, min: u32, max: u32) -> Result<Option<u32>, String> {
        let Some(v) = self.get(key) else { return Ok(None) };
        match number(v) {
            Some(n) if n.fract() == 0.0 && n >= min as f32 && n <= max as f32 => Ok(Some(n as u32)),
            _ => {
                Err(self
                    .err(format!("`{key}` must be a whole number from {min} to {max}, got {v}")))
            }
        }
    }

    /// A seconds value in `(0, max]`.
    pub fn seconds(&self, key: &str, max: f32) -> Result<Option<f32>, String> {
        match self.f32(key)? {
            None => Ok(None),
            Some(s) if s > 0.0 && s <= max => Ok(Some(s)),
            Some(s) => {
                Err(self
                    .err(format!("`{key}` must be more than 0 and at most {max} seconds, got {s}")))
            }
        }
    }

    /// A 64-bit entity id, given as a number or a string of digits.
    pub fn id(&self, key: &str) -> Result<Option<u64>, String> {
        let Some(v) = self.get(key) else { return Ok(None) };
        let parsed = match v {
            Value::Number(n) => n.as_u64(),
            Value::String(s) => s.trim().parse::<u64>().ok(),
            _ => None,
        };
        match parsed {
            Some(id) if id > 0 => Ok(Some(id)),
            _ => Err(self.err(format!(
                "`{key}` must be an object id (a positive whole number, as a number or string), \
                 got {v}"
            ))),
        }
    }

    /// A point given as `{"x":..,"y":..,"z":..}` or `[x, y, z]`.
    pub fn vec3(&self, key: &str) -> Result<Option<Vec3>, String> {
        let Some(v) = self.get(key) else { return Ok(None) };
        let bad = || {
            self.err(format!(
                "`{key}` must be an object {{\"x\": number, \"y\": number, \"z\": number}}, got {v}"
            ))
        };
        let (x, y, z) = match v {
            Value::Object(m) => {
                if let Some(k) = m.keys().find(|k| !["x", "y", "z"].contains(&k.as_str())) {
                    return Err(
                        self.err(format!("`{key}` has an unknown field `{k}`; use x, y and z"))
                    );
                }
                let c = |n: &str| m.get(n).and_then(number);
                (c("x"), c("y"), c("z"))
            }
            Value::Array(a) if a.len() == 3 => (number(&a[0]), number(&a[1]), number(&a[2])),
            _ => return Err(bad()),
        };
        match (x, y, z) {
            (Some(x), Some(y), Some(z)) => Ok(Some(Vec3 { x, y, z })),
            _ => Err(bad()),
        }
    }

    /// A scale: a vector, or a single number for the same size on every axis.
    pub fn scale(&self, key: &str) -> Result<Option<Vec3>, String> {
        if let Some(v) = self.get(key)
            && let Some(n) = number(v)
        {
            return Ok(Some(Vec3 { x: n, y: n, z: n }));
        }
        self.vec3(key)
    }
}

fn number(v: &Value) -> Option<f32> {
    let n = match v {
        Value::Number(n) => n.as_f64()?,
        Value::String(s) => s.trim().parse::<f64>().ok()?,
        _ => return None,
    };
    let n = n as f32;
    n.is_finite().then_some(n)
}
