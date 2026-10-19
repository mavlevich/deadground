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
    ServerFull = 5
}

public static class WireProtocol
{
    public const byte Version = MatchSetup.CurrentProtocolVersion;
    public const int MoveSize = 22;
    public const int AttackSize = 18;
    public const int SetFireStanceSize = 15;
    public const int CommandResultSize = 11;
    public const int MissionSize = 32;
    public const int SnapshotHeaderSize = 16 + MissionSize;
    public const int UnitSize = 35;
    public const int MaxUnits = 1024;
    public const int ContactSize = 24;
    public const int MaxContacts = 256;
    public const int EventSize = 40;
    public const int MaxEvents = 64;
    public const int MaxMessageSize = SnapshotHeaderSize + MaxUnits * UnitSize + MaxContacts * ContactSize + MaxEvents * EventSize;
    public const int HandshakeResponseSize = 5;
    public const int FragmentHeaderSize = 22;
    public const int MaxFragmentPayloadSize = 960;

    public const byte MoveKind = 1;
    public const byte SnapshotKind = 2;
    public const byte CommandResultKind = 3;
    public const byte SnapshotFragmentKind = 4;
    public const byte HandshakeRequestKind = 5;
    public const byte HandshakeResponseKind = 6;
    public const byte AttackKind = 7;
    public const byte SetFireStanceKind = 8;

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

    public static int WriteSnapshot(
        Span<byte> bytes,
        uint sequence,
        int tick,
        ReadOnlySpan<OwnUnitObservation> units,
        ReadOnlySpan<ContactObservation> contacts = default,
        ReadOnlySpan<CombatEventObservation> events = default,
        MissionObservation mission = default)
    {
        int length = checked(SnapshotHeaderSize + units.Length * UnitSize + contacts.Length * ContactSize + events.Length * EventSize);
        if (units.Length > MaxUnits || contacts.Length > MaxContacts || events.Length > MaxEvents || bytes.Length < length)
            throw new ArgumentException("Snapshot buffer is too small or capacity exceeded.");

        bytes[0] = Version; bytes[1] = SnapshotKind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[2..], sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[6..], tick);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], (ushort)units.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[12..], (ushort)contacts.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[14..], (ushort)events.Length);

        Span<byte> missionRecord = bytes.Slice(16, MissionSize);
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
        }

        int eventsOffset = SnapshotHeaderSize + units.Length * UnitSize + contacts.Length * ContactSize;
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

        if (sequence == 0 || tick < 0 || unitCount > frame.Units.Length || contactCount > frame.Contacts.Length || eventCount > frame.Events.Length)
            return false;

        int expectedLength = checked(SnapshotHeaderSize + unitCount * UnitSize + contactCount * ContactSize + eventCount * EventSize);
        if (bytes.Length != expectedLength) return false;

        ReadOnlySpan<byte> missionRecord = bytes.Slice(16, MissionSize);
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

        if (statusByte > (byte)MissionStatus.Defeat ||
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

            if (id.Value == 0 || !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                navStatusByte > (byte)PathRequestStatus.CapacityExceeded ||
                attackStatusByte > (byte)AttackExecutionStatus.TargetLost ||
                stanceByte > (byte)FireStance.HoldFire ||
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
                hullHeading);
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

            if (contactId.Value == 0 ||
                stateByte is not ((byte)ContactState.Current or (byte)ContactState.LastKnown) ||
                classByte > (byte)ContactClassification.Apc ||
                !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
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
                hullHeading);
        }

        int eventsOffset = SnapshotHeaderSize + unitCount * UnitSize + contactCount * ContactSize;
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
                (kindByte != (byte)CombatEventKind.ShotFired &&
                 kindByte != (byte)CombatEventKind.UnitDestroyed &&
                 kindByte != (byte)CombatEventKind.TookFire) ||
                resultByte > (byte)HitResult.Deflected ||
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
        return true;
    }
}

public sealed class SnapshotAssembler
{
    private readonly byte[] _buffer = new byte[WireProtocol.MaxMessageSize];
    private readonly bool[] _received = new bool[64];
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
    public readonly Dictionary<EntityId, int> Indices;
    public readonly Dictionary<ContactId, int> ContactIndices;
    public int Count, ContactCount, EventCount, Tick;
    public uint Sequence;
    public MissionObservation Mission;

    public SnapshotFrame(
        int capacity,
        int contactCapacity = WireProtocol.MaxContacts,
        int eventCapacity = WireProtocol.MaxEvents)
    {
        Units = new OwnUnitObservation[capacity];
        Contacts = new ContactObservation[contactCapacity];
        Events = new CombatEventObservation[eventCapacity];
        Indices = new Dictionary<EntityId, int>(capacity);
        ContactIndices = new Dictionary<ContactId, int>(contactCapacity);
    }
}
