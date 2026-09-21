using System;
using System.IO;
using System.Linq;
using System.Text;

namespace WinputLan.Core
{
    public enum FrameType : byte
    {
        Hello = 1,
        PairingOffer = 2,
        PairingConfirm = 3,
        Input = 4,
        Heartbeat = 5,
        HeartbeatAck = 6,
        Goodbye = 7,
        Error = 8,
        ReleaseAll = 9
    }

    public enum InputKind : byte
    {
        MouseMove = 1,
        MouseButtonDown = 2,
        MouseButtonUp = 3,
        MouseWheel = 4,
        KeyDown = 5,
        KeyUp = 6
    }

    public sealed class Frame
    {
        public Frame(FrameType type, ulong sequence, byte[] payload)
        {
            Type = type;
            Sequence = sequence;
            Payload = payload ?? new byte[0];
        }

        public FrameType Type { get; private set; }
        public ulong Sequence { get; private set; }
        public byte[] Payload { get; private set; }
    }

    public sealed class InputEvent
    {
        public InputKind Kind { get; set; }
        public uint Flags { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public ushort MouseData { get; set; }
        public ushort VirtualKey { get; set; }
        public ushort ScanCode { get; set; }
        public long TimestampUtcTicks { get; set; }

        public static InputEvent MouseMove(int x, int y, long timestampUtcTicks)
        {
            return new InputEvent { Kind = InputKind.MouseMove, X = x, Y = y, TimestampUtcTicks = timestampUtcTicks };
        }

        public static InputEvent Key(InputKind kind, ushort virtualKey, ushort scanCode, uint flags, long timestampUtcTicks)
        {
            if (kind != InputKind.KeyDown && kind != InputKind.KeyUp) throw new ArgumentException("Key event must be down or up.", "kind");
            return new InputEvent { Kind = kind, VirtualKey = virtualKey, ScanCode = scanCode, Flags = flags, TimestampUtcTicks = timestampUtcTicks };
        }

        public static InputEvent MouseButton(InputKind kind, uint flags, long timestampUtcTicks)
        {
            if (kind != InputKind.MouseButtonDown && kind != InputKind.MouseButtonUp && kind != InputKind.MouseWheel) throw new ArgumentException("Invalid mouse button event.", "kind");
            return new InputEvent { Kind = kind, Flags = flags, TimestampUtcTicks = timestampUtcTicks };
        }

        public static InputEvent MouseWheel(short delta, long timestampUtcTicks)
        {
            return new InputEvent { Kind = InputKind.MouseWheel, MouseData = unchecked((ushort)delta), TimestampUtcTicks = timestampUtcTicks };
        }
    }

    public static class PointerCoordinates
    {
        public static int Normalize(int coordinate, int origin, int size)
        {
            if (size <= 1) return 0;
            return (int)Math.Max(0, Math.Min(65535, (coordinate - origin) * 65535L / (size - 1)));
        }

        public static int ClampNormalized(int value) { return Math.Max(0, Math.Min(65535, value)); }
    }

    public static class ProtocolConstants
    {
        public const ushort Magic = 0x4C57; // bytes "WL" in little-endian order
        public const byte CurrentVersion = 1;
        public const int HeaderSize = 16;
        public const int MaxPayloadBytes = 64 * 1024;
        public const int InputPayloadBytes = 32;
    }

    public static class FrameCodec
    {
        public static byte[] Encode(FrameType type, ulong sequence, byte[] payload)
        {
            payload = payload ?? new byte[0];
            if (payload.Length > ProtocolConstants.MaxPayloadBytes) throw new InvalidDataException("Frame payload is too large.");
            using (var stream = new MemoryStream(ProtocolConstants.HeaderSize + payload.Length))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(ProtocolConstants.Magic);
                writer.Write(ProtocolConstants.CurrentVersion);
                writer.Write((byte)type);
                writer.Write(payload.Length);
                writer.Write(sequence);
                writer.Write(payload);
                return stream.ToArray();
            }
        }

        public static Frame Decode(byte[] encoded)
        {
            if (encoded == null || encoded.Length < ProtocolConstants.HeaderSize) throw new InvalidDataException("Frame is incomplete.");
            using (var stream = new MemoryStream(encoded, false)) return Read(stream);
        }

        public static Frame Read(Stream stream)
        {
            if (stream == null || !stream.CanRead) throw new ArgumentException("Readable stream required.", "stream");
            var header = ReadExactly(stream, ProtocolConstants.HeaderSize);
            using (var headerStream = new MemoryStream(header, false))
            using (var reader = new BinaryReader(headerStream, Encoding.UTF8, true))
            {
                var magic = reader.ReadUInt16();
                var version = reader.ReadByte();
                var typeValue = reader.ReadByte();
                var length = reader.ReadInt32();
                var sequence = reader.ReadUInt64();
                if (magic != ProtocolConstants.Magic) throw new InvalidDataException("Frame magic mismatch.");
                if (version != ProtocolConstants.CurrentVersion) throw new InvalidDataException("Unsupported protocol version.");
                if (length < 0 || length > ProtocolConstants.MaxPayloadBytes) throw new InvalidDataException("Frame payload length is invalid.");
                if (!Enum.IsDefined(typeof(FrameType), typeValue)) throw new InvalidDataException("Unknown frame type.");
                return new Frame((FrameType)typeValue, sequence, ReadExactly(stream, length));
            }
        }

        public static byte[] EncodeInput(InputEvent value)
        {
            if (value == null) throw new ArgumentNullException("value");
            using (var stream = new MemoryStream(ProtocolConstants.InputPayloadBytes))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write((byte)value.Kind);
                writer.Write((byte)0);
                writer.Write(value.Flags);
                writer.Write(value.X);
                writer.Write(value.Y);
                writer.Write(value.MouseData);
                writer.Write(value.VirtualKey);
                writer.Write(value.ScanCode);
                writer.Write(value.TimestampUtcTicks);
                writer.Write(0U); // reserved for future input metadata; keeps the wire record fixed at 32 bytes
                return stream.ToArray();
            }
        }

        public static InputEvent DecodeInput(byte[] payload)
        {
            if (payload == null || payload.Length != ProtocolConstants.InputPayloadBytes) throw new InvalidDataException("Input payload length is invalid.");
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var kind = (InputKind)reader.ReadByte();
                reader.ReadByte();
                if (!Enum.IsDefined(typeof(InputKind), kind)) throw new InvalidDataException("Unknown input kind.");
                var result = new InputEvent
                {
                    Kind = kind,
                    Flags = reader.ReadUInt32(),
                    X = reader.ReadInt32(),
                    Y = reader.ReadInt32(),
                    MouseData = reader.ReadUInt16(),
                    VirtualKey = reader.ReadUInt16(),
                    ScanCode = reader.ReadUInt16(),
                    TimestampUtcTicks = reader.ReadInt64()
                };
                reader.ReadUInt32();
                return result;
            }
        }

        public static byte[] EncodeText(string value, int maxBytes = 2048)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > maxBytes) throw new InvalidDataException("Text field is too large.");
            return bytes;
        }

        public static string DecodeText(byte[] value, int maxBytes = 2048)
        {
            if (value == null || value.Length > maxBytes) throw new InvalidDataException("Text field is too large.");
            return Encoding.UTF8.GetString(value);
        }

        private static byte[] ReadExactly(Stream stream, int length)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read == 0) throw new EndOfStreamException("Frame ended before all bytes arrived.");
                offset += read;
            }
            return buffer;
        }
    }
}
