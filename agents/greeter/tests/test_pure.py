"""Unit tests for the parts of the greeter that do not need a server."""

import math

import pytest

from greeter import protocol
from greeter.brains import Trigger, parse_build_request
from greeter.policy import Heard, Memory, ReplyPolicy, SpeechLimiter, fit_chat, is_addressed
from greeter.tools import (
    ToolError,
    build_create_object,
    build_delete_object,
    build_edit_object,
    build_look,
    build_move_to,
    build_say,
)
from greeter.world import Avatar, Proximity, WorldState, heading_from, point_near

ME = "user-me"


def state_with(*others: Avatar) -> WorldState:
    s = WorldState()
    s.apply("welcome", {
        "avatar": {"id": "1", "userId": ME, "name": "Greeter", "kind": "CLIENT_KIND_AGENT",
                   "transform": {"position": {"x": 0, "y": 0, "z": 0}}},
        "world": {"name": "W", "spawnPoint": {"x": 0, "y": 0, "z": 0},
                  "boundsMin": {"x": -10, "y": -1, "z": -10}, "boundsMax": {"x": 10, "y": 10, "z": 10}},
        "limits": {"walkSpeed": 4.0, "runSpeed": 8.0},
    })
    for a in others:
        s.avatars[a.id] = a
    return s


# ------------------------------------------------------------- messages

def test_hello_is_an_agent_hello():
    h = protocol.hello(name="G", token="t", body_color=(1, 0, 0))
    assert h["protocolVersion"] == 1
    assert h["kind"] == "CLIENT_KIND_AGENT"
    assert h["token"] == "t" and h["name"] == "G"
    assert h["appearance"]["bodyColor"] == {"r": 1, "g": 0, "b": 0, "a": 1.0}
    assert protocol.message(7, "hello", h) == {"seq": 7, "hello": h}


def test_body_of_finds_the_body():
    assert protocol.body_of({"replyTo": 3, "ack": {}}) == ("ack", {})
    assert protocol.body_of({"replyTo": 0, "pong": {"clientTimeMs": "1"}})[0] == "pong"
    assert protocol.body_of({"replyTo": 0}) == ("", {})


