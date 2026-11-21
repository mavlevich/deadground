using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using DeadGround.Engine;

namespace DeadGround.Engine.Net;

public enum HandshakeReason : byte
{
    Accepted = 0,
    ProtocolVersionMismatch = 1,
    ContentMismatch = 2,
    MapMismatch = 3,
    RuleSetMismatch = 4,
    ServerFull = 5,
    Timeout = 6
}

public static class WireProtocol
{
    public const byte Version = MatchSetup.CurrentProtocolVersion;
    public const int MoveSize = 32;
    public const int AttackSize = 18;
    public const int SetFireStanceSize = 15;
    public const int CommandResultSize = 11;
    public const int MissionSize = 32;
    public const int SnapshotHeaderSize = 20 + MissionSize;
    public const int UnitSize = 69;
    public const int MaxUnits = 1024;
    public const int ContactSize = 35;
    public const int MaxContacts = 256;
    public const int WreckSize = 22;
    public const int MaxWrecks = 128;
    public const int SmokeSize = 40;
    public const int MaxSmokeClouds = WorldState.MaxSmokeClouds;
    public const int EventSize = 40;
    public const int MaxEvents = 64;
    public const int MaxMessageSize = SnapshotHeaderSize + MaxUnits * UnitSize + MaxContacts * ContactSize + MaxWrecks * WreckSize + MaxSmokeClouds * SmokeSize + MaxEvents * EventSize;
    public const int HandshakeResponseSize = 5;
    public const int FragmentHeaderSize = 22;
    public const int MaxFragmentPayloadSize = 960;
    public const int GroupMoveHeaderSize = 30;
    public const int GroupMoveResultHeaderSize = 27;
    public const int GroupMovePlacedRecordSize = 21;
    public const int GroupMoveUnplacedRecordSize = 5;
    public const int BoardTransportSize = 18;
    public const int DisembarkSize = 26;
    public const int DisembarkResultSize = 17;
    public const int DeploySmokeSize = 27;
    public const int HelicopterSize = 29;
    public const int FireMissionSize = 23;

    public const byte MoveKind = 1;
    public const byte SnapshotKind = 2;
    public const byte CommandResultKind = 3;
    public const byte SnapshotFragmentKind = 4;
    public const byte HandshakeRequestKind = 5;
    public const byte HandshakeResponseKind = 6;
    public const byte AttackKind = 7;
    public const byte SetFireStanceKind = 8;
    public const byte GroupMoveKind = 9;
    public const byte GroupMoveResultKind = 10;
    public const byte BoardTransportKind = 11;
    public const byte DisembarkKind = 12;
    public const byte DisembarkResultKind = 13;
    public const byte DeploySmokeKind = 14;
    public const byte HelicopterKind = 15;
    public const byte FireMissionKind = 16;

    // Yaw in 65536 steps over a full turn: code 32768 is exactly 0, and the quarter turns are exact too.
    // +pi and -pi share code 0.
    public static ushort QuantizeYaw(float yaw)
    {
        if (!float.IsFinite(yaw)) return 32768;
        float wrapped = MathF.IEEERemainder(yaw, MathF.Tau);
        int code = (int)MathF.Round((wrapped + MathF.PI) / MathF.Tau * 65536f);
        return (ushort)(code & 0xFFFF);
    }

