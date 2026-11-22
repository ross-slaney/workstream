using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Workstream.Api.Desktops;

namespace Workstream.Tests;

/// <summary>What a viewer sends a display, as noVNC sends it (RFC 6143 and the extensions it speaks).</summary>
public static class Rfb
{
    public static byte[] Version(string version = "RFB 003.008\n") => Encoding.ASCII.GetBytes(version);
    /// <summary>The 3.8 handshake with no security: the version, security type None, and a shared ClientInit.</summary>
    public static byte[] Handshake() => [.. Version(), 1, 1];
    public static byte[] SetPixelFormat() => [0, 0, 0, 0, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0];
    public static byte[] SetEncodings(params int[] encodings)
    {
        var bytes = new byte[4 + 4 * encodings.Length];
        bytes[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)encodings.Length);
        for (var i = 0; i < encodings.Length; i++) BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4 + 4 * i), encodings[i]);
        return bytes;
    }
    public static byte[] UpdateRequest() => [3, 1, 0, 0, 0, 0, 7, 128, 4, 56];
    public static byte[] Key(uint key = 0x61, bool down = true)
    {
        var bytes = new byte[8];
        bytes[0] = 4;
        bytes[1] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), key);
        return bytes;
    }
    public static byte[] Pointer(ushort x = 100, ushort y = 200, byte buttons = 1)
    {
        var bytes = new byte[6];
        bytes[0] = 5;
        bytes[1] = buttons;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), x);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), y);
        return bytes;
    }
    /// <summary>Text for the desktop's clipboard; <paramref name="extended"/> sends it the extended clipboard's way, with a negative length.</summary>
    public static byte[] CutText(string text, bool extended = false)
    {
        var body = Encoding.Latin1.GetBytes(text);
        var bytes = new byte[8 + body.Length];
        bytes[0] = 6;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), extended ? -body.Length : body.Length);
        body.CopyTo(bytes, 8);
        return bytes;
    }
    public static byte[] ContinuousUpdates() => [150, 1, 0, 0, 0, 0, 7, 128, 4, 56];
    /// <summary>A fence carries up to 64 bytes it is answered with: the journeys mark their stream with one.</summary>
    public static byte[] Fence(byte[] payload)
    {
        var bytes = new byte[9 + payload.Length];
        bytes[0] = 248;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 0x80000000);
        bytes[8] = (byte)payload.Length;
        payload.CopyTo(bytes, 9);
        return bytes;
    }
    public static byte[] DesktopSize(int screens = 1)
    {
        var bytes = new byte[8 + 16 * screens];
        bytes[0] = 251;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 1920);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1080);
        bytes[6] = (byte)screens;
        return bytes;
    }
    public static byte[] QemuKey() => [255, 0, 0, 1, 0, 0, 0, 0x61, 0, 0, 0, 30];
    public static byte[] Xvp() => [250, 0, 1, 2];
}

/// <summary>Reading a viewer's stream so someone who may only watch never reaches the mouse, the keyboard or the clipboard.</summary>
[TestClass]
public sealed class RfbClientFilterTests
{
    static (bool Open, byte[] Through) Run(bool drive, IEnumerable<byte[]> chunks, RfbClientFilter? filter = null)
    {
        filter ??= new RfbClientFilter(drive);
        var output = new ArrayBufferWriter<byte>();
        foreach (var chunk in chunks)
        {
            if (!filter.Filter(chunk, output)) return (false, output.WrittenSpan.ToArray());
        }
        return (true, output.WrittenSpan.ToArray());
    }