def test_colours_are_srgb_floats():
    assert protocol.parse_color("#ff8000") == pytest.approx({"r": 1.0, "g": 128 / 255, "b": 0.0, "a": 1.0})
    assert protocol.parse_color("#f00") == {"r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0}
    assert protocol.parse_color("#00000080")["a"] == pytest.approx(128 / 255)
    assert protocol.parse_color("Red")["r"] == pytest.approx(0.86)
    with pytest.raises(ValueError):
        protocol.parse_color("#12")
    with pytest.raises(ValueError):
        protocol.parse_color("mauve-ish")


def test_yaw_quaternion_matches_unity():
    q = protocol.yaw_quat(90)
    assert q == pytest.approx({"x": 0.0, "y": math.sqrt(0.5), "z": 0.0, "w": math.sqrt(0.5)})
    assert protocol.yaw_quat(0) == {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}


def test_enums_and_ids():
    assert protocol.shape_enum("cube") == "SHAPE_BOX"
    assert protocol.shape_enum("Ball") == "SHAPE_SPHERE"
    assert protocol.material_enum("glass") == "MATERIAL_GLASS"
    assert protocol.short_id(12) == "12" and protocol.short_id("12") == "12"
    with pytest.raises(ValueError):
        protocol.shape_enum("torus")


# ------------------------------------------------------------- world copy

def test_world_state_follows_events():
    s = state_with()
    s.apply("snapshot", {"avatars": [{"id": "2", "userId": "u2", "name": "Ada", "kind": "CLIENT_KIND_HUMAN",
                                      "transform": {"position": {"x": 3, "y": 0, "z": 4}}}],
                         "objects": [{"id": "9", "ownerId": ME, "spec": {"shape": "SHAPE_BOX"}}], "last": True})
    assert s.distance_to(s.avatars["2"]) == pytest.approx(5.0)
    s.apply("avatarStates", {"states": [{"avatarId": "2", "transform": {"position": {"x": 0, "y": 0, "z": 1}}},
                                        {"avatarId": "1", "transform": {"position": {"x": 0, "y": 0, "z": 2},
                                                                        "animation": "walk"}}]})
    assert s.avatars["2"].position == (0, 0, 1)
    assert s.me.position == (0, 0, 2) and s.me.animation == "walk"
    assert [o["id"] for o in s.own_objects()] == ["9"]
    s.apply("objectDeleted", {"id": "9"})
    s.apply("avatarLeft", {"avatarId": "2"})
    assert not s.objects and not s.avatars
    assert s.find_by_name("ada") is None


def test_geometry_helpers():
    assert point_near((10, 0, 0), (0, 0, 0), 1.5) == pytest.approx((8.5, 0, 0))
    assert point_near((1, 0, 0), (0, 0, 0), 1.5) == (0, 0, 0)
    assert heading_from((0, 0, 0), (1, 0, 0)) == pytest.approx(90)
    assert heading_from((0, 0, 0), (0, 0, -1)) == pytest.approx(180)


# ---------------------------------------------------------------- proximity

def test_proximity_greets_once_per_visit():
    p = Proximity(greet_radius=4)
    me = (0.0, 0.0, 0.0)
    assert p.update(me, {"2": (10, 0, 0)}) == []
    assert p.update(me, {"2": (3, 0, 0)}) == ["2"]
    assert p.update(me, {"2": (2, 0, 0)}) == []  # still the same visit
    assert p.update(me, {"2": (30, 0, 0)}) == []  # walked away
    assert p.update(me, {"2": (1, 0, 0)}) == []  # and back: same visit
    assert p.update(me, {}) == []  # left the world
    assert p.update(me, {"2": (1, 0, 0)}) == ["2"]  # a new visit


def test_proximity_reports_several_in_id_order():
    p = Proximity(greet_radius=4)
    assert p.update((0, 0, 0), {"10": (1, 0, 0), "9": (0, 0, 1), "11": (5, 0, 0)}) == ["9", "10"]
    assert p.greeted("9") and not p.greeted("11")


# ---------------------------------------------------- being a good guest

def test_speech_limiter():
    lim = SpeechLimiter(per_second=0.25, burst=3, now=0.0)
    assert [lim.allow(0.0) for _ in range(4)] == [True, True, True, False]
    assert not lim.allow(2.0)
    assert lim.allow(4.1)


def test_addressing():
    assert is_addressed("Greeter, come here", "Greeter")
    assert is_addressed("hey @greeter!", "Greeter")
    assert not is_addressed("the greeters are here", "Greeter")
    assert is_addressed("hi Old Tom", "Old Tom")
    assert not is_addressed("anything", "")


def heard(text, agent=False, channel="CHAT_CHANNEL_LOCAL", distance=3.0, user="u2"):
    return Heard(user, "Other", agent, channel, text, distance)


def test_reply_policy_for_humans():
    p = ReplyPolicy(ME, "Greeter", hear_radius=10)
    assert p.should_reply(heard("nice day"))  # local, nearby
    assert not p.should_reply(heard("nice day", distance=15))  # local, too far
    assert not p.should_reply(heard("nice day", channel="CHAT_CHANNEL_WORLD"))
    assert p.should_reply(heard("Greeter?", channel="CHAT_CHANNEL_WORLD", distance=math.inf))
    assert not p.should_reply(Heard(ME, "Greeter", True, "CHAT_CHANNEL_LOCAL", "Greeter", 0))  # myself


def test_reply_policy_stops_agent_loops():
    p = ReplyPolicy(ME, "Greeter", agent_replies=2, agent_window=300)
    assert not p.should_reply(heard("nice day", agent=True), now=0)  # not addressed
    assert p.should_reply(heard("Greeter, hi", agent=True), now=1)
    assert p.should_reply(heard("Greeter, hi", agent=True), now=2)
    assert not p.should_reply(heard("Greeter, hi", agent=True), now=3)
    assert p.should_reply(heard("Greeter, hi", agent=True, user="u3"), now=3)  # another agent
    assert p.should_reply(heard("Greeter, hi", agent=True), now=302)  # window passed


def test_memory_rolls_and_chat_fits():
    m = Memory(max_lines=3)
    assert m.transcript() == "(nothing yet)"
    for i in range(5):
        m.add(f"line {i}\nwith newline")
    assert m.transcript().splitlines() == ["line 2 with newline", "line 3 with newline", "line 4 with newline"]
    assert fit_chat("  a   b ") == "a b"
    assert len(fit_chat("x" * 600)) == 500


# ----------------------------------------------- tools to protocol messages

def test_look_and_say():
    assert build_look({}) == ("observe", {"radius": 0.0})
    assert build_look({"radius": 12}) == ("observe", {"radius": 12.0})
    with pytest.raises(ToolError):
        build_look({"radius": -1})
    assert build_say({"text": " hi  there "}) == ("chat", {"channel": "CHAT_CHANNEL_LOCAL", "text": "hi there"})
    with pytest.raises(ToolError):
        build_say({"text": "   "})


def test_move_to_person_or_point():
    ada = Avatar("2", "u2", "Ada", "CLIENT_KIND_HUMAN", "", position=(10, 0, 0))
    s = state_with(ada)
    body, payload = build_move_to({"person": "ada"}, s)
    assert body == "moveTo"
    assert payload["mode"] == "MOVE_MODE_WALK"
    assert payload["target"] == pytest.approx({"x": 8.5, "y": 0.0, "z": 0.0})
    _, payload = build_move_to({"x": 1, "z": -2, "run": True}, s)
    assert payload == {"target": {"x": 1.0, "y": 0.0, "z": -2.0}, "mode": "MOVE_MODE_RUN"}
    with pytest.raises(ToolError):
        build_move_to({"person": "Nobody"}, s)
    with pytest.raises(ToolError):
        build_move_to({"x": 1}, s)


def test_create_object_fills_a_full_spec():
    body, payload = build_create_object({"shape": "cube", "x": 1, "y": 0.5, "z": 2, "color": "#0000ff",
                                         "scale": {"x": 2, "y": 1, "z": 0.5}, "yaw_deg": 90, "name": "seat"})
    assert body == "objectCreate"
    spec = payload["spec"]
    assert spec["shape"] == "SHAPE_BOX"
    assert spec["position"] == {"x": 1.0, "y": 0.5, "z": 2.0}
    assert spec["scale"] == {"x": 2.0, "y": 1.0, "z": 0.5}
    assert spec["color"] == {"r": 0.0, "g": 0.0, "b": 1.0, "a": 1.0}
    assert spec["rotation"]["y"] == pytest.approx(math.sqrt(0.5))
    assert spec["material"] == "MATERIAL_MATTE" and spec["solid"] is True and spec["locked"] is False
    assert spec["name"] == "seat"
    with pytest.raises(ToolError):
        build_create_object({"shape": "box", "x": 0, "y": 0})
    with pytest.raises(ToolError):
        build_create_object({"shape": "torus", "x": 0, "y": 0, "z": 0})


def test_edit_and_delete_only_own_objects():
    s = state_with()
    s.objects["5"] = {"id": "5", "ownerId": ME, "spec": {"position": {"x": 1, "y": 2, "z": 3}}}
    s.objects["6"] = {"id": "6", "ownerId": "someone", "spec": {"position": {"x": 0, "y": 0, "z": 0}}}
    assert build_edit_object({"id": 5, "color": "white", "x": 4}, s) == ("objectUpdate", {
        "id": "5", "color": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0}, "position": {"x": 4.0, "y": 2.0, "z": 3.0}})
    assert build_delete_object({"id": "5"}, s) == ("objectDelete", {"id": "5"})
    for bad in ({"id": "6"}, {"id": "7"}, {"id": "x"}):
        with pytest.raises(ToolError):
            build_delete_object(bad, s)
    with pytest.raises(ToolError):
        build_edit_object({"id": "5"}, s)  # nothing to change


def test_rule_brain_understands_build_requests():
    assert parse_build_request("Greeter, build a red box please") == {"shape": "box", "color": "red"}
    assert parse_build_request("make me a ball") == {"shape": "sphere"}
    assert parse_build_request("build something") is None
    assert parse_build_request("a red box is nice") is None
    assert Trigger("chat", "Ada", False, "hi").channel == "CHAT_CHANNEL_LOCAL"