    public static float DequantizeYaw(ushort quantized)
    {
        float yaw = quantized / 65536f * MathF.Tau - MathF.PI;
        return yaw <= -MathF.PI ? yaw + MathF.Tau : yaw;
    }

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
        if (command.FinalFacing.HasValue)
        {
            bytes[22] = 1;
            BinaryPrimitives.WriteSingleLittleEndian(bytes[23..], command.FinalFacing.Value);
        }
        else
        {
            bytes[22] = 0;
            BinaryPrimitives.WriteSingleLittleEndian(bytes[23..], 0f);
        }
        bytes[27] = (byte)command.Mode;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[28..], command.TargetSurface);
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
        byte hasFacing = bytes[22];
        if (hasFacing > 1) return false;
        float? finalFacing = null;
        if (hasFacing == 1)
        {
            float facing = BinaryPrimitives.ReadSingleLittleEndian(bytes[23..]);
            if (!float.IsFinite(facing)) return false;
            finalFacing = facing;
        }
        byte modeByte = bytes[27];
        if (modeByte > 1) return false;
        MoveMode mode = (MoveMode)modeByte;
        uint targetSurface = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        if (sequence == 0 || id == 0 || tick < 0 || !float.IsFinite(target.X) || !float.IsFinite(target.Y)) return false;
        command = new MoveCommand(sequence, tick, new EntityId(id), target, finalFacing, mode, targetSurface);
        return true;
    }

    public static int WriteGroupMove(Span<byte> bytes, GroupMoveCommand command)
    {
        int unitCount = command.Units.Length;
        if (unitCount <= 0 || unitCount > MaxUnits)
            throw new ArgumentException("Group move unit count must be between 1 and MaxUnits.");
        int length = GroupMoveHeaderSize + unitCount * 4;
        if (bytes.Length < length) throw new ArgumentException("Group move buffer is too small.");
        bytes[0] = Version;
        bytes[1] = GroupMoveKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], command.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], command.ClientTick);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[10..], command.Target.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[14..], command.Target.Y);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[18..], command.TargetSurface);
        if (command.FinalFacing.HasValue)
        {
            bytes[22] = 1;
            BinaryPrimitives.WriteSingleLittleEndian(bytes[23..], command.FinalFacing.Value);
        }
        else
        {
            bytes[22] = 0;
            BinaryPrimitives.WriteSingleLittleEndian(bytes[23..], 0f);
        }
        bytes[27] = (byte)command.Mode;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[28..], (ushort)unitCount);

        ReadOnlySpan<EntityId> units = command.Units.Span;
        for (int i = 0; i < unitCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(GroupMoveHeaderSize + i * 4, 4), units[i].Value);
        }
        return length;
    }

    public static bool TryReadGroupMove(ReadOnlySpan<byte> bytes, out GroupMoveCommand command)
    {
        command = default;
        if (bytes.Length < GroupMoveHeaderSize || bytes[0] != Version || bytes[1] != GroupMoveKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        Vector2 target = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[10..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[14..]));
        uint targetSurface = BinaryPrimitives.ReadUInt32LittleEndian(bytes[18..]);
        byte hasFacing = bytes[22];
        if (hasFacing > 1) return false;
        float? finalFacing = null;
        if (hasFacing == 1)
        {
            float facing = BinaryPrimitives.ReadSingleLittleEndian(bytes[23..]);
            if (!float.IsFinite(facing)) return false;
            finalFacing = facing;
        }
        byte modeByte = bytes[27];
        if (modeByte > 1) return false;
        MoveMode mode = (MoveMode)modeByte;
        ushort unitCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]);
        if (unitCount == 0 || unitCount > MaxUnits) return false;
        int expectedLength = GroupMoveHeaderSize + unitCount * 4;
        if (bytes.Length != expectedLength) return false;
        if (sequence == 0 || tick < 0 || !float.IsFinite(target.X) || !float.IsFinite(target.Y)) return false;

        EntityId[] unitIds = new EntityId[unitCount];
        for (int i = 0; i < unitCount; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(GroupMoveHeaderSize + i * 4, 4));
            if (id == 0) return false;
            unitIds[i] = new EntityId(id);
        }
        command = new GroupMoveCommand(sequence, tick, unitIds, target, targetSurface, finalFacing, mode);
        return true;
    }

    public static int WriteGroupMoveResult(Span<byte> bytes, GroupMoveResult result)
    {
        int placedCount = result.Placed.Length;
        int unplacedCount = result.Unplaced.Length;
        if (placedCount > MaxUnits || unplacedCount > MaxUnits)
            throw new ArgumentException("Group move result unit count exceeds capacity.");
        int length = GroupMoveResultHeaderSize + placedCount * GroupMovePlacedRecordSize + unplacedCount * GroupMoveUnplacedRecordSize;
        if (bytes.Length < length) throw new ArgumentException("Group move result buffer is too small.");
        bytes[0] = Version;
        bytes[1] = GroupMoveResultKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], result.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], result.ServerTick);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[10..], result.Target.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[14..], result.Target.Y);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[18..], result.TargetSurface);
        bytes[22] = (byte)result.Mode;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[23..], (ushort)placedCount);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[25..], (ushort)unplacedCount);

        int offset = GroupMoveResultHeaderSize;
        ReadOnlySpan<GroupMoveAssignment> placed = result.Placed.Span;
        for (int i = 0; i < placedCount; i++)
        {
            GroupMoveAssignment a = placed[i];
            Span<byte> record = bytes.Slice(offset, GroupMovePlacedRecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record[0..], a.Unit.Value);
            BinaryPrimitives.WriteSingleLittleEndian(record[4..], a.Target.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[8..], a.Target.Y);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], a.TargetSurface);
            if (a.FinalFacing.HasValue)
            {
                record[16] = 1;
                BinaryPrimitives.WriteSingleLittleEndian(record[17..], a.FinalFacing.Value);
            }
            else
            {
                record[16] = 0;
                BinaryPrimitives.WriteSingleLittleEndian(record[17..], 0f);
            }
            offset += GroupMovePlacedRecordSize;
        }

        ReadOnlySpan<GroupMoveUnplaced> unplaced = result.Unplaced.Span;
        for (int i = 0; i < unplacedCount; i++)
        {
            GroupMoveUnplaced u = unplaced[i];
            Span<byte> record = bytes.Slice(offset, GroupMoveUnplacedRecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record[0..], u.Unit.Value);
            record[4] = (byte)u.Reason;
            offset += GroupMoveUnplacedRecordSize;
        }

        return length;
    }

    public static bool TryReadGroupMoveResult(ReadOnlySpan<byte> bytes, out GroupMoveResult result)
    {
        result = default;
        if (bytes.Length < GroupMoveResultHeaderSize || bytes[0] != Version || bytes[1] != GroupMoveResultKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        Vector2 target = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[10..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[14..]));
        uint targetSurface = BinaryPrimitives.ReadUInt32LittleEndian(bytes[18..]);
        byte modeByte = bytes[22];
        if (modeByte > 1) return false;
        MoveMode mode = (MoveMode)modeByte;
        ushort placedCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[23..]);
        ushort unplacedCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[25..]);
        if (placedCount > MaxUnits || unplacedCount > MaxUnits) return false;
        int expectedLength = GroupMoveResultHeaderSize + placedCount * GroupMovePlacedRecordSize + unplacedCount * GroupMoveUnplacedRecordSize;
        if (bytes.Length != expectedLength) return false;
        if (sequence == 0 || tick < 0 || !float.IsFinite(target.X) || !float.IsFinite(target.Y)) return false;

        GroupMoveAssignment[] placed = new GroupMoveAssignment[placedCount];
        int offset = GroupMoveResultHeaderSize;
        for (int i = 0; i < placedCount; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(offset, GroupMovePlacedRecordSize);
            uint unitId = BinaryPrimitives.ReadUInt32LittleEndian(record[0..]);
            Vector2 unitTarget = new(
                BinaryPrimitives.ReadSingleLittleEndian(record[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[8..]));
            uint surface = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            byte hasFacing = record[16];
            if (hasFacing > 1) return false;
            float? facing = null;
            if (hasFacing == 1)
            {
                float f = BinaryPrimitives.ReadSingleLittleEndian(record[17..]);
                if (!float.IsFinite(f)) return false;
                facing = f;
            }
            if (unitId == 0 || !float.IsFinite(unitTarget.X) || !float.IsFinite(unitTarget.Y)) return false;
            placed[i] = new GroupMoveAssignment(new EntityId(unitId), unitTarget, surface, facing);
            offset += GroupMovePlacedRecordSize;
        }

        GroupMoveUnplaced[] unplaced = new GroupMoveUnplaced[unplacedCount];
        for (int i = 0; i < unplacedCount; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(offset, GroupMoveUnplacedRecordSize);
            uint unitId = BinaryPrimitives.ReadUInt32LittleEndian(record[0..]);
            byte reasonByte = record[4];
            if (reasonByte > (byte)DestinationBlockReason.NoReverseGear) return false;
            if (unitId == 0) return false;
            unplaced[i] = new GroupMoveUnplaced(new EntityId(unitId), (DestinationBlockReason)reasonByte);
            offset += GroupMoveUnplacedRecordSize;
        }

        result = new GroupMoveResult(sequence, tick, target, targetSurface, mode, placed, unplaced);
        return true;
    }

    public static void WriteAttack(Span<byte> bytes, AttackCommand command)
    {
        if (bytes.Length < AttackSize) throw new ArgumentException("Attack buffer is too small.");
        bytes[0] = Version; bytes[1] = AttackKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], command.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], command.ClientTick);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[10..], command.Unit.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[14..], command.Target.Value);
    }

    public static bool TryReadAttack(ReadOnlySpan<byte> bytes, out AttackCommand command)
    {
        command = default;
        if (bytes.Length != AttackSize || bytes[0] != Version || bytes[1] != AttackKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        uint unitId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        uint targetId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        if (sequence == 0 || unitId == 0 || targetId == 0 || tick < 0) return false;
        command = new AttackCommand(sequence, tick, new EntityId(unitId), new ContactId(targetId));
        return true;
    }

    public static void WriteSetFireStance(Span<byte> bytes, SetFireStanceCommand command)
    {
        if (bytes.Length < SetFireStanceSize) throw new ArgumentException("SetFireStance buffer is too small.");
        bytes[0] = Version; bytes[1] = SetFireStanceKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], command.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], command.ClientTick);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[10..], command.Unit.Value);
        bytes[14] = (byte)command.Stance;
    }

    public static bool TryReadSetFireStance(ReadOnlySpan<byte> bytes, out SetFireStanceCommand command)
    {
        command = default;
        if (bytes.Length != SetFireStanceSize || bytes[0] != Version || bytes[1] != SetFireStanceKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        uint unitId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        byte stanceByte = bytes[14];
        if (sequence == 0 || unitId == 0 || tick < 0 || stanceByte > (byte)FireStance.HoldFire) return false;
        command = new SetFireStanceCommand(sequence, tick, new EntityId(unitId), (FireStance)stanceByte);
        return true;
    }

    public static void WriteBoardTransport(Span<byte> bytes, BoardTransportCommand command)
    {
        if (bytes.Length < BoardTransportSize) throw new ArgumentException("Buffer is too small.");
        bytes[0] = Version; bytes[1] = BoardTransportKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], command.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], command.ClientTick);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[10..], command.Unit.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[14..], command.Transport.Value);
    }

    public static bool TryReadBoardTransport(ReadOnlySpan<byte> bytes, out BoardTransportCommand command)
    {
        command = default;
        if (bytes.Length != BoardTransportSize || bytes[0] != Version || bytes[1] != BoardTransportKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        uint unitId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        uint transportId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        if (sequence == 0 || unitId == 0 || transportId == 0 || tick < 0) return false;
        command = new BoardTransportCommand(sequence, tick, new EntityId(unitId), new EntityId(transportId));
        return true;
    }

    public static void WriteDisembark(Span<byte> bytes, DisembarkCommand command)
    {
        if (bytes.Length < DisembarkSize) throw new ArgumentException("Buffer is too small.");
        bytes[0] = Version; bytes[1] = DisembarkKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], command.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], command.ClientTick);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[10..], command.Transport.Value);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[14..], command.Target.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[18..], command.Target.Y);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[22..], command.TargetSurface);
    }

    public static bool TryReadDisembark(ReadOnlySpan<byte> bytes, out DisembarkCommand command)
    {
        command = default;
        if (bytes.Length != DisembarkSize || bytes[0] != Version || bytes[1] != DisembarkKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        uint transportId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        float targetX = BinaryPrimitives.ReadSingleLittleEndian(bytes[14..]);
        float targetY = BinaryPrimitives.ReadSingleLittleEndian(bytes[18..]);
        uint targetSurface = BinaryPrimitives.ReadUInt32LittleEndian(bytes[22..]);
        if (sequence == 0 || transportId == 0 || tick < 0 || !float.IsFinite(targetX) || !float.IsFinite(targetY)) return false;
        command = new DisembarkCommand(sequence, tick, new EntityId(transportId), new Vector2(targetX, targetY), targetSurface);
        return true;
    }

    public static int WriteDisembarkResult(Span<byte> bytes, DisembarkResult result)
    {
        if (bytes.Length < DisembarkResultSize) throw new ArgumentException("Buffer is too small.");
        bytes[0] = Version; bytes[1] = DisembarkResultKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], result.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], result.ServerTick);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[10..], result.Transport.Value);
        bytes[14] = result.DisembarkedCount;
        bytes[15] = result.RemainingCount;
        bytes[16] = result.Success ? (byte)1 : (byte)0;
        return DisembarkResultSize;
    }

    public static bool TryReadDisembarkResult(ReadOnlySpan<byte> bytes, out DisembarkResult result)
    {
        result = default;
        if (bytes.Length != DisembarkResultSize || bytes[0] != Version || bytes[1] != DisembarkResultKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        uint transportId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        byte disembarked = bytes[14];
        byte remaining = bytes[15];
        bool success = bytes[16] == 1;
        if (sequence == 0 || tick < 0 || transportId == 0) return false;
        result = new DisembarkResult(sequence, tick, new EntityId(transportId), disembarked, remaining, success);
        return true;
    }

    public static int WriteHandshakeRequest(Span<byte> bytes, MatchSetup setup)
    {
        int stringByteCount = Encoding.UTF8.GetByteCount(setup.RuleSetId);
        int totalSize = 20 + stringByteCount;
        if (bytes.Length < totalSize) throw new ArgumentException("Handshake buffer is too small.");
        bytes[0] = Version;
        bytes[1] = HandshakeRequestKind;
        bytes[2] = setup.ProtocolVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[3..], setup.ContentHash);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[11..], setup.MapHash);
        bytes[19] = (byte)stringByteCount;
        Encoding.UTF8.GetBytes(setup.RuleSetId, bytes[20..]);
        return totalSize;
    }

    public static bool TryReadHandshakeRequest(ReadOnlySpan<byte> bytes, out MatchSetup setup)
    {
        setup = default!;
        if (bytes.Length < 20 || bytes[0] != Version || bytes[1] != HandshakeRequestKind) return false;
        byte protocolVersion = bytes[2];
        ulong contentHash = BinaryPrimitives.ReadUInt64LittleEndian(bytes[3..]);
        ulong mapHash = BinaryPrimitives.ReadUInt64LittleEndian(bytes[11..]);
        byte strLen = bytes[19];
        if (bytes.Length != 20 + strLen) return false;
        string ruleSetId = Encoding.UTF8.GetString(bytes.Slice(20, strLen));
        setup = new MatchSetup(protocolVersion, contentHash, mapHash, ruleSetId);
        return true;
    }

    public static void WriteHandshakeResponse(Span<byte> bytes, bool accepted, int assignedPlayer, HandshakeReason reason)
    {
        if (bytes.Length < HandshakeResponseSize) throw new ArgumentException("Handshake response buffer too small.");
        bytes[0] = Version;
        bytes[1] = HandshakeResponseKind;
        bytes[2] = accepted ? (byte)1 : (byte)0;
        bytes[3] = (byte)assignedPlayer;
        bytes[4] = (byte)reason;
    }

    public static bool TryReadHandshakeResponse(ReadOnlySpan<byte> bytes, out bool accepted, out int assignedPlayer, out HandshakeReason reason)
    {
        accepted = false;
        assignedPlayer = 0;
        reason = default;
        if (bytes.Length != HandshakeResponseSize || bytes[0] != Version || bytes[1] != HandshakeResponseKind || bytes[2] > 1)
            return false;
        accepted = bytes[2] == 1;
        assignedPlayer = bytes[3];
        reason = (HandshakeReason)bytes[4];
        return true;
    }

    public static void WriteDeploySmoke(Span<byte> bytes, DeploySmokeCommand command)
    {
        if (bytes.Length < DeploySmokeSize) throw new ArgumentException("Deploy smoke buffer is too small.");
        bytes[0] = Version;
        bytes[1] = DeploySmokeKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], command.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], command.ClientTick);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[10..], command.Unit.Value);
        bytes[14] = command.HasTarget ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(bytes[15..], command.HasTarget ? command.Target.X : 0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[19..], command.HasTarget ? command.Target.Y : 0f);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[23..], command.HasTarget ? command.TargetSurface : 0u);
    }

    public static bool TryReadDeploySmoke(ReadOnlySpan<byte> bytes, out DeploySmokeCommand command)
    {
        command = default;
        if (bytes.Length != DeploySmokeSize || bytes[0] != Version || bytes[1] != DeploySmokeKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        uint unitId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        byte hasTargetByte = bytes[14];
        if (sequence == 0 || tick < 0 || unitId == 0 || hasTargetByte > 1) return false;
        bool hasTarget = hasTargetByte == 1;
        Vector2 target = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[15..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[19..]));
        uint targetSurface = BinaryPrimitives.ReadUInt32LittleEndian(bytes[23..]);
        if (hasTarget && (!float.IsFinite(target.X) || !float.IsFinite(target.Y))) return false;
        command = new DeploySmokeCommand(sequence, tick, new EntityId(unitId), hasTarget, hasTarget ? target : default, hasTarget ? targetSurface : 0u);
        return true;
    }

    public static void WriteHelicopter(Span<byte> bytes, HelicopterCommand command)
    {
        if (bytes.Length < HelicopterSize) throw new ArgumentException("Helicopter command buffer is too small.");
        bytes[0] = Version;
        bytes[1] = HelicopterKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], command.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], command.ClientTick);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[10..], command.Unit.Value);
        bytes[14] = (byte)command.Kind;
        bytes[15] = (byte)command.AltitudeBand;
        bytes[16] = command.HasTarget ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(bytes[17..], command.HasTarget ? command.Target.X : 0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[21..], command.HasTarget ? command.Target.Y : 0f);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[25..], command.TargetUnit.Value);
    }

    public static bool TryReadHelicopter(ReadOnlySpan<byte> bytes, out HelicopterCommand command)
    {
        command = default;
        if (bytes.Length != HelicopterSize || bytes[0] != Version || bytes[1] != HelicopterKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        uint unitId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        byte kindByte = bytes[14];
        byte bandByte = bytes[15];
        byte hasTargetByte = bytes[16];
        if (sequence == 0 || tick < 0 || unitId == 0 ||
            kindByte > (byte)HelicopterOrderKind.SlingDrop ||
            bandByte > (byte)AltitudeBand.High ||
            hasTargetByte > 1)
        {
            return false;
        }

        bool hasTarget = hasTargetByte == 1;
        Vector2 target = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[17..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[21..]));
        uint targetUnitId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[25..]);
        if (hasTarget && (!float.IsFinite(target.X) || !float.IsFinite(target.Y))) return false;
        command = new HelicopterCommand(
            sequence,
            tick,
            new EntityId(unitId),
            (HelicopterOrderKind)kindByte,
            (AltitudeBand)bandByte,
            hasTarget,
            hasTarget ? target : default,
            new EntityId(targetUnitId));
        return true;
    }

    public static void WriteFireMission(Span<byte> bytes, FireMissionCommand command)
    {
        if (bytes.Length < FireMissionSize) throw new ArgumentException("Fire mission buffer is too small.");
        bytes[0] = Version;
        bytes[1] = FireMissionKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], command.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], command.ClientTick);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[10..], command.Unit.Value);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[14..], command.Cancel ? 0f : command.Target.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[18..], command.Cancel ? 0f : command.Target.Y);
        bytes[22] = command.Cancel ? (byte)1 : (byte)0;
    }

    public static bool TryReadFireMission(ReadOnlySpan<byte> bytes, out FireMissionCommand command)
    {
        command = default;
        if (bytes.Length != FireMissionSize || bytes[0] != Version || bytes[1] != FireMissionKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        uint unitId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        float targetX = BinaryPrimitives.ReadSingleLittleEndian(bytes[14..]);
        float targetY = BinaryPrimitives.ReadSingleLittleEndian(bytes[18..]);
        byte cancelByte = bytes[22];
        if (sequence == 0 || tick < 0 || unitId == 0 || cancelByte > 1) return false;
        bool cancel = cancelByte == 1;
        if (!cancel && (!float.IsFinite(targetX) || !float.IsFinite(targetY))) return false;
        command = new FireMissionCommand(sequence, tick, new EntityId(unitId), cancel ? default : new Vector2(targetX, targetY), cancel);
        return true;
    }

    public static int WriteSnapshot(
        Span<byte> bytes,
        uint sequence,
        int tick,
        ReadOnlySpan<OwnUnitObservation> units,
        ReadOnlySpan<ContactObservation> contacts = default,
        ReadOnlySpan<CombatEventObservation> events = default,
        MissionObservation mission = default,
        ReadOnlySpan<WreckObservation> wrecks = default,
        ReadOnlySpan<SmokeCloudObservation> smokeClouds = default)
    {
        int length = checked(SnapshotHeaderSize + units.Length * UnitSize + contacts.Length * ContactSize + wrecks.Length * WreckSize + smokeClouds.Length * SmokeSize + events.Length * EventSize);
        if (units.Length > MaxUnits || contacts.Length > MaxContacts || wrecks.Length > MaxWrecks || smokeClouds.Length > MaxSmokeClouds || events.Length > MaxEvents || bytes.Length < length)
            throw new ArgumentException("Snapshot buffer is too small or capacity exceeded.");

        bytes[0] = Version; bytes[1] = SnapshotKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], tick);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], (ushort)units.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[12..], (ushort)contacts.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[14..], (ushort)events.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[16..], (ushort)wrecks.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[18..], (ushort)smokeClouds.Length);

        Span<byte> missionRecord = bytes.Slice(20, MissionSize);
        BinaryPrimitives.WriteUInt16LittleEndian(missionRecord, mission.MissionId);
        missionRecord[2] = (byte)mission.Status;
        missionRecord[3] = (byte)mission.EndReason;
        BinaryPrimitives.WriteInt32LittleEndian(missionRecord[4..], mission.TimeLimitTicks);
        BinaryPrimitives.WriteInt32LittleEndian(missionRecord[8..], mission.FinishedTick);
        BinaryPrimitives.WriteUInt32LittleEndian(missionRecord[12..], mission.TargetContactId.Value);
        BinaryPrimitives.WriteSingleLittleEndian(missionRecord[16..], mission.SearchAreaCenter.X);
        BinaryPrimitives.WriteSingleLittleEndian(missionRecord[20..], mission.SearchAreaCenter.Y);
        BinaryPrimitives.WriteSingleLittleEndian(missionRecord[24..], mission.SearchAreaRadius);
        missionRecord[28] = mission.Flags;
        missionRecord[29] = 0;
        missionRecord[30] = 0;
        missionRecord[31] = 0;

        for (int i = 0; i < units.Length; i++)
        {
            OwnUnitObservation unit = units[i];
            Span<byte> record = bytes.Slice(SnapshotHeaderSize + i * UnitSize, UnitSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, unit.Id.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], unit.Definition);
            BinaryPrimitives.WriteSingleLittleEndian(record[6..], unit.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[10..], unit.Position.Y);
            BinaryPrimitives.WriteUInt32LittleEndian(record[14..], unit.SupportSurface);
            record[18] = (byte)unit.NavStatus;
            BinaryPrimitives.WriteUInt16LittleEndian(record[19..], unit.Health);
            BinaryPrimitives.WriteUInt16LittleEndian(record[21..], unit.Ammunition);
            BinaryPrimitives.WriteUInt16LittleEndian(record[23..], unit.ReloadTicksRemaining);
            record[25] = (byte)unit.AttackStatus;
            BinaryPrimitives.WriteUInt32LittleEndian(record[26..], unit.TargetContactId.Value);
            record[30] = (byte)unit.Stance;
            BinaryPrimitives.WriteUInt16LittleEndian(record[31..], QuantizeYaw(unit.TurretYaw));
            BinaryPrimitives.WriteUInt16LittleEndian(record[33..], QuantizeYaw(unit.HullHeading));
            BinaryPrimitives.WriteUInt32LittleEndian(record[35..], unit.NavOrderSequence);
            BinaryPrimitives.WriteUInt32LittleEndian(record[39..], unit.ContainerId.Value);
            record[43] = unit.PassengerCount;
            record[44] = unit.PassengerCapacity;
            record[45] = unit.SmokeCharges;
            BinaryPrimitives.WriteUInt16LittleEndian(record[46..], unit.SmokeCooldownTicks);
            record[48] = (byte)((unit.HasSmokeCapability ? 1 : 0) | (unit.SmokeActive ? 2 : 0));
            BinaryPrimitives.WriteSingleLittleEndian(record[49..], unit.Altitude);
            BinaryPrimitives.WriteUInt16LittleEndian(record[53..], QuantizeYaw(unit.PitchRad));
            BinaryPrimitives.WriteUInt16LittleEndian(record[55..], QuantizeYaw(unit.RollRad));
            byte flightFlags = (byte)(((byte)unit.FlightPhase & 0x07) |
                (((byte)unit.AltitudeBand & 0x01) << 3) |
                (unit.HasFlightCapability ? 0x10 : 0) |
                (((byte)unit.FlightFlags & 0x07) << 5));
            record[57] = flightFlags;
            BinaryPrimitives.WriteUInt32LittleEndian(record[58..], unit.SlingLoadId.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(record[62..], unit.SlungById.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(record[66..], QuantizeYaw(unit.GunPitchRad));
            record[68] = (byte)unit.ArtilleryFlags;
        }

        int contactsOffset = SnapshotHeaderSize + units.Length * UnitSize;
        for (int i = 0; i < contacts.Length; i++)
        {
            ContactObservation contact = contacts[i];
            Span<byte> record = bytes.Slice(contactsOffset + i * ContactSize, ContactSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, contact.Id.Value);
            record[4] = (byte)contact.State;
            record[5] = (byte)contact.Classification;
            BinaryPrimitives.WriteUInt16LittleEndian(record[6..], contact.Type);
            BinaryPrimitives.WriteSingleLittleEndian(record[8..], contact.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[12..], contact.Position.Y);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], contact.LastSeenTick);
            BinaryPrimitives.WriteUInt16LittleEndian(record[20..], QuantizeYaw(contact.TurretYaw));
            BinaryPrimitives.WriteUInt16LittleEndian(record[22..], QuantizeYaw(contact.HullHeading));
            BinaryPrimitives.WriteUInt32LittleEndian(record[24..], contact.SupportSurface);
            BinaryPrimitives.WriteSingleLittleEndian(record[28..], contact.Altitude);
            record[32] = (byte)contact.FlightPhase;
            BinaryPrimitives.WriteUInt16LittleEndian(record[33..], QuantizeYaw(contact.GunPitchRad));
        }

        int wrecksOffset = SnapshotHeaderSize + units.Length * UnitSize + contacts.Length * ContactSize;
        for (int i = 0; i < wrecks.Length; i++)
        {
            WreckObservation wreck = wrecks[i];
            Span<byte> record = bytes.Slice(wrecksOffset + i * WreckSize, WreckSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, wreck.Id.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], wreck.Definition);
            BinaryPrimitives.WriteSingleLittleEndian(record[6..], wreck.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[10..], wreck.Position.Y);
            BinaryPrimitives.WriteUInt32LittleEndian(record[14..], wreck.SupportSurface);
            BinaryPrimitives.WriteUInt16LittleEndian(record[18..], QuantizeYaw(wreck.HullHeading));
            BinaryPrimitives.WriteUInt16LittleEndian(record[20..], QuantizeYaw(wreck.TurretYaw));
        }

        int smokeOffset = SnapshotHeaderSize + units.Length * UnitSize + contacts.Length * ContactSize + wrecks.Length * WreckSize;
        for (int i = 0; i < smokeClouds.Length; i++)
        {
            SmokeCloudObservation cloud = smokeClouds[i];
            Span<byte> record = bytes.Slice(smokeOffset + i * SmokeSize, SmokeSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, cloud.Id.Value);
            BinaryPrimitives.WriteSingleLittleEndian(record[4..], cloud.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[8..], cloud.Position.Y);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], cloud.SupportSurface);
            BinaryPrimitives.WriteSingleLittleEndian(record[16..], cloud.Radius);
            BinaryPrimitives.WriteSingleLittleEndian(record[20..], cloud.Height);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], cloud.CreatedTick);
            BinaryPrimitives.WriteInt32LittleEndian(record[28..], cloud.ExpiresTick);
            BinaryPrimitives.WriteUInt16LittleEndian(record[32..], cloud.BloomTicks);
            BinaryPrimitives.WriteUInt16LittleEndian(record[34..], cloud.FadeTicks);
            BinaryPrimitives.WriteSingleLittleEndian(record[36..], cloud.OpticalDensity);
        }

        int eventsOffset = smokeOffset + smokeClouds.Length * SmokeSize;
        for (int i = 0; i < events.Length; i++)
        {
            CombatEventObservation ev = events[i];
            Span<byte> record = bytes.Slice(eventsOffset + i * EventSize, EventSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, ev.EventId);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], ev.ServerTick);
            record[8] = (byte)ev.Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(record[9..], ev.PresentationId);
            BinaryPrimitives.WriteUInt32LittleEndian(record[11..], ev.ShooterUnitId.Value);
            BinaryPrimitives.WriteSingleLittleEndian(record[15..], ev.Origin.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[19..], ev.Origin.Y);
            BinaryPrimitives.WriteSingleLittleEndian(record[23..], ev.Origin.Z);
            BinaryPrimitives.WriteSingleLittleEndian(record[27..], ev.Target.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[31..], ev.Target.Y);
            BinaryPrimitives.WriteSingleLittleEndian(record[35..], ev.Target.Z);
            record[39] = (byte)ev.Result;
        }

        return length;
    }

    public static int WriteSnapshotFragment(Span<byte> bytes, uint sequence, int serverTick, ushort fragmentIndex,
        ushort fragmentCount, int totalPayloadSize, int fragmentOffset, ReadOnlySpan<byte> payload)
    {
        int totalLength = FragmentHeaderSize + payload.Length;
        if (bytes.Length < totalLength) throw new ArgumentException("Fragment buffer is too small.");
        bytes[0] = Version;
        bytes[1] = SnapshotFragmentKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], serverTick);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], fragmentIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[12..], fragmentCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[14..], totalPayloadSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[18..], fragmentOffset);
        payload.CopyTo(bytes[FragmentHeaderSize..]);
        return totalLength;
    }

    public static bool TryReadSnapshotFragment(ReadOnlySpan<byte> bytes, out uint sequence, out int serverTick,
        out ushort fragmentIndex, out ushort fragmentCount, out int totalPayloadSize, out int fragmentOffset,
        out ReadOnlySpan<byte> payload)
    {
        sequence = 0;
        serverTick = 0;
        fragmentIndex = 0;
        fragmentCount = 0;
        totalPayloadSize = 0;
        fragmentOffset = 0;
        payload = default;
        if (bytes.Length < FragmentHeaderSize || bytes[0] != Version || bytes[1] != SnapshotFragmentKind)
            return false;
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        serverTick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        fragmentIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        fragmentCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        totalPayloadSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[14..]);
        fragmentOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
        payload = bytes[FragmentHeaderSize..];

        if (sequence == 0 || serverTick < 0 || fragmentCount == 0 || fragmentIndex >= fragmentCount ||
            totalPayloadSize < SnapshotHeaderSize || totalPayloadSize > MaxMessageSize ||
            fragmentOffset < 0 || payload.Length < 0 || payload.Length > MaxFragmentPayloadSize ||
            payload.Length > totalPayloadSize || fragmentOffset > totalPayloadSize - payload.Length)
            return false;

        int expectedOffset = fragmentIndex * MaxFragmentPayloadSize;
        if (fragmentOffset != expectedOffset) return false;
        int expectedLength = Math.Min(MaxFragmentPayloadSize, totalPayloadSize - expectedOffset);
        if (payload.Length != expectedLength) return false;
        ushort expectedCount = (ushort)((totalPayloadSize + MaxFragmentPayloadSize - 1) / MaxFragmentPayloadSize);
        if (fragmentCount != expectedCount) return false;

        return true;
    }

    internal static bool TryReadSnapshot(ReadOnlySpan<byte> bytes, SnapshotFrame frame)
    {
        if (bytes.Length < SnapshotHeaderSize || bytes[0] != Version || bytes[1] != SnapshotKind) return false;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        int tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[6..]);
        int unitCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        int contactCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        int eventCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        int wreckCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]);
        int smokeCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]);

        if (sequence == 0 || tick < 0 || unitCount > frame.Units.Length || contactCount > frame.Contacts.Length || eventCount > frame.Events.Length || wreckCount > frame.Wrecks.Length || smokeCount > frame.SmokeClouds.Length)
            return false;

        int expectedLength = checked(SnapshotHeaderSize + unitCount * UnitSize + contactCount * ContactSize + wreckCount * WreckSize + smokeCount * SmokeSize + eventCount * EventSize);
        if (bytes.Length != expectedLength) return false;

        ReadOnlySpan<byte> missionRecord = bytes.Slice(20, MissionSize);
        ushort missionId = BinaryPrimitives.ReadUInt16LittleEndian(missionRecord);
        byte statusByte = missionRecord[2];
        byte endReasonByte = missionRecord[3];
        int timeLimitTicks = BinaryPrimitives.ReadInt32LittleEndian(missionRecord[4..]);
        int finishedTick = BinaryPrimitives.ReadInt32LittleEndian(missionRecord[8..]);
        uint missionTargetContactVal = BinaryPrimitives.ReadUInt32LittleEndian(missionRecord[12..]);
        Vector2 searchAreaCenter = new(
            BinaryPrimitives.ReadSingleLittleEndian(missionRecord[16..]),
            BinaryPrimitives.ReadSingleLittleEndian(missionRecord[20..]));
        float searchAreaRadius = BinaryPrimitives.ReadSingleLittleEndian(missionRecord[24..]);
        byte flags = missionRecord[28];

        if (statusByte > (byte)MissionStatus.Draw ||
            endReasonByte > (byte)MissionEndReason.TimeLimitExpired ||
            timeLimitTicks < 0 ||
            finishedTick < 0 ||
            !float.IsFinite(searchAreaCenter.X) || !float.IsFinite(searchAreaCenter.Y) ||
            !float.IsFinite(searchAreaRadius) || searchAreaRadius < 0f)
        {
            return false;
        }

        frame.Mission = new MissionObservation(
            missionId,
            (MissionStatus)statusByte,
            (MissionEndReason)endReasonByte,
            timeLimitTicks,
            finishedTick,
            new ContactId(missionTargetContactVal),
            searchAreaCenter,
            searchAreaRadius,
            flags);

        frame.Indices.Clear();
        for (int i = 0; i < unitCount; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(SnapshotHeaderSize + i * UnitSize, UnitSize);
            EntityId id = new(BinaryPrimitives.ReadUInt32LittleEndian(record));
            ushort definition = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            Vector2 position = new(BinaryPrimitives.ReadSingleLittleEndian(record[6..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[10..]));
            uint surface = BinaryPrimitives.ReadUInt32LittleEndian(record[14..]);
            byte navStatusByte = record[18];
            ushort health = BinaryPrimitives.ReadUInt16LittleEndian(record[19..]);
            ushort ammo = BinaryPrimitives.ReadUInt16LittleEndian(record[21..]);
            ushort reloadTicks = BinaryPrimitives.ReadUInt16LittleEndian(record[23..]);
            byte attackStatusByte = record[25];
            uint targetContactVal = BinaryPrimitives.ReadUInt32LittleEndian(record[26..]);
            byte stanceByte = record[30];
            float turretYaw = DequantizeYaw(BinaryPrimitives.ReadUInt16LittleEndian(record[31..]));
            float hullHeading = DequantizeYaw(BinaryPrimitives.ReadUInt16LittleEndian(record[33..]));
            uint navOrderSequence = BinaryPrimitives.ReadUInt32LittleEndian(record[35..]);
            uint containerIdVal = BinaryPrimitives.ReadUInt32LittleEndian(record[39..]);
            byte passengerCount = record[43];
            byte passengerCapacity = record[44];
            byte smokeCharges = record[45];
            ushort smokeCooldownTicks = BinaryPrimitives.ReadUInt16LittleEndian(record[46..]);
            byte smokeFlags = record[48];
            float altitude = BinaryPrimitives.ReadSingleLittleEndian(record[49..]);
            float pitchRad = DequantizeYaw(BinaryPrimitives.ReadUInt16LittleEndian(record[53..]));
            float rollRad = DequantizeYaw(BinaryPrimitives.ReadUInt16LittleEndian(record[55..]));
            byte flightFlags = record[57];
            byte flightPhaseByte = (byte)(flightFlags & 0x07);
            byte altitudeBandByte = (byte)((flightFlags >> 3) & 0x01);
            bool hasFlightCapability = (flightFlags & 0x10) != 0;
            FlightStateFlags runtimeFlightFlags = (FlightStateFlags)((flightFlags >> 5) & 0x07);
            uint slingLoadIdVal = BinaryPrimitives.ReadUInt32LittleEndian(record[58..]);
            uint slungByIdVal = BinaryPrimitives.ReadUInt32LittleEndian(record[62..]);
            float gunPitchRad = DequantizeYaw(BinaryPrimitives.ReadUInt16LittleEndian(record[66..]));
            byte artilleryFlagsByte = record[68];

            if (id.Value == 0 || !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(altitude) || altitude < 0f ||
                navStatusByte > (byte)PathRequestStatus.WaitingForClearance ||
                attackStatusByte > (byte)AttackExecutionStatus.TargetLost ||
                stanceByte > (byte)FireStance.HoldFire ||
                (smokeFlags & ~3) != 0 ||
                flightPhaseByte > (byte)FlightPhase.Crashing ||
                (artilleryFlagsByte & ~3) != 0 ||
                !frame.Indices.TryAdd(id, i)) return false;

            frame.Units[i] = new OwnUnitObservation(
                id,
                definition,
                position,
                surface,
                (PathRequestStatus)navStatusByte,
                health,
                ammo,
                reloadTicks,
                (AttackExecutionStatus)attackStatusByte,
                new ContactId(targetContactVal),
                (FireStance)stanceByte,
                turretYaw,
                hullHeading,
                navOrderSequence,
                new EntityId(containerIdVal),
                passengerCount,
                passengerCapacity,
                smokeCharges,
                smokeCooldownTicks,
                (smokeFlags & 1) != 0,
                (smokeFlags & 2) != 0,
                altitude,
                (FlightPhase)flightPhaseByte,
                (AltitudeBand)altitudeBandByte,
                pitchRad,
                rollRad,
                hasFlightCapability,
                new EntityId(slingLoadIdVal),
                new EntityId(slungByIdVal),
                runtimeFlightFlags,
                gunPitchRad,
                (ArtilleryFlags)artilleryFlagsByte);
        }

        int contactsOffset = SnapshotHeaderSize + unitCount * UnitSize;
        frame.ContactIndices.Clear();
        for (int i = 0; i < contactCount; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(contactsOffset + i * ContactSize, ContactSize);
            ContactId contactId = new(BinaryPrimitives.ReadUInt32LittleEndian(record));
            byte stateByte = record[4];
            byte classByte = record[5];
            ushort typeVal = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            Vector2 position = new(BinaryPrimitives.ReadSingleLittleEndian(record[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[12..]));
            int lastSeenTick = BinaryPrimitives.ReadInt32LittleEndian(record[16..]);
            float turretYaw = DequantizeYaw(BinaryPrimitives.ReadUInt16LittleEndian(record[20..]));
            float hullHeading = DequantizeYaw(BinaryPrimitives.ReadUInt16LittleEndian(record[22..]));
            uint supportSurface = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
            float contactAltitude = BinaryPrimitives.ReadSingleLittleEndian(record[28..]);
            byte contactFlightPhase = record[32];
            float contactGunPitchRad = DequantizeYaw(BinaryPrimitives.ReadUInt16LittleEndian(record[33..]));

            if (contactId.Value == 0 ||
                stateByte is not ((byte)ContactState.Current or (byte)ContactState.LastKnown) ||
                classByte > (byte)ContactClassification.Artillery ||
                !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(contactAltitude) || contactAltitude < 0f ||
                contactFlightPhase > (byte)FlightPhase.Crashing ||
                lastSeenTick < 0 || lastSeenTick > tick ||
                !frame.ContactIndices.TryAdd(contactId, i))
            {
                return false;
            }

            frame.Contacts[i] = new ContactObservation(
                contactId,
                (ContactState)stateByte,
                (ContactClassification)classByte,
                typeVal,
                position,
                lastSeenTick,
                turretYaw,
                hullHeading,
                supportSurface,
                contactAltitude,
                (FlightPhase)contactFlightPhase,
                contactGunPitchRad);
        }

        int wrecksOffset = SnapshotHeaderSize + unitCount * UnitSize + contactCount * ContactSize;
        frame.WreckIndices.Clear();
        for (int i = 0; i < wreckCount; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(wrecksOffset + i * WreckSize, WreckSize);
            WreckId wreckId = new(BinaryPrimitives.ReadUInt32LittleEndian(record));
            ushort definition = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            Vector2 position = new(
                BinaryPrimitives.ReadSingleLittleEndian(record[6..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[10..]));
            uint supportSurface = BinaryPrimitives.ReadUInt32LittleEndian(record[14..]);
            float hullHeading = DequantizeYaw(BinaryPrimitives.ReadUInt16LittleEndian(record[18..]));
            float turretYaw = DequantizeYaw(BinaryPrimitives.ReadUInt16LittleEndian(record[20..]));

            if (wreckId.Value == 0 ||
                !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !frame.WreckIndices.TryAdd(wreckId, i))
            {
                return false;
            }

            frame.Wrecks[i] = new WreckObservation(
                wreckId,
                definition,
                position,
                supportSurface,
                hullHeading,
                turretYaw);
        }

        int smokeOffset = SnapshotHeaderSize + unitCount * UnitSize + contactCount * ContactSize + wreckCount * WreckSize;
        frame.SmokeIndices.Clear();
        for (int i = 0; i < smokeCount; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(smokeOffset + i * SmokeSize, SmokeSize);
            SmokeCloudId smokeId = new(BinaryPrimitives.ReadUInt32LittleEndian(record));
            Vector2 position = new(
                BinaryPrimitives.ReadSingleLittleEndian(record[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[8..]));
            uint supportSurface = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            float radius = BinaryPrimitives.ReadSingleLittleEndian(record[16..]);
            float height = BinaryPrimitives.ReadSingleLittleEndian(record[20..]);
            int createdTick = BinaryPrimitives.ReadInt32LittleEndian(record[24..]);
            int expiresTick = BinaryPrimitives.ReadInt32LittleEndian(record[28..]);
            ushort bloomTicks = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            ushort fadeTicks = BinaryPrimitives.ReadUInt16LittleEndian(record[34..]);
            float opticalDensity = BinaryPrimitives.ReadSingleLittleEndian(record[36..]);

            if (smokeId.Value == 0 ||
                !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(radius) || radius <= 0f ||
                !float.IsFinite(height) || height <= 0f ||
                !float.IsFinite(opticalDensity) || opticalDensity <= 0f ||
                createdTick < 0 || expiresTick <= createdTick ||
                !frame.SmokeIndices.TryAdd(smokeId, i))
            {
                return false;
            }

            frame.SmokeClouds[i] = new SmokeCloudObservation(
                smokeId,
                position,
                supportSurface,
                radius,
                height,
                createdTick,
                expiresTick,
                bloomTicks,
                fadeTicks,
                opticalDensity);
        }

        int eventsOffset = smokeOffset + smokeCount * SmokeSize;
        for (int i = 0; i < eventCount; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(eventsOffset + i * EventSize, EventSize);
            uint eventId = BinaryPrimitives.ReadUInt32LittleEndian(record);
            int serverTick = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            byte kindByte = record[8];
            ushort presId = BinaryPrimitives.ReadUInt16LittleEndian(record[9..]);
            uint shooterIdVal = BinaryPrimitives.ReadUInt32LittleEndian(record[11..]);
            Vector3 origin = new(
                BinaryPrimitives.ReadSingleLittleEndian(record[15..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[19..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[23..]));
            Vector3 target = new(
                BinaryPrimitives.ReadSingleLittleEndian(record[27..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[31..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[35..]));

            byte resultByte = record[39];

            if (eventId == 0 || serverTick < 0 ||
                kindByte < (byte)CombatEventKind.ShotFired ||
                kindByte > (byte)CombatEventKind.ArtilleryImpact ||
                resultByte > (byte)HitResult.Miss ||
                !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z) ||
                !float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z))
            {
                return false;
            }

            frame.Events[i] = new CombatEventObservation(
                eventId,
                serverTick,
                (CombatEventKind)kindByte,
                presId,
                origin,
                target,
                new EntityId(shooterIdVal),
                (HitResult)resultByte);
        }

        frame.Sequence = sequence;
        frame.Tick = tick;
        frame.Count = unitCount;
        frame.ContactCount = contactCount;
        frame.EventCount = eventCount;
        frame.WreckCount = wreckCount;
        frame.SmokeCount = smokeCount;
        return true;
    }
}

public sealed class SnapshotAssembler
{
    private readonly byte[] _buffer = new byte[WireProtocol.MaxMessageSize];
    private readonly bool[] _received = new bool[(WireProtocol.MaxMessageSize + WireProtocol.MaxFragmentPayloadSize - 1) / WireProtocol.MaxFragmentPayloadSize];
    private uint _sequence;
    private int _serverTick;
    private int _totalSize;
    private ushort _count;
    private ushort _receivedCount;
    private int _bytesReceived;
    public uint LatestCompletedSequence { get; private set; }

    public bool TryAddFragment(ReadOnlySpan<byte> message, out ReadOnlySpan<byte> completeSnapshot)
    {
        completeSnapshot = default;
        if (!WireProtocol.TryReadSnapshotFragment(message, out uint sequence, out int serverTick, out ushort index,
            out ushort count, out int totalSize, out int offset, out ReadOnlySpan<byte> payload))
            return false;

        if (sequence <= LatestCompletedSequence || sequence < _sequence) return false;
        if (totalSize > _buffer.Length || count > _received.Length) return false;

        if (sequence != _sequence)
        {
            Array.Clear(_buffer, 0, Math.Max(_totalSize, totalSize));
            _sequence = sequence;
            _serverTick = serverTick;
            _totalSize = totalSize;
            _count = count;
            _receivedCount = 0;
            _bytesReceived = 0;
            Array.Clear(_received, 0, _received.Length);
        }

        if (totalSize != _totalSize || count != _count || serverTick != _serverTick) return false;
        if (_received[index]) return false;

        payload.CopyTo(_buffer.AsSpan(offset, payload.Length));
        _received[index] = true;
        _receivedCount++;
        _bytesReceived += payload.Length;

        if (_receivedCount == _count && _bytesReceived == _totalSize)
        {
            LatestCompletedSequence = _sequence;
            completeSnapshot = _buffer.AsSpan(0, _totalSize);
            return true;
        }

        return false;
    }
}

internal sealed class SnapshotFrame
{
    public readonly OwnUnitObservation[] Units;
    public readonly ContactObservation[] Contacts;
    public readonly CombatEventObservation[] Events;
    public readonly WreckObservation[] Wrecks;
    public readonly SmokeCloudObservation[] SmokeClouds;
    public readonly Dictionary<EntityId, int> Indices;
    public readonly Dictionary<ContactId, int> ContactIndices;
    public readonly Dictionary<WreckId, int> WreckIndices;
    public readonly Dictionary<SmokeCloudId, int> SmokeIndices;
    public int Count, ContactCount, EventCount, WreckCount, SmokeCount, Tick;
    public uint Sequence;
    public MissionObservation Mission;

    public SnapshotFrame(
        int capacity,
        int contactCapacity = WireProtocol.MaxContacts,
        int eventCapacity = WireProtocol.MaxEvents,
        int wreckCapacity = WireProtocol.MaxWrecks,
        int smokeCapacity = WireProtocol.MaxSmokeClouds)
    {
        Units = new OwnUnitObservation[capacity];
        Contacts = new ContactObservation[contactCapacity];
        Events = new CombatEventObservation[eventCapacity];
        Wrecks = new WreckObservation[wreckCapacity];
        SmokeClouds = new SmokeCloudObservation[smokeCapacity];
        Indices = new Dictionary<EntityId, int>(capacity);
        ContactIndices = new Dictionary<ContactId, int>(contactCapacity);
        WreckIndices = new Dictionary<WreckId, int>(wreckCapacity);
        SmokeIndices = new Dictionary<SmokeCloudId, int>(smokeCapacity);
    }
}
