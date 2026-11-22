using System.Buffers;
using System.Buffers.Binary;

namespace Workstream.Api.Desktops;

/// <summary>
/// Reads what a viewer sends a desktop's display, the client half of the remote framebuffer protocol (RFC 6143),
/// so the relay can keep someone who may only watch off the mouse and keyboard. After the handshake (the
/// version, the security type, its answer, the client's init) the stream is messages, each with a type and a
/// length that the type gives. A viewer without <see cref="Drive"/> keeps every message that only asks for the
/// picture (pixel format, encodings, update requests, continuous updates, fences, the desktop's size) and loses
/// the ones that act on the desktop: key and pointer events, the clipboard, QEMU's key events and xvp's power
/// actions. A message it cannot read means it no longer knows where the next one starts: for a watcher the
/// stream is closed, and a driver's bytes go through as they are from then on.
///
/// It also notices use: a key event that reaches the display, or a pointer event that moves the pointer or
/// changes a button, is a person at the desktop's mouse or keyboard (<see cref="TakeInput"/>). Asking for the
/// picture is not, however long the stream stays open, and neither is a pointer event that says again where
/// the pointer already is, which a browser can send with nobody there.
/// </summary>
public sealed class RfbClientFilter
{
    enum Stage { Version, SecurityType, VncAuth, ClientInit, Messages, Opaque }

    const byte SetPixelFormat = 0;
    const byte SetEncodings = 2;
    const byte FramebufferUpdateRequest = 3;
    const byte KeyEvent = 4;
    const byte PointerEvent = 5;
    const byte ClientCutText = 6;
    const byte EnableContinuousUpdates = 150;
    const byte ClientFence = 248;
    const byte Xvp = 250;
    const byte SetDesktopSize = 251;
    const byte Qemu = 255;

    const int VersionLength = 12;
    const int VncAuthLength = 16;
    Stage stage = Stage.Version;
    readonly byte[] header = new byte[VersionLength];
    int held;
    /// <summary>Bytes of the current message still to come, and whether they go through.</summary>
    long remaining;
    bool passing;
    volatile bool drive;
    int input;
    /// <summary>The buttons and the place of the last pointer event that reached the display, to tell a move from a repeat.</summary>
    readonly byte[] pointer = new byte[5];
    bool pointed;

    public RfbClientFilter(bool drive) => this.drive = drive;

    /// <summary>Whether this viewer may use the mouse and keyboard. Checked again while the stream is open.</summary>
    public bool Drive
    {
        get => drive;
        set => drive = value;
    }

    /// <summary>
    /// Whether a key or pointer event has reached the display since this was last asked. The relay asks, and
    /// writes down that the desktop is in use.
    /// </summary>
    public bool TakeInput() => Interlocked.Exchange(ref input, 0) == 1;

    /// <summary>
    /// Takes the next bytes from the viewer and writes what may reach the display to <paramref name="output"/>.
    /// False when the stream must be closed: a watcher sent something this cannot read.
    /// </summary>
    public bool Filter(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        while (input.Length > 0)
        {
            if (stage == Stage.Opaque)
            {
                if (!drive) return false;
                output.Write(input);
                return true;
            }
            if (remaining > 0)
            {
                var take = (int)Math.Min(remaining, input.Length);
                if (passing) output.Write(input[..take]);
                remaining -= take;
                input = input[take..];
                continue;
            }
            header[held++] = input[0];
            input = input[1..];
            if (!Step(output)) return false;
        }
        // A watcher who lost their place is closed as soon as it is known, not at their next byte.
        return stage != Stage.Opaque || drive;
    }

