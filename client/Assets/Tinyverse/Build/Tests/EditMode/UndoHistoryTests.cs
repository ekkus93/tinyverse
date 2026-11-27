using System.Collections.Generic;
using NUnit.Framework;
using Tinyverse.World;
using UnityEngine;

namespace Tinyverse.Building.Tests
{
    public class UndoHistoryTests
    {
        /// <summary>A pretend server: requests apply at once, creates wait for <see cref="CompleteCreates"/>.</summary>
        sealed class FakeWorld : IUndoTarget
        {
            public readonly Dictionary<ulong, PrimData> Prims = new Dictionary<ulong, PrimData>();
            public readonly List<(PrimData data, int token)> PendingCreates = new List<(PrimData, int)>();
            public ulong NextId = 100;
            public int NextToken = 1;
            public int Updates, Deletes;

            public bool IsInState(ulong id, in PrimData expected) => Prims.TryGetValue(id, out var cur) && BuildMath.SameState(cur, expected);

            public void RequestUpdate(ulong id, PrimData data)
            {
                Updates++;
                Prims[id] = data;
            }

            public void RequestDelete(ulong id)
            {
                Deletes++;
                Prims.Remove(id);
            }

            public int ReserveToken() => NextToken++;

            public void RequestCreate(PrimData data, int token) => PendingCreates.Add((data, token));

            public ulong Add(PrimData d)
            {
                d.Id = NextId++;
                Prims[d.Id] = d;
                return d.Id;
            }

            /// <summary>Creates everything pending and reports the ids back.</summary>
            public List<ulong> CompleteCreates(UndoHistory h)
            {
                var ids = new List<ulong>();
                foreach (var (data, token) in PendingCreates)
                {
                    ulong id = Add(data);
                    Assert.That(h.NotifyCreated(token, id), Is.True);
                    ids.Add(id);
                }
                PendingCreates.Clear();
                return ids;
            }
        }

        static PrimData Box(Vector3 pos) => new PrimData
        {
            Shape = PrimShape.Box,
            Position = pos,
            Rotation = Quaternion.identity,
            Scale = Vector3.one,
            Color = Color.white,
            Solid = true,
        };

        [Test]
        public void CreateUndoDeletesRedoRecreatesWithNewId()
        {
            var w = new FakeWorld();
            var h = new UndoHistory();
            ulong id = w.Add(Box(Vector3.zero));
            h.RecordCreate(w.Prims[id]);
            Assert.That(h.CanUndo, Is.True);

            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Applied));
            Assert.That(w.Prims.ContainsKey(id), Is.False);
            Assert.That(h.CanUndo, Is.False);
            Assert.That(h.CanRedo, Is.True);

