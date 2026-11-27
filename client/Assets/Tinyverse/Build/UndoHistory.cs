using System;
using System.Collections.Generic;
using Tinyverse.World;

namespace Tinyverse.Building
{
    public enum UndoKind
    {
        /// <summary>The user created a prim. Undo deletes it; redo creates it again (new id).</summary>
        Create,
        /// <summary>The user deleted a prim. Undo creates it again (new id); redo deletes it.</summary>
        Delete,
        /// <summary>The user changed a prim. Undo restores <see cref="UndoStep.Before"/>; redo applies <see cref="UndoStep.After"/>.</summary>
        Modify,
    }

    /// <summary>One recorded action. Ids are remapped in place when an undo or redo recreates a prim.</summary>
    public sealed class UndoStep
    {
        public UndoKind Kind;
        /// <summary>The prim's current id (0 while it does not exist).</summary>
        public ulong Id;
        /// <summary>State before the action (Delete, Modify).</summary>
        public PrimData Before;
        /// <summary>State after the action (Create, Modify).</summary>
        public PrimData After;
        /// <summary>Non-zero while a create raised by undo/redo awaits its server id.</summary>
        public int PendingToken;
    }

    public enum UndoResult
    {
        /// <summary>The step's request was raised.</summary>
        Applied,
        /// <summary>Nothing to undo or redo.</summary>
        Nothing,
        /// <summary>The prim was deleted or changed by someone else since; the step was discarded.</summary>
        Dropped,
        /// <summary>The step waits for a server id from an earlier recreate; try again shortly.</summary>
        Busy,
    }

    /// <summary>What <see cref="UndoHistory"/> acts on. The build controller implements it; tests fake it.</summary>
    public interface IUndoTarget
    {
        /// <summary>True if the prim exists and is in (or has just been asked to be in) the given state.</summary>
        bool IsInState(ulong id, in PrimData expected);
        void RequestUpdate(ulong id, PrimData data);
        void RequestDelete(ulong id);
        /// <summary>A fresh, non-zero create request token.</summary>
        int ReserveToken();
        /// <summary>Raises a create with a token from <see cref="ReserveToken"/>. The reply may arrive synchronously.</summary>
        void RequestCreate(PrimData data, int token);
    }

    /// <summary>
    /// Bounded undo/redo of the local user's own actions. Every undo or redo is only a request: if the prim has
    /// since been deleted or changed by someone else, the step is dropped. A recreated prim gets a new id from
    /// the server; <see cref="NotifyCreated"/> remaps every step that referred to the old one.
    /// </summary>
    public sealed class UndoHistory
    {
        public const int DefaultCapacity = 100;

        readonly List<UndoStep> undo = new List<UndoStep>();
        readonly List<UndoStep> redo = new List<UndoStep>();
        readonly Dictionary<int, UndoStep> pending = new Dictionary<int, UndoStep>();

        public UndoHistory(int capacity = DefaultCapacity)
        {
            Capacity = Math.Max(1, capacity);
        }

        public int Capacity { get; }
        public int UndoCount => undo.Count;
        public int RedoCount => redo.Count;
        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;

        /// <summary>The steps, oldest first (for inspection and tests).</summary>
        public IReadOnlyList<UndoStep> UndoSteps => undo;
        public IReadOnlyList<UndoStep> RedoSteps => redo;

        /// <summary>Raised whenever <see cref="CanUndo"/> or <see cref="CanRedo"/> may have changed.</summary>
        public event Action Changed;

        public void RecordCreate(PrimData created) =>
            Record(new UndoStep { Kind = UndoKind.Create, Id = created.Id, After = created });

        public void RecordDelete(PrimData before) =>
            Record(new UndoStep { Kind = UndoKind.Delete, Id = before.Id, Before = before });

        public void RecordModify(ulong id, PrimData before, PrimData after)
        {
            before.Id = id;
            after.Id = id;
            Record(new UndoStep { Kind = UndoKind.Modify, Id = id, Before = before, After = after });
        }

        void Record(UndoStep step)
        {
            undo.Add(step);
            while (undo.Count > Capacity)
            {
                Forget(undo[0]);
                undo.RemoveAt(0);
            }
            foreach (var s in redo) Forget(s);
            redo.Clear();
            Changed?.Invoke();
        }

