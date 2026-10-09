using System.Buffers.Binary;
using System.Numerics;

namespace DeadGround.Engine.Net;

public static class WireProtocol
{
    public const byte Version = 1;
    public const int MoveSize = 22;
    public const int CommandResultSize = 11;
    public const int SnapshotHeaderSize = 12;
    public const int UnitSize = 18;
    public const int MaxUnits = 1024;
    public const int MaxMessageSize = SnapshotHeaderSize + MaxUnits * UnitSize;
    private const byte MoveKind = 1, SnapshotKind = 2, CommandResultKind = 3;

    public static void WriteCommandResult(Span<byte> bytes, CommandResult result)
    {
        if (bytes.Length < CommandResultSize) throw new ArgumentException("Command result buffer is too small.");
        bytes[0] = Version; bytes[1] = CommandResultKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], result.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], result.ServerTick);
        bytes[10] = result.Queued ? (byte)1 : (byte)0;
    }

    public static bool TryReadCommandResult(ReadOnlySpan<byte> bytes, out CommandResult result)
    {
        result = default;
        if (bytes.Length != CommandResultSize || bytes[0] != Version || bytes[1] != CommandResultKind || bytes[10] > 1)
            return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        if (sequence == 0 || tick < 0) return false;
        result = new CommandResult(sequence, tick, bytes[10] == 1);
        return true;
    }

    public static void WriteMove(Span<byte> bytes, MoveCommand command)
    {
        if (bytes.Length < MoveSize) throw new ArgumentException("Command buffer is too small.");
        bytes[0] = Version; bytes[1] = MoveKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], command.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], command.ClientTick);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[10..], command.Unit.Value);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[14..], command.Target.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[18..], command.Target.Y);
    }

    public static bool TryReadMove(ReadOnlySpan<byte> bytes, out MoveCommand command)
    {
        command = default;
        if (bytes.Length != MoveSize || bytes[0] != Version || bytes[1] != MoveKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        Vector2 target = new(BinaryPrimitives.ReadSingleLittleEndian(bytes[14..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[18..]));
        if (sequence == 0 || id == 0 || tick < 0 || !float.IsFinite(target.X) || !float.IsFinite(target.Y)) return false;
        command = new MoveCommand(sequence, tick, new EntityId(id), target);
        return true;
    }

    public static int WriteSnapshot(Span<byte> bytes, uint sequence, int tick, ReadOnlySpan<OwnUnitObservation> units)
    {
        int length = checked(SnapshotHeaderSize + units.Length * UnitSize);
        if (units.Length > MaxUnits || bytes.Length < length) throw new ArgumentException("Snapshot buffer is too small.");
        bytes[0] = Version; bytes[1] = SnapshotKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], tick);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], (ushort)units.Length);
        for (int i = 0; i < units.Length; i++)
        {
            OwnUnitObservation unit = units[i];
            Span<byte> record = bytes.Slice(SnapshotHeaderSize + i * UnitSize, UnitSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, unit.Id.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], unit.Definition);
            BinaryPrimitives.WriteSingleLittleEndian(record[6..], unit.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[10..], unit.Position.Y);
            BinaryPrimitives.WriteUInt32LittleEndian(record[14..], unit.SupportSurface);
        }
        return length;
    }

    internal static bool TryReadSnapshot(ReadOnlySpan<byte> bytes, SnapshotFrame frame)
    {
        if (bytes.Length < SnapshotHeaderSize || bytes[0] != Version || bytes[1] != SnapshotKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        if (sequence == 0 || tick < 0 || count > frame.Units.Length ||
            bytes.Length != SnapshotHeaderSize + count * UnitSize) return false;
        frame.Indices.Clear();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(SnapshotHeaderSize + i * UnitSize, UnitSize);
            EntityId id = new(BinaryPrimitives.ReadUInt32LittleEndian(record));
            ushort definition = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            Vector2 position = new(BinaryPrimitives.ReadSingleLittleEndian(record[6..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[10..]));
            uint surface = BinaryPrimitives.ReadUInt32LittleEndian(record[14..]);
            if (id.Value == 0 || !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !frame.Indices.TryAdd(id, i)) return false;
            frame.Units[i] = new OwnUnitObservation(id, definition, position, surface);
        }
        frame.Sequence = sequence; frame.Tick = tick; frame.Count = count;
        return true;
    }
}

internal sealed class SnapshotFrame
{
    public readonly OwnUnitObservation[] Units;
    public readonly Dictionary<EntityId, int> Indices;
    public int Count, Tick;
    public uint Sequence;
    public SnapshotFrame(int capacity)
    {
        Units = new OwnUnitObservation[capacity];
        Indices = new Dictionary<EntityId, int>(capacity);
    }
}
