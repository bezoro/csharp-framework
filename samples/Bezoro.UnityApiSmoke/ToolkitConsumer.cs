using System;
using System.Numerics;
using Bezoro.Core.Binary;
using Bezoro.Core.Collections;
using Bezoro.Core.Extensions;
using Bezoro.Core.Numerics;
using Bezoro.Inventory;
using Bezoro.Spatial;
using Bezoro.StateMachines;

namespace Bezoro.UnityApiSmoke
{
    public static class ToolkitConsumer
    {
        public static int ExerciseDataApis()
        {
            var coin = new ItemId(1);
            var catalog = new ItemCatalog(new[] { new ItemDefinition(coin, 10) });
            var backpack = new SlotInventory(catalog, 2);
            var stash = new SlotInventory(catalog, 1);
            if (!backpack.TryAdd(coin, 12) || !backpack.TryTransferTo(stash, coin, 5))
                throw new InvalidOperationException("Inventory transfer failed.");

            var quest = new StateMachineBuilder();
            var gathering = quest.State();
            var complete = quest.State();
            var collected = quest.Signal();
            quest.StartIn(gathering);
            quest.Transition(from: gathering, on: collected, to: complete);
            var machine = quest.Build();
            StateId done = quest.Resolve(complete);
            SignalId collect = quest.Resolve(collected);
            StateStep step = machine.Step(machine.InitialState, collect);

            var points = new PointSpatialIndex<int>(
                new Bounds2D(Vector2.Zero, new Vector2(8, 8)), 2, 4, 16);
            SlotHandle handle = points.Add(new Vector2(1, 1), 42);
            Span<SpatialHit<int>> hits = stackalloc SpatialHit<int>[4];
            SpatialQueryResult nearby = points.QueryCircle(Vector2.Zero, 2, hits);
            if (!nearby.IsSuccess || nearby.WrittenCount != 1 || hits[0].Handle != handle)
                throw new InvalidOperationException("Spatial query failed.");

            var completed = new BitSet(65);
            completed.Set(step.CurrentState.Value);
            Span<int> values = stackalloc int[] { 1, 2, 2, 4 };
            int first = values.LowerBound(2);
            int last = values.UpperBound(2);
            values = values.RemoveAtOrdered(first);
            values = values.RemoveAtSwapBack(values.Length - 1);

            Span<byte> payload = stackalloc byte[32];
            var writer = new BinarySpanWriter(payload);
            Span<byte> tag = stackalloc byte[] { 1, 2 };
            if (!writer.TryWriteUInt32(coin.Value) || !writer.TryWriteInt64(stash.GetQuantity(coin)) ||
                !writer.TryWriteLengthPrefixedBytes(tag))
                throw new InvalidOperationException("Checkpoint encoding failed.");
            var reader = new BinarySpanReader(writer.WrittenSpan);
            ReadOnlySpan<byte> restoredTag = reader.UnreadSpan.Slice(0, 0);
            if (!reader.TryReadUInt32(out uint id) || !reader.TryReadInt64(out long quantity) ||
                !reader.TryReadLengthPrefixedBytes(2, out restoredTag) ||
                id != coin.Value || quantity != 5 || restoredTag.Length != 2 || reader.Remaining != 0)
                throw new InvalidOperationException("Checkpoint decoding failed.");

            double position = ScalarMath.MoveTowards(0d, 10d, 2d);
            double opacity = ScalarMath.Damp(0d, 1d, 4d, 0.016d);
            if (position != 2 || opacity <= 0 || opacity >= 1 || (-3).FloorDivide(2) != -2 ||
                (-3).EuclideanModulo(2) != 1 || !completed[done.Value] || !step.HasStateChanged)
                throw new InvalidOperationException("Utility results failed.");
            return last - first + values.Length;
        }
    }
}
