namespace DeadGround.Engine.Net;

// Queued is an admission result, not a promise that a unit will reach its destination.
// Rejections carry no unit ID or reason that could reveal hidden world state.
public readonly record struct CommandResult(uint Sequence, int ServerTick, bool Queued);