            Assert.That(h.Redo(w), Is.EqualTo(UndoResult.Applied));
            Assert.That(w.PendingCreates.Count, Is.EqualTo(1));
            Assert.That(w.PendingCreates[0].data.Id, Is.EqualTo(0UL), "creates go out with id 0");
            // Not created yet: undoing it again has to wait.
            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Busy));
            ulong newId = w.CompleteCreates(h)[0];
            Assert.That(newId, Is.Not.EqualTo(id));
            Assert.That(h.UndoSteps[0].Id, Is.EqualTo(newId));

            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Applied));
            Assert.That(w.Prims.ContainsKey(newId), Is.False);
        }

        [Test]
        public void DeleteUndoRecreatesAndRemapsEveryStep()
        {
            var w = new FakeWorld();
            var h = new UndoHistory();
            ulong id = w.Add(Box(Vector3.zero));
            h.RecordCreate(w.Prims[id]);

            // Move it twice, then delete it.
            var a = w.Prims[id];
            var b = a; b.Position = new Vector3(1, 0, 0);
            w.Prims[id] = b; h.RecordModify(id, a, b);
            var c = b; c.Position = new Vector3(2, 0, 0);
            w.Prims[id] = c; h.RecordModify(id, b, c);
            h.RecordDelete(c);
            w.Prims.Remove(id);

            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Applied)); // recreate
            ulong id2 = w.CompleteCreates(h)[0];
            Assert.That(w.Prims[id2].Position, Is.EqualTo(new Vector3(2, 0, 0)));
            foreach (var s in h.UndoSteps) Assert.That(s.Id, Is.EqualTo(id2), s.Kind.ToString());
            foreach (var s in h.RedoSteps) Assert.That(s.Id, Is.EqualTo(id2), s.Kind.ToString());

            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Applied)); // back to x = 1
            Assert.That(w.Prims[id2].Position, Is.EqualTo(new Vector3(1, 0, 0)));
            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Applied)); // back to x = 0
            Assert.That(w.Prims[id2].Position, Is.EqualTo(Vector3.zero));
            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Applied)); // un-create
            Assert.That(w.Prims.ContainsKey(id2), Is.False);
            Assert.That(h.CanUndo, Is.False);

            // Redo the whole lot: create (new id again), two moves, delete.
            Assert.That(h.Redo(w), Is.EqualTo(UndoResult.Applied));
            ulong id3 = w.CompleteCreates(h)[0];
            Assert.That(h.Redo(w), Is.EqualTo(UndoResult.Applied));
            Assert.That(h.Redo(w), Is.EqualTo(UndoResult.Applied));
            Assert.That(w.Prims[id3].Position, Is.EqualTo(new Vector3(2, 0, 0)));
            Assert.That(h.Redo(w), Is.EqualTo(UndoResult.Applied));
            Assert.That(w.Prims.ContainsKey(id3), Is.False);
            Assert.That(h.CanRedo, Is.False);
            Assert.That(h.Redo(w), Is.EqualTo(UndoResult.Nothing));
        }

        [Test]
        public void StepDroppedWhenSomeoneElseChangedThePrim()
        {
            var w = new FakeWorld();
            var h = new UndoHistory();
            ulong id = w.Add(Box(Vector3.zero));
            var before = w.Prims[id];
            var after = before; after.Color = Color.red;
            w.Prims[id] = after;
            h.RecordModify(id, before, after);

            var other = after; other.Position = new Vector3(5, 0, 0); // someone else moved it
            w.Prims[id] = other;
            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Dropped));
            Assert.That(w.Prims[id].Position, Is.EqualTo(new Vector3(5, 0, 0)), "their change is kept");
            Assert.That(h.CanUndo, Is.False);
            Assert.That(h.CanRedo, Is.False);
            Assert.That(w.Updates, Is.EqualTo(0));
        }

        [Test]
        public void StepDroppedWhenThePrimWasDeleted()
        {
            var w = new FakeWorld();
            var h = new UndoHistory();
            ulong keep = w.Add(Box(Vector3.one));
            var k0 = w.Prims[keep];
            var k1 = k0; k1.Scale = new Vector3(2, 2, 2);
            w.Prims[keep] = k1;
            h.RecordModify(keep, k0, k1);

            ulong id = w.Add(Box(Vector3.zero));
            h.RecordCreate(w.Prims[id]);
            w.Prims.Remove(id); // deleted by someone else

            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Dropped));
            Assert.That(w.Deletes, Is.EqualTo(0));
            // The older step still works.
            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Applied));
            Assert.That(w.Prims[keep].Scale, Is.EqualTo(Vector3.one));
        }

        [Test]
        public void NewActionClearsRedo()
        {
            var w = new FakeWorld();
            var h = new UndoHistory();
            ulong id = w.Add(Box(Vector3.zero));
            var a = w.Prims[id];
            var b = a; b.Name = "door";
            w.Prims[id] = b;
            h.RecordModify(id, a, b);
            h.Undo(w);
            Assert.That(h.CanRedo, Is.True);
            var c = a; c.Locked = true;
            w.Prims[id] = c;
            h.RecordModify(id, a, c);
            Assert.That(h.CanRedo, Is.False);
        }

        [Test]
        public void BoundedCapacityDropsOldest()
        {
            var w = new FakeWorld();
            var h = new UndoHistory(5);
            ulong id = w.Add(Box(Vector3.zero));
            for (int i = 0; i < 12; i++)
            {
                var a = w.Prims[id];
                var b = a; b.Position = new Vector3(i + 1, 0, 0);
                w.Prims[id] = b;
                h.RecordModify(id, a, b);
            }
            Assert.That(h.UndoCount, Is.EqualTo(5));
            for (int i = 0; i < 5; i++) Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Applied));
            Assert.That(w.Prims[id].Position.x, Is.EqualTo(7f));
            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Nothing));
        }

        [Test]
        public void FailedRecreateRemovesTheStep()
        {
            var w = new FakeWorld();
            var h = new UndoHistory();
            ulong id = w.Add(Box(Vector3.zero));
            h.RecordDelete(w.Prims[id]);
            w.Prims.Remove(id);
            Assert.That(h.Undo(w), Is.EqualTo(UndoResult.Applied));
            int token = w.PendingCreates[0].token;
            Assert.That(h.OwnsToken(token), Is.True);
            Assert.That(h.NotifyCreateFailed(token), Is.True);
            Assert.That(h.CanUndo || h.CanRedo, Is.False);
            Assert.That(h.NotifyCreated(token, 999), Is.False, "late reply ignored");
        }

        [Test]
        public void DropLatestForgetsTheRefusedStep()
        {
            var w = new FakeWorld();
            var h = new UndoHistory();
            ulong a = w.Add(Box(Vector3.zero));
            ulong b = w.Add(Box(Vector3.one));
            h.RecordDelete(w.Prims[a]);
            h.RecordDelete(w.Prims[b]);
            Assert.That(h.DropLatest(a), Is.True);
            Assert.That(h.UndoCount, Is.EqualTo(1));
            Assert.That(h.UndoSteps[0].Id, Is.EqualTo(b));
        }

        [Test]
        public void ChangedEventFires()
        {
            var w = new FakeWorld();
            var h = new UndoHistory();
            int n = 0;
            h.Changed += () => n++;
            ulong id = w.Add(Box(Vector3.zero));
            h.RecordCreate(w.Prims[id]);
            h.Undo(w);
            Assert.That(n, Is.GreaterThanOrEqualTo(2));
        }
    }
}
