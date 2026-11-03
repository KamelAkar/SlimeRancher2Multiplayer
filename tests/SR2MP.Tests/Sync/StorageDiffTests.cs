using SR2MP.Shared.Sync;
using Xunit;

namespace SR2MP.Tests.Sync;

public sealed class StorageDiffTests
{
    private const int Carrot = 10;
    private const int Beet = 11;

    private static StorageSlot S(int typeId, int count) => StorageSlot.Of(typeId, count);

    private static StorageSlot[] Apply(IReadOnlyList<StorageSlot> content, IEnumerable<StorageOperation> operations)
        => StorageDiff.Apply(content, operations);

    [Fact]
    public void Identical_contents_give_no_operation()
    {
        var content = new[] { S(Carrot, 3), StorageSlot.Empty };
        Assert.Empty(StorageDiff.Compute(content, content));
    }

    [Fact]
    public void Same_type_gives_one_delta()
    {
        var operations = StorageDiff.Compute(new[] { S(Carrot, 3) }, new[] { S(Carrot, 5) });
        Assert.Equal(new[] { new StorageOperation(0, Carrot, 2) }, operations);

        operations = StorageDiff.Compute(new[] { S(Carrot, 3) }, new[] { S(Carrot, 1) });
        Assert.Equal(new[] { new StorageOperation(0, Carrot, -2) }, operations);
    }

    [Fact]
    public void Type_change_removes_then_adds()
    {
        var operations = StorageDiff.Compute(new[] { S(Carrot, 2) }, new[] { S(Beet, 4) });
        Assert.Equal(new[] { new StorageOperation(0, Carrot, -2), new StorageOperation(0, Beet, 4) }, operations);
    }

    [Fact]
    public void An_emptied_slot_forgets_its_type()
    {
        Assert.Equal(StorageSlot.Empty, S(Carrot, 0));
        var operations = StorageDiff.Compute(new[] { S(Carrot, 2) }, new[] { new StorageSlot(Carrot, 0) });
        Assert.Equal(new[] { new StorageOperation(0, Carrot, -2) }, operations);
    }

    [Fact]
    public void Comparing_twice_sends_a_change_once()
    {
        // Nested game calls flush the same storage several times: only the first flush sees a difference.
        var mirror = new[] { S(Carrot, 1) };
        var current = new[] { S(Carrot, 2) };

        var first = StorageDiff.Compute(mirror, current);
        mirror = current;
        var second = StorageDiff.Compute(mirror, current);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void Random_contents_are_rebuilt_exactly()
    {
        var random = new Random(1234);
        int[] types = { Carrot, Beet, 12 };

        for (var round = 0; round < 2000; round++)
        {
            var size = random.Next(1, 6);
            var previous = Enumerable.Range(0, size).Select(_ => S(types[random.Next(types.Length)], random.Next(0, 4))).ToArray();
            var current = Enumerable.Range(0, size).Select(_ => S(types[random.Next(types.Length)], random.Next(0, 4))).ToArray();

            Assert.Equal(current, Apply(previous, StorageDiff.Compute(previous, current)));
        }
    }

    [Fact]
    public void Spamming_put_and_take_never_creates_items()
    {
        // The client deposits and takes in a fast random sequence, sending its diffs in order. The host replays them.
        var random = new Random(42);
        var client = new[] { S(Carrot, 5), StorageSlot.Empty };
        var host = client.ToArray();
        var mirror = client.ToArray();
        var carried = 0;

        for (var step = 0; step < 5000; step++)
        {
            var slot = client[0];
            if (random.Next(2) == 0 && carried > 0)
            {
                client[0] = S(Carrot, slot.Count + 1);
                carried--;
            }
            else if (!slot.IsEmpty)
            {
                client[0] = S(Carrot, slot.Count - 1);
                carried++;
            }

            // Not every step is flushed right away: several changes can pile up before the comparison runs.
            if (random.Next(3) != 0)
                continue;

            host = Apply(host, StorageDiff.Compute(mirror, client));
            mirror = client.ToArray();
        }

        host = Apply(host, StorageDiff.Compute(mirror, client));

        Assert.Equal(client, host);
        Assert.Equal(5, host[0].Count + carried);
    }

    [Fact]
    public void Late_host_answers_never_undo_unconfirmed_changes()
    {
        // The client spams with network delay. Host answers that predate its latest operations arrive while those are
        // still in flight. The client rebuilds its content from the answer plus its unconfirmed operations.
        var random = new Random(7);
        var host = new[] { S(Carrot, 1) };
        var client = host.ToArray();
        var mirror = client.ToArray();
        var pending = new List<(uint Sequence, StorageOperation Operation)>();
        var toHost = new Queue<(uint Sequence, StorageOperation Operation)>();
        var toClient = new Queue<(uint Acked, StorageSlot[] Content)>();
        uint sequence = 0, applied = 0;
        var carried = 0;

        for (var step = 0; step < 4000; step++)
        {
            if (random.Next(2) == 0 && !client[0].IsEmpty)
            {
                client[0] = S(Carrot, client[0].Count - 1);
                carried++;
            }
            else if (carried > 0)
            {
                client[0] = S(Carrot, client[0].Count + 1);
                carried--;
            }

            foreach (var operation in StorageDiff.Compute(mirror, client))
            {
                pending.Add((++sequence, operation));
                toHost.Enqueue((sequence, operation));
            }
            mirror = client.ToArray();

            if (toHost.Count > 0 && random.Next(3) == 0)
            {
                var (number, operation) = toHost.Dequeue();
                host = Apply(host, new[] { operation });
                applied = number;
                toClient.Enqueue((applied, host.ToArray()));
            }

            if (toClient.Count > 0 && random.Next(3) == 0)
            {
                var (acked, content) = toClient.Dequeue();
                pending.RemoveAll(entry => entry.Sequence <= acked);
                var rebuilt = Apply(content, pending.Select(entry => entry.Operation));
                Assert.Equal(client, rebuilt);
                client = rebuilt;
                mirror = client.ToArray();
            }
        }

        while (toHost.Count > 0)
            host = Apply(host, new[] { toHost.Dequeue().Operation });

        Assert.Equal(client, host);
        Assert.Equal(1, host[0].Count + carried);
    }
}
