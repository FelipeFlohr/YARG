using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YARG.Core.Input;
using YARG.Multiplayer.Protocol;
using YARG.Multiplayer.Sync;
using YARG.Multiplayer.Transport;

namespace YARG.Multiplayer.Tests.Sync
{
    public class RemoteInputTests
    {
        private sealed class MessageSink : INetEventListener
        {
            public readonly List<byte[]> Messages = new();

            public void OnPeerConnected(int peerId) { }
            public void OnPeerDisconnected(int peerId, string reason) { }
            public void OnMessage(int peerId, byte[] message) => Messages.Add(message);
        }

        private static InputBatch Batch(double watermark, params double[] times) =>
            new InputBatch(1, watermark, times.Select((t, i) => new GameInput(t, i, i)).ToArray());

        private static List<GameInput> Drain(RemoteInputBuffer buffer, double upTo)
        {
            var result = new List<GameInput>();
            while (buffer.TryDequeue(upTo, out var input))
            {
                result.Add(input);
            }
            return result;
        }

        [Test]
        public void Batcher_FlushesEvery50msEvenWhenEmpty()
        {
            var batcher = new InputBatcher(4);

            var first = batcher.TryFlush(0);
            Assert.That(first, Is.Not.Null);
            Assert.That(first!.Inputs, Is.Empty);
            Assert.That(first.PlayerId, Is.EqualTo(4));

            batcher.SetWatermark(0.02);
            Assert.That(batcher.TryFlush(0.03), Is.Null);

            var second = batcher.TryFlush(0.05);
            Assert.That(second, Is.Not.Null);
            Assert.That(second!.Inputs, Is.Empty);
            Assert.That(second.Watermark, Is.EqualTo(0.02));
        }

        [Test]
        public void Batcher_SendsEachInputExactlyOnce()
        {
            var batcher = new InputBatcher(1);
            batcher.Add(new GameInput(0.01, 1, 1));
            var first = batcher.TryFlush(0)!;
            batcher.Add(new GameInput(0.06, 2, 1));
            var second = batcher.TryFlush(0.05)!;

            Assert.That(first.Inputs.Select(i => i.Action), Is.EqualTo(new[] { 1 }));
            Assert.That(second.Inputs.Select(i => i.Action), Is.EqualTo(new[] { 2 }));
        }

        [Test]
        public void Buffer_TargetNeverPassesWatermark()
        {
            var buffer = new RemoteInputBuffer(0.2);
            Assert.That(buffer.Watermark, Is.EqualTo(double.NegativeInfinity));

            buffer.AddBatch(Batch(1.0));
            Assert.That(buffer.GetTargetTime(5.0), Is.EqualTo(1.0));
            // 1.1 - 0.2 = 0.9 would go backwards: the target is monotonic
            Assert.That(buffer.GetTargetTime(1.1), Is.EqualTo(1.0));
        }

        [Test]
        public void Buffer_TargetFollowsDelayWhenWatermarkIsAhead()
        {
            var buffer = new RemoteInputBuffer(0.2);
            buffer.AddBatch(Batch(10.0));
            Assert.That(buffer.GetTargetTime(3.0), Is.EqualTo(2.8).Within(1e-9));
        }

        [Test]
        public void Buffer_WatermarkNeverDecreases()
        {
            var buffer = new RemoteInputBuffer(0.2);
            buffer.AddBatch(Batch(2.0));
            buffer.AddBatch(Batch(1.0));
            Assert.That(buffer.Watermark, Is.EqualTo(2.0));
        }

        [Test]
        public void Buffer_DequeuesOnlyInputsUpToTarget()
        {
            var buffer = new RemoteInputBuffer(0.2);
            buffer.AddBatch(Batch(3.0, 0.5, 1.0, 1.5, 2.5));

            Assert.That(Drain(buffer, 1.0).Select(i => i.Time), Is.EqualTo(new[] { 0.5, 1.0 }));
            Assert.That(Drain(buffer, 2.0).Select(i => i.Time), Is.EqualTo(new[] { 1.5 }));
            Assert.That(Drain(buffer, 3.0).Select(i => i.Time), Is.EqualTo(new[] { 2.5 }));
        }

        [Test]
        public void Buffer_KeepsTimeOrderAcrossBatchesAndStableForTies()
        {
            var buffer = new RemoteInputBuffer(0.2);
            buffer.AddBatch(new InputBatch(1, 1.0, new[] { new GameInput(0.5, 1, 0), new GameInput(0.7, 2, 0) }));
            buffer.AddBatch(new InputBatch(1, 2.0, new[] { new GameInput(0.7, 3, 0), new GameInput(0.6, 4, 0) }));

            Assert.That(Drain(buffer, 2.0).Select(i => i.Action), Is.EqualTo(new[] { 1, 4, 2, 3 }));
        }

        [Test]
        public void Buffer_LateInputIsStillDeliveredAndCounted()
        {
            // Review Focus 1: a device input timestamped after the watermark was already sent
            var buffer = new RemoteInputBuffer(0.2);
            buffer.AddBatch(Batch(2.0, 1.0, 2.0));
            Assert.That(Drain(buffer, 2.0), Has.Count.EqualTo(2));

            buffer.AddBatch(Batch(2.1, 1.9));

            Assert.That(buffer.LateInputs, Is.EqualTo(1));
            Assert.That(Drain(buffer, 2.0).Select(i => i.Time), Is.EqualTo(new[] { 1.9 }));
        }

        [Test]
        public void Fidelity_InputsSurviveJitteryNetworkUnchanged()
        {
            var random = new Random(42);
            double time = 0;
            var sent = Enumerable.Range(0, 2000).Select(_ =>
            {
                time += random.NextDouble() * 0.06;
                return new GameInput(time, random.Next(0, 11), random.Next(-1000, 1000));
            }).ToList();
            Assert.That(sent.Last().Time, Is.LessThan(65));

            double now = 0;
            var network = new InMemoryNetwork();
            var receiverTransport = new LaggyTransport(network.CreateHost(), () => now, 0.08, 0.12, 42);
            var senderTransport = network.CreateClient();
            receiverTransport.StartHost(0);
            senderTransport.Connect("ignored", 0);

            var batcher = new InputBatcher(1);
            var buffer = new RemoteInputBuffer(0.2);
            var sink = new MessageSink();
            var received = new List<GameInput>();
            int nextToSend = 0;

            const double step = 1.0 / 60;
            for (int frame = 0; now < sent.Last().Time + 2; frame++)
            {
                now = frame * step;

                // Sender: the local engine has processed everything up to now
                while (nextToSend < sent.Count && sent[nextToSend].Time <= now)
                {
                    batcher.Add(sent[nextToSend++]);
                }
                batcher.SetWatermark(now);
                var batch = batcher.TryFlush(now);
                if (batch != null)
                {
                    senderTransport.Send(0, MessageCodec.Encode(batch));
                }

                // Receiver: the remote engine runs behind, never past the watermark
                receiverTransport.Poll(sink);
                foreach (var message in sink.Messages)
                {
                    buffer.AddBatch((InputBatch) MessageCodec.Decode(message));
                }
                sink.Messages.Clear();

                double target = buffer.GetTargetTime(now);
                while (buffer.TryDequeue(target, out var input))
                {
                    Assert.That(input.Time, Is.LessThanOrEqualTo(target));
                    received.Add(input);
                }
            }

            Assert.That(received.Select(i => (i.Time, i.Action, i.Integer)),
                Is.EqualTo(sent.Select(i => (i.Time, i.Action, i.Integer))));
            Assert.That(buffer.LateInputs, Is.EqualTo(0));
        }
    }
}