    static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(x => x)];

    /// <summary>Every way of cutting the stream into pieces a WebSocket might deliver: whole, byte by byte, and in uneven pieces.</summary>
    static IEnumerable<IEnumerable<byte[]>> Deliveries(byte[] stream)
    {
        yield return [stream];
        yield return stream.Select(b => new[] { b });
        foreach (var size in new[] { 2, 3, 5, 7, 13 }) yield return stream.Chunk(size);
    }

    static readonly byte[] Picture = Join(
        Rfb.SetPixelFormat(), Rfb.SetEncodings(7, 16, 0, -223, -224, -308, -312), Rfb.UpdateRequest(), Rfb.ContinuousUpdates(),
        Rfb.Fence([1, 2, 3, 4]), Rfb.DesktopSize(2));

    static readonly byte[] Acting = Join(Rfb.Key(), Rfb.Key(down: false), Rfb.Pointer(), Rfb.CutText("hunter2"), Rfb.CutText("extended text", extended: true), Rfb.QemuKey(), Rfb.Xvp());

    [TestMethod]
    public void A_watcher_keeps_the_picture_and_loses_the_keyboard_the_mouse_and_the_clipboard()
    {
        var stream = Join(Rfb.Handshake(), Rfb.Key(), Rfb.SetPixelFormat(), Rfb.Pointer(), Rfb.UpdateRequest(), Rfb.CutText("hunter2"), Picture, Acting, Rfb.UpdateRequest());
        var expected = Join(Rfb.Handshake(), Rfb.SetPixelFormat(), Rfb.UpdateRequest(), Picture, Rfb.UpdateRequest());
        foreach (var delivery in Deliveries(stream))
        {
            var (open, through) = Run(drive: false, delivery);
            Assert.IsTrue(open);
            CollectionAssert.AreEqual(expected, through);
        }
    }

    [TestMethod]
    public void A_key_or_a_pointer_event_that_reaches_the_display_is_a_person_using_the_desktop()
    {
        foreach (var hand in new[] { Rfb.Key(), Rfb.Key(down: false), Rfb.Pointer(), Rfb.Pointer(buttons: 0), Rfb.QemuKey() })
        {
            foreach (var delivery in Deliveries(Join(Rfb.Handshake(), Picture, hand, Rfb.UpdateRequest())))
            {
                var filter = new RfbClientFilter(drive: true);
                Assert.IsTrue(Run(drive: true, delivery, filter).Open);
                Assert.IsTrue(filter.TakeInput());
                // Asked once, it is taken: the next answer is about what came after.
                Assert.IsFalse(filter.TakeInput());
            }
        }
    }

    [TestMethod]
    public void Asking_for_the_picture_is_not_using_the_desktop_however_long_it_goes_on()
    {
        // Everything an open pane sends by itself: the handshake, then requests for the picture, over and over.
        var stream = Join([Rfb.Handshake(), Picture, .. Enumerable.Repeat(Join(Rfb.UpdateRequest(), Rfb.ContinuousUpdates(), Rfb.Fence([9, 9]), Rfb.DesktopSize()), 50)]);
        foreach (var delivery in Deliveries(stream))
        {
            var filter = new RfbClientFilter(drive: true);
            Assert.IsTrue(Run(drive: true, delivery, filter).Open);
            Assert.IsFalse(filter.TakeInput());
        }
    }

    [TestMethod]
    public void The_clipboard_and_a_power_action_are_not_a_hand_on_the_desktop()
    {
        // A browser can send its clipboard without anyone touching the desktop.
        var filter = new RfbClientFilter(drive: true);
        Assert.IsTrue(Run(drive: true, [Join(Rfb.Handshake(), Rfb.CutText("hunter2"), Rfb.CutText("extended text", extended: true), Rfb.Xvp())], filter).Open);
        Assert.IsFalse(filter.TakeInput());
    }

    [TestMethod]
    public void What_a_watcher_sends_never_reaches_the_display_so_it_is_not_use()
    {
        foreach (var delivery in Deliveries(Join(Rfb.Handshake(), Picture, Acting, Rfb.UpdateRequest())))
        {
            var filter = new RfbClientFilter(drive: false);
            Assert.IsTrue(Run(drive: false, delivery, filter).Open);
            Assert.IsFalse(filter.TakeInput());
        }
        // Once they may use the desktop, what they send counts from then on.
        var shared = new RfbClientFilter(drive: false);
        Run(drive: false, [Join(Rfb.Handshake(), Rfb.Pointer())], shared);
        Assert.IsFalse(shared.TakeInput());
        shared.Drive = true;
        Run(drive: true, [Rfb.Pointer()], shared);
        Assert.IsTrue(shared.TakeInput());
    }

    [TestMethod]
    public void A_pointer_event_that_says_where_the_pointer_already_is_is_not_use()
    {
        foreach (var delivery in Deliveries(Join(Rfb.Handshake(), Rfb.Pointer(100, 200, 0))))
        {
            var filter = new RfbClientFilter(drive: true);
            Run(drive: true, delivery, filter);
            Assert.IsTrue(filter.TakeInput());
            // The same place and the same buttons, again and again: it still reaches the display, and nobody moved anything.
            var repeats = Join([.. Enumerable.Repeat(Rfb.Pointer(100, 200, 0), 20)]);
            var (open, through) = Run(drive: true, [repeats], filter);
            Assert.IsTrue(open);
            CollectionAssert.AreEqual(repeats, through);
            Assert.IsFalse(filter.TakeInput());
            // It moves, and then a button goes down where it is.
            Run(drive: true, [Rfb.Pointer(101, 200, 0)], filter);
            Assert.IsTrue(filter.TakeInput());
            Run(drive: true, [Rfb.Pointer(101, 200, 1)], filter);
            Assert.IsTrue(filter.TakeInput());
        }
    }

    [TestMethod]
    public void Input_is_noticed_again_after_it_was_taken()
    {
        var filter = new RfbClientFilter(drive: true);
        Run(drive: true, [Join(Rfb.Handshake(), Rfb.Key())], filter);
        Assert.IsTrue(filter.TakeInput());
        Run(drive: true, [Rfb.UpdateRequest()], filter);
        Assert.IsFalse(filter.TakeInput());
        Run(drive: true, [Rfb.Pointer()], filter);
        Assert.IsTrue(filter.TakeInput());
        Run(drive: true, [Rfb.Key(down: false)], filter);
        Assert.IsTrue(filter.TakeInput());
    }

    [TestMethod]
    public void A_driver_sends_everything_as_it_is()
    {
        var stream = Join(Rfb.Handshake(), Picture, Acting, Rfb.UpdateRequest());
        foreach (var delivery in Deliveries(stream))
        {
            var (open, through) = Run(drive: true, delivery);
            Assert.IsTrue(open);
            CollectionAssert.AreEqual(stream, through);
        }
    }

    [TestMethod]
    public void A_long_clipboard_streams_through_or_is_dropped_whole()
    {
        var text = new string('x', 200_000);
        var stream = Join(Rfb.Handshake(), Rfb.CutText(text), Rfb.UpdateRequest());
        foreach (var size in new[] { 1000, 65536 })
        {
            CollectionAssert.AreEqual(Join(Rfb.Handshake(), Rfb.UpdateRequest()), Run(drive: false, stream.Chunk(size)).Through);
            CollectionAssert.AreEqual(stream, Run(drive: true, stream.Chunk(size)).Through);
        }
    }

    [TestMethod]
    public void VNC_authentication_answers_with_sixteen_bytes_before_the_messages()
    {
        var answer = Enumerable.Range(4, 16).Select(x => (byte)x).ToArray();
        var stream = Join(Rfb.Version(), [2], answer, [1], Rfb.Key(), Rfb.UpdateRequest());
        CollectionAssert.AreEqual(Join(Rfb.Version(), [2], answer, [1], Rfb.UpdateRequest()), Run(drive: false, stream.Select(b => new[] { b })).Through);
    }

    [TestMethod]
    public void A_watcher_who_sends_what_cannot_be_read_is_closed_and_a_driver_goes_on()
    {
        // A message type nobody defined: where the next message starts is unknown from here.
        var unknown = Join(Rfb.Handshake(), Rfb.UpdateRequest(), [77, 1, 2, 3], Rfb.Key());
        var (open, through) = Run(drive: false, [unknown]);
        Assert.IsFalse(open);
        CollectionAssert.AreEqual(Join(Rfb.Handshake(), Rfb.UpdateRequest()), through);

        (open, through) = Run(drive: true, [unknown]);
        Assert.IsTrue(open);
        CollectionAssert.AreEqual(unknown, through);

        // A security type this cannot follow, and a version from before 3.7, whose security only the server knows.
        Assert.IsFalse(Run(drive: false, [Join(Rfb.Version(), [19], [0, 0, 1, 1])]).Open);
        Assert.IsFalse(Run(drive: false, [Join(Rfb.Version("RFB 003.003\n"), [1], Rfb.Key())]).Open);
        Assert.IsTrue(Run(drive: true, [Join(Rfb.Version("RFB 003.003\n"), [1], Rfb.Key())]).Open);
    }

    [TestMethod]
    public void Losing_the_mouse_and_keyboard_while_the_stream_is_open_takes_effect_at_the_next_message()
    {
        var filter = new RfbClientFilter(drive: true);
        var output = new ArrayBufferWriter<byte>();
        Assert.IsTrue(filter.Filter(Join(Rfb.Handshake(), Rfb.Key()), output));
        filter.Drive = false;
        Assert.IsTrue(filter.Filter(Join(Rfb.Pointer(), Rfb.UpdateRequest()), output));
        CollectionAssert.AreEqual(Join(Rfb.Handshake(), Rfb.Key(), Rfb.UpdateRequest()), output.WrittenSpan.ToArray());

        // A driver whose stream could not be read keeps it only while they still drive.
        var lost = new RfbClientFilter(drive: true);
        Assert.IsTrue(lost.Filter(Join(Rfb.Handshake(), [77]), new ArrayBufferWriter<byte>()));
        lost.Drive = false;
        Assert.IsFalse(lost.Filter(Rfb.UpdateRequest(), new ArrayBufferWriter<byte>()));
    }
}