        void Forget(UndoStep s)
        {
            if (s.PendingToken != 0) pending.Remove(s.PendingToken);
            s.PendingToken = 0;
        }

        public UndoResult Undo(IUndoTarget target) => Run(target, undo, redo, true);

        public UndoResult Redo(IUndoTarget target) => Run(target, redo, undo, false);

        UndoResult Run(IUndoTarget target, List<UndoStep> from, List<UndoStep> to, bool isUndo)
        {
            if (from.Count == 0) return UndoResult.Nothing;
            var step = from[from.Count - 1];
            if (step.PendingToken != 0) return UndoResult.Busy;

            // Create-undo and Delete-redo remove the prim; Delete-undo and Create-redo recreate it.
            bool removes = (step.Kind == UndoKind.Create) == isUndo;
            if (step.Kind == UndoKind.Modify)
            {
                var expected = isUndo ? step.After : step.Before;
                if (!target.IsInState(step.Id, expected)) return Drop(from);
                var apply = isUndo ? step.Before : step.After;
                apply.Id = step.Id;
                from.RemoveAt(from.Count - 1);
                to.Add(step);
                target.RequestUpdate(step.Id, apply);
            }
            else if (removes)
            {
                var expected = step.Kind == UndoKind.Create ? step.After : step.Before;
                if (!target.IsInState(step.Id, expected)) return Drop(from);
                from.RemoveAt(from.Count - 1);
                to.Add(step);
                target.RequestDelete(step.Id);
            }
            else
            {
                var data = step.Kind == UndoKind.Create ? step.After : step.Before;
                data.Id = 0;
                from.RemoveAt(from.Count - 1);
                to.Add(step);
                int token = target.ReserveToken();
                step.PendingToken = token;
                pending[token] = step;
                Changed?.Invoke();
                target.RequestCreate(data, token);
                return UndoResult.Applied;
            }
            Changed?.Invoke();
            return UndoResult.Applied;
        }

        UndoResult Drop(List<UndoStep> from)
        {
            Forget(from[from.Count - 1]);
            from.RemoveAt(from.Count - 1);
            Changed?.Invoke();
            return UndoResult.Dropped;
        }

        /// <summary>True if the token belongs to a create raised by this history.</summary>
        public bool OwnsToken(int token) => pending.ContainsKey(token);

        /// <summary>A recreate succeeded: every step that referred to the old id now refers to the new one.</summary>
        public bool NotifyCreated(int token, ulong newId)
        {
            if (!pending.TryGetValue(token, out var step)) return false;
            pending.Remove(token);
            step.PendingToken = 0;
            ulong oldId = step.Id;
            Remap(oldId, newId);
            step.Id = newId; // also covers oldId == 0
            step.Before.Id = newId;
            step.After.Id = newId;
            return true;
        }

        /// <summary>A recreate failed: its step can no longer be replayed and is removed.</summary>
        public bool NotifyCreateFailed(int token)
        {
            if (!pending.TryGetValue(token, out var step)) return false;
            pending.Remove(token);
            step.PendingToken = 0;
            undo.Remove(step);
            redo.Remove(step);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Replaces an id in every step.</summary>
        public void Remap(ulong oldId, ulong newId)
        {
            if (oldId == 0 || oldId == newId) return;
            Remap(undo, oldId, newId);
            Remap(redo, oldId, newId);
        }

        static void Remap(List<UndoStep> list, ulong oldId, ulong newId)
        {
            foreach (var s in list)
            {
                if (s.Id != oldId) continue;
                s.Id = newId;
                s.Before.Id = newId;
                s.After.Id = newId;
            }
        }

        /// <summary>The server refused the latest request for this prim: forget the newest undo step about it.</summary>
        public bool DropLatest(ulong id)
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                if (undo[i].Id != id || undo[i].PendingToken != 0) continue;
                undo.RemoveAt(i);
                Changed?.Invoke();
                return true;
            }
            return false;
        }

        public void Clear()
        {
            undo.Clear();
            redo.Clear();
            pending.Clear();
            Changed?.Invoke();
        }
    }
}