    /// <summary>One more byte is held: act on what is held when it is enough to.</summary>
    bool Step(IBufferWriter<byte> output)
    {
        switch (stage)
        {
            case Stage.Version:
                if (held < VersionLength) return true;
                // "RFB 003.008\n". Before 3.7 the server alone chooses the security, which this side cannot see.
                var minor = (header[8] - '0') * 100 + (header[9] - '0') * 10 + (header[10] - '0');
                var readable = header[0] == 'R' && header[1] == 'F' && header[2] == 'B' && minor >= 7 && minor < 900;
                Pass(output);
                stage = readable ? Stage.SecurityType : Stage.Opaque;
                return true;
            case Stage.SecurityType:
                var type = header[0];
                Pass(output);
                // 1 is None; 2 is VNC authentication, answered with sixteen bytes. Anything else speaks its own way.
                stage = type switch { 1 => Stage.ClientInit, 2 => Stage.VncAuth, _ => Stage.Opaque };
                return true;
            case Stage.VncAuth:
                // The first byte of the answer is held; the rest streams through.
                Pass(output);
                remaining = VncAuthLength - 1;
                passing = true;
                stage = Stage.ClientInit;
                return true;
            case Stage.ClientInit:
                Pass(output);
                stage = Stage.Messages;
                return true;
            default:
                return Message(output);
        }
    }

    /// <summary>The held bytes of a message: once its whole length is known, pass or drop it and stream the rest.</summary>
    bool Message(IBufferWriter<byte> output)
    {
        var type = header[0];
        long length;
        bool acts;
        // A person's hand on the keyboard or the mouse. The clipboard and a power action are not: a browser can send those by itself.
        var hand = false;
        switch (type)
        {
            case SetPixelFormat: length = 20; acts = false; break;
            case FramebufferUpdateRequest: length = 10; acts = false; break;
            case EnableContinuousUpdates: length = 10; acts = false; break;
            case KeyEvent: length = 8; acts = true; hand = true; break;
            case PointerEvent:
                // The whole event is held, to see whether the pointer moved or a button changed.
                if (held < 6) return true;
                length = 6;
                acts = true;
                if (drive)
                {
                    var now = header.AsSpan(1, 5);
                    hand = !pointed || !now.SequenceEqual(pointer);
                    now.CopyTo(pointer);
                    pointed = true;
                }
                break;
            case Xvp: length = 4; acts = true; break;
            case SetEncodings:
                if (held < 4) return true;
                length = 4 + 4L * BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
                acts = false;
                break;
            case ClientCutText:
                if (held < 8) return true;
                // A negative length is the extended clipboard's: its size is the absolute value.
                length = 8 + Math.Abs((long)BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4)));
                acts = true;
                break;
            case ClientFence:
                if (held < 9) return true;
                length = 9 + header[8];
                acts = false;
                break;
            case SetDesktopSize:
                if (held < 8) return true;
                length = 8 + 16L * header[6];
                acts = false;
                break;
            case Qemu:
                if (held < 2) return true;
                if (header[1] == 0)
                {
                    // QEMU's extended key event.
                    length = 12;
                    acts = true;
                    hand = true;
                    break;
                }
                if (header[1] == 1)
                {
                    // QEMU audio: enable and disable are four bytes, setting the format ten.
                    if (held < 4) return true;
                    length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)) == 2 ? 10 : 4;
                    acts = false;
                    break;
                }
                return Lost(output);
            default:
                return Lost(output);
        }
        var keep = !acts || drive;
        if (keep && hand) Volatile.Write(ref input, 1);
        var consumed = held;
        if (keep) Pass(output);
        else held = 0;
        remaining = length - consumed;
        passing = keep;
        return true;
    }

    /// <summary>Write what is held and start again.</summary>
    void Pass(IBufferWriter<byte> output)
    {
        output.Write(header.AsSpan(0, held));
        held = 0;
    }

    /// <summary>A message this cannot read: from here the stream is opaque. A watcher's is closed.</summary>
    bool Lost(IBufferWriter<byte> output)
    {
        stage = Stage.Opaque;
        if (!drive) return false;
        Pass(output);
        return true;
    }
}
