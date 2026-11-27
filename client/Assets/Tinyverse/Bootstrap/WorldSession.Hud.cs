using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tinyverse.Net;
using Tinyverse.UI;
using UnityEngine;
using P = Tinyverse.Proto;

namespace Tinyverse.Bootstrap
{
    // The HUD half of the session: chat, people, status bar, toasts and moderation.
    public sealed partial class WorldSession
    {
        /// <summary>Seqs of requests whose errors are reported by the code awaiting them, not by the generic toast.</summary>
        readonly HashSet<uint> handledSeqs = new HashSet<uint>();
        float statusTimer;
        bool joinedOnce;

        /// <summary>Toasts and system lines shown so far, newest last (for tests and logs).</summary>
        public readonly List<string> Notices = new List<string>();

        void UIOn<T>(Action<T> add, Action<T> remove, T handler) => On(add, remove, handler);

        void WireUI()
        {
            UIOn<Action<string, ChatChannel>>(h => ui.Chat.Submitted += h, h => ui.Chat.Submitted -= h, OnChatSubmitted);
            UIOn<Action<PersonInfo>>(h => ui.People.KickRequested += h, h => ui.People.KickRequested -= h,
                p => Admin(new P.Admin { Kick = new P.Admin.Types.Kick { UserId = p.UserId, Reason = "Kicked by the host." } }, $"{p.Name} was kicked."));
            UIOn<Action<PersonInfo>>(h => ui.People.BanRequested += h, h => ui.People.BanRequested -= h,
                p => Admin(new P.Admin { Ban = new P.Admin.Types.Ban { UserId = p.UserId, Reason = "Banned by the host." } }, $"{p.Name} was banned."));
            UIOn<Action<PersonInfo, Role>>(h => ui.People.RoleChangeRequested += h, h => ui.People.RoleChangeRequested -= h,
                (p, r) => Admin(new P.Admin { SetRole = new P.Admin.Types.SetRole { UserId = p.UserId, Role = ProtoConvert.ToProto(r) } },
                    $"{p.Name} is now {r.ToString().ToLowerInvariant()}."));
            WireBuild();
        }

        void HudWelcomed(P.Welcome w, bool reconnect)
        {
            BuildWelcomed(w);
            if (ui == null) return;
            ui.StatusBar.SetWorldName(w.World?.Name);
            ui.Menu.SetWorldName(w.World?.Name);
            ui.StatusBar.SetConnectionState(Tinyverse.UI.ConnectionState.Connected);
            ui.People.SetLocalRole(ProtoConvert.ToUI(w.Role));
            RefreshPeople();
        }

        void RoleChanged()
        {
            ui?.People.SetLocalRole(ProtoConvert.ToUI(Role));
            BuildRoleChanged();
        }

        void RefreshPeople()
        {
            if (ui == null) return;
            ui.People.SetPeople(people.Values.Select(a => ProtoConvert.ToPerson(a, OwnAvatarId)));
            ui.StatusBar.SetPeopleCount(people.Count);
        }

        // ---------------------------------------------------------------- chat

        void OnChatSubmitted(string text, ChatChannel channel)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (conn.SendChat(ProtoConvert.ToProto(channel), text) == 0)
                AddSystem("Not connected: the message was not sent.");
        }

        void OnChat(P.ChatEntry e)
        {
            if (e == null) return;
            ui?.Chat.AddLine(ProtoConvert.ToChatLine(e));
        }

        void OnChatHistory(P.ChatHistory h)
        {
            if (ui == null) return;
            var lines = h.Entries.Select(ProtoConvert.ToChatLine).ToList();
            ui.Chat.SetHistory(lines);
            // The history replaces the panel's lines, so the join or reconnect note comes after it.
            if (!joinedOnce)
            {
                joinedOnce = true;
                AddSystem($"Welcome to {WorldName}." + (lines.Count > 0 ? $" {lines.Count} recent message{(lines.Count == 1 ? "" : "s")} above." : ""));
            }
            else if (WelcomeCount > 1)
            {
                AddSystem("Reconnected.");
            }
        }

        /// <summary>A system line in the chat panel.</summary>
        public void AddSystem(string text)
        {
            Notices.Add(text);
            ui?.Chat.AddSystem(text);
        }

        void Toast(string text, ToastKind kind)
        {
            Notices.Add(text);
            Debug.Log($"[Session] toast ({kind}): {text}");
            if (ui != null) ui.Toasts.Show(text, kind);
        }

        // ---------------------------------------------------------------- errors and connection

        void OnError(P.Error e, uint replyTo)
        {
            if (replyTo != 0 && handledSeqs.Remove(replyTo)) return;
            Toast(ErrorText.Describe(e.Code, e.Message), ToastKind.Error);
        }

        void OnStateChanged(Net.ConnectionState s)
        {
            ui?.StatusBar.SetConnectionState(ProtoConvert.ToUI(s));
            if (s == Net.ConnectionState.Reconnecting) AddSystem("Connection lost. Reconnecting…");
        }

        void OnReconnectScheduled(int attempt, TimeSpan delay)
        {
            if (attempt > 1) AddSystem($"Still trying to reconnect (attempt {attempt})…");
        }

        // ---------------------------------------------------------------- requests

        /// <summary>
        /// Sends a request and reports a refusal through <paramref name="onError"/> (default: a toast)
        /// instead of the generic error toast.
        /// </summary>
        async void Request(P.ClientMessage message, Action<P.ServerMessage> onReply, Action<string> onError = null)
        {
            Task<P.ServerMessage> task = conn.RequestAsync(message);
            uint seq = message.Seq;
            if (seq != 0) handledSeqs.Add(seq);
            try
            {
                var reply = await task;
                if (disposed) return;
                onReply?.Invoke(reply);
            }
            catch (TinyverseServerException e)
            {
                if (disposed) return;
                string text = ErrorText.Describe(e.Code, e.ServerMessage);
                if (onError != null) onError(text);
                else Toast(text, ToastKind.Error);
            }
            catch (Exception e)
            {
                if (disposed) return;
                string text = e is TimeoutException ? "The server did not answer in time." : e.Message;
                if (onError != null) onError(text);
                else Toast(text, ToastKind.Error);
            }
            finally
            {
                handledSeqs.Remove(seq);
            }
        }

        void Admin(P.Admin admin, string done)
        {
            Request(new P.ClientMessage { Admin = admin }, _ => Toast(done, ToastKind.Info));
        }

        // ---------------------------------------------------------------- per frame

        void HudTick(float dt)
        {
            if (ui == null) return;
            statusTimer -= dt;
            if (statusTimer > 0f) return;
            statusTimer = 0.1f;
            long rtt = conn.RoundTripTimeMs;
            ui.StatusBar.SetPing(rtt >= 0 ? (int?)rtt : null);
            if (LocalAvatar != null)
            {
                ui.StatusBar.SetPosition(LocalAvatar.transform.position);
                ui.StatusBar.SetMoveMode(ProtoConvert.ToUI(LocalAvatar.CurrentPose.Mode));
            }
        }

        void HudDispose()
        {
            if (ui == null) return;
            ui.People.Clear();
            ui.StatusBar.SetConnectionState(Tinyverse.UI.ConnectionState.Disconnected);
        }
    }
}
