"""The Claude tool-use loop against a fake Messages API (no network, no key).

The real `anthropic` SDK builds and sends each request; an httpx mock
transport answers with scripted responses and records what was sent.
"""

import asyncio
import json

import httpx2 as httpx  # the HTTP library the anthropic SDK uses
import pytest

from greeter.brains import ClaudeBrain, Trigger
from greeter.policy import Memory
from greeter.tools import ToolError
from greeter.world import WorldState


def response(content, stop_reason):
    return {"id": "msg_test", "type": "message", "role": "assistant", "model": "claude-opus-5-5",
            "content": content, "stop_reason": stop_reason, "stop_sequence": None,
            "usage": {"input_tokens": 10, "output_tokens": 5}}


class FakeTools:
    def __init__(self):
        self.calls = []
        self.created_this_request = 0

    async def run(self, name, args):
        self.calls.append((name, args))
        if name == "delete_object":
            raise ToolError("object 6 belongs to someone else; I only change my own objects")
        return f"{name} done"


def run_brain(script, monkeypatch, model="claude-opus-5-5"):
    monkeypatch.setenv("ANTHROPIC_API_KEY", "test-key")
    sent = []

    def handler(request: httpx.Request) -> httpx.Response:
        sent.append((request, json.loads(request.content)))
        return httpx.Response(200, json=script[len(sent) - 1])

    brain = ClaudeBrain("Greeter", model=model)
    brain.client = brain.anthropic.AsyncAnthropic(
        api_key="test-key", http_client=httpx.AsyncClient(transport=httpx.MockTransport(handler)))
    tools, said = FakeTools(), []

    async def speak(text, channel):
        said.append((text, channel))
        return True

    state = WorldState()
    state.world_name = "W"
    memory = Memory()
    memory.add("[local] Ada (human): hi")
    trigger = Trigger("chat", "Ada", False, "Greeter, build a box and remove that", "CHAT_CHANNEL_LOCAL", 3.0)
    asyncio.run(brain.handle(trigger, tools, speak, memory, state))
    return sent, tools, said


def test_tool_loop_runs_tools_and_speaks_the_final_text(monkeypatch):
    script = [
        response([{"type": "text", "text": "Sure."},
                  {"type": "tool_use", "id": "tu_1", "name": "create_object",
                   "input": {"shape": "box", "x": 1, "y": 0.5, "z": 2}},
                  {"type": "tool_use", "id": "tu_2", "name": "delete_object", "input": {"id": "6"}}],
                 "tool_use"),
        response([{"type": "text", "text": "Built you a box, Ada."}], "end_turn"),
    ]
    sent, tools, said = run_brain(script, monkeypatch)

    assert tools.calls == [("create_object", {"shape": "box", "x": 1, "y": 0.5, "z": 2}),
                           ("delete_object", {"id": "6"})]
    assert said == [("Built you a box, Ada.", "CHAT_CHANNEL_LOCAL")]

    first_req, first = sent[0]
    assert first_req.url.path == "/v1/messages"
    assert "server-side-fallback-2026-07-01" in first_req.headers["anthropic-beta"]
    assert first["model"] == "claude-opus-5-5"
    assert first["fallbacks"] == "default"
    assert first["output_config"] == {"effort": "low"}
    assert {t["name"] for t in first["tools"]} == {
        "look", "move_to", "say", "create_object", "edit_object", "delete_object"}
    assert "Greeter" in first["system"]
    prompt = first["messages"][0]["content"]
    assert "[local] Ada (human): hi" in prompt and "build a box" in prompt

    _, second = sent[1]
    assistant, results = second["messages"][1], second["messages"][2]
    assert assistant["role"] == "assistant" and len(assistant["content"]) == 3
    assert results["role"] == "user"
    assert [r["tool_use_id"] for r in results["content"]] == ["tu_1", "tu_2"]  # one message, all results
    assert results["content"][0]["is_error"] is False
    assert results["content"][1]["is_error"] is True
    assert "someone else" in results["content"][1]["content"]


def test_silent_and_refusal_say_nothing(monkeypatch):
    _, _, said = run_brain([response([{"type": "text", "text": "(silent)"}], "end_turn")], monkeypatch)
    assert said == []
    _, _, said = run_brain([response([], "refusal")], monkeypatch)
    assert said == []


def test_models_without_fallbacks_use_the_plain_endpoint(monkeypatch):
    sent, _, said = run_brain([response([{"type": "text", "text": "Hello!"}], "end_turn")], monkeypatch,
                              model="claude-haiku-5-5")
    req, body = sent[0]
    assert "fallbacks" not in body and "anthropic-beta" not in req.headers
    assert body["model"] == "claude-haiku-5-5"
    assert said == [("Hello!", "CHAT_CHANNEL_LOCAL")]


@pytest.mark.parametrize("status", [429, 500])
def test_api_errors_do_not_crash(monkeypatch, status):
    monkeypatch.setenv("ANTHROPIC_API_KEY", "test-key")
    brain = ClaudeBrain("Greeter")
    brain.client = brain.anthropic.AsyncAnthropic(
        api_key="test-key", max_retries=0,
        http_client=httpx.AsyncClient(transport=httpx.MockTransport(
            lambda r: httpx.Response(status, json={"type": "error", "error": {"type": "x", "message": "no"}}))))

    async def speak(text, channel):
        raise AssertionError("should not speak")

    asyncio.run(brain.handle(Trigger("greet", "Ada", False), FakeTools(), speak, Memory(), WorldState()))
