# Native event polling follow-up

Recorded while diagnosing the Windows allocation-test failure in PR #44 and resolved in `fix/net-poll-backlog`.

## Root cause

The pinned LiteNetLib 1.3.5 `NetManager.PollEvents(maxProcessedEvents)` detached the entire pending
event list (`_pendingEventHead`), processed up to the budget, and exited without restoring the
remaining tail to the queue. Events beyond the budget were discarded, including reliable receive
events already acknowledged by the library.

Both client and server adapters use this API. Reliable receive events could disappear after the
library acknowledged their packets. The connection remained healthy and application overflow
counters stayed at zero.

Source commit: [cf54c7d7a15a45bd2460cd4a460807329ee7026a](https://github.com/RevenantX/LiteNetLib/blob/cf54c7d7a15a45bd2460cd4a460807329ee7026a/LiteNetLib/NetManager.cs#L1542).

## Allocation-test correction (PR #44 context)

The former allocation test sent 100 reliable messages into a 64-packet application queue and
polled up to 100 callbacks before draining. A burst could therefore trigger the intentional
reliable-overflow disconnect. Its warmup also did not wait for delivery, leaving stray packets.

PR #44 corrected the test harness to wait for delivery and drain batches within receive capacity.
That resolved the test overload, but did not repair the runtime dependency defect.

## Chosen resolution

The dependency does not provide an official 1.x release with this fix. LiteNetLib 2.x removed
bounded polling (`maxProcessedEvents`) and made breaking API changes across all types.

We applied a narrowly scoped, reproducible patch to LiteNetLib 1.3.5:

- Pinned source revision: `cf54c7d7a15a45bd2460cd4a460807329ee7026a` (LiteNetLib 1.3.5 release).
- License: MIT License (Ruslan Pyrch), preserved in `src/Engine.ThirdParty.LiteNetLib/LICENSE.txt`.
- Location: isolated in a dedicated project `src/Engine.ThirdParty.LiteNetLib/Engine.ThirdParty.LiteNetLib.csproj`.
  No external package feed or manual NuGet cache edits are required.
- Compiler diagnostic isolation: `Engine.ThirdParty.LiteNetLib.csproj` enables unsafe blocks, sets `Nullable` to
  `disable`, and suppresses legacy warnings. `Engine.Net.csproj` references this project without disabling any
  warnings. Application transport code in `Engine.Net` remains fully checked under strict warnings-as-errors
  and nullable rules.
- Formatting analyzer: `.editorconfig` marks `[src/Engine.ThirdParty.LiteNetLib/**]` as generated code so code
  formatting analyzers remain clean.

### Core patch behavior

In `NetManager.PollEvents(maxProcessedEvents)`:

- When detaching the pending event chain under `_eventLock`, `NetManager` captures both the head (`_pendingEventHead`)
  and the tail (`_pendingEventTail`).
- When `maxProcessedEvents > 0` and the event budget is reached, the loop breaks with unread events remaining
  in `pendingEvent`.
- The detached tail pointer is already the terminal node of the unread list. The unread list is restored to the
  head in O(1) time without scanning links under `_eventLock`.
- If new events arrived on the background socket thread during callbacks, the unread tail is linked to the head
  of the new events in O(1) time, preserving strict FIFO delivery.
- Restoration performs zero managed heap allocations.
- Lifecycle protection on Stop: if `Stop()` is called during a callback or concurrently, `PollEvents` aborts the
  loop immediately. Unread events are not restored to the queue. Instead, abandoned events and their packet
  readers are recycled via `RecycleEventChain`, returning packet buffers to the pool and preventing callback
  resurrection on restart.
- Receive readiness inspection: `PendingReceiveCount` is exposed on `NetManager` and both client and server transports.
  It counts only data receive events, distinguishing payload packets from native control and latency update events.

### Adapter adjustments

- `LiteNetLibClientTransport`: once explicitly polled via `Poll(budget)`, `TryReceive` strictly drains from the
  bounded receive queue and does not perform fallback lazy polls that could exceed the caller's budget.
- `LiteNetLibServerTransport`: preserves existing round-robin fair scheduling across player queues.

## Verification

The fix is verified by deterministic and UDP tests in `tests/Engine.Core.Tests/TransportTests.cs`:

- `Deterministic_BoundedPoll_PreservesUnreadTailAndFifoOrder`: verifies exact event preservation, budget
  enforcement, and FIFO interleaving of newer events.
- `Deterministic_BoundedPoll_LargeBacklog_BookkeepingIsBounded`: queues 100,000 events and executes 10 bounded
  polls of budget 1; proves constant-time O(1) queue restoration without backlog-dependent link scanning.
- `Deterministic_StopInsideCallback_AbortsPollAndDiscardsPendingEvents`: queues 3 events, invokes `Stop()` inside
  the first callback, and verifies zero callbacks post-stop, zero pending events, and clean restart without resurrection.
- `Deterministic_StopInsideCallback_ReleasesAbandonedPacketReaders`: queues data receive events with pooled packets,
  stops during the first callback, and verifies abandoned packet buffers return to the pool without leaks.
- `RealUdp_BoundedPoll_ClientToServer_BurstDrainedOverMultipleLimitedPolls`: client sends a burst larger than
  poll budget; server drains it over limited polls (budget 3) without extra sends; uses receive-specific readiness,
  respects budget per batch, and validates exact-once delivery and sequential ordering under a deadline.
- `RealUdp_BoundedPoll_ServerToClient_BurstDrainedOverMultipleLimitedPolls`: server sends a burst larger than
  poll budget; client drains it over limited polls under a deadline.
- `RealUdp_BoundedPoll_WithNoiseEvents_PreservesPayloadDeliveryAndOrdering`: injects native latency update events
  before and during a burst; proves non-receive noise does not break delivery, bounded polling, or message order.
- `RealUdp_BoundedPoll_TwoPeers_FairSchedulingUnderHeavyTraffic`: Client 1 floods a 20-message prefix burst while
  Client 2 sends 10 messages; verifies prompt servicing of Client 2 under a bounded deadline, round-robin
  alternation while both queues hold packets, and exact-once delivery for both peers.
- `RealUdp_BoundedPoll_DisconnectAndSlotReuse_StaleEventsNotDeliveredToReplacement`: peer disconnects with unread
  pending events; new peer connects into the same slot; asserts old peer events never surface to the replacement.
- `RealUdp_BoundedPoll_OverloadPolicy_PreservesReliableDisconnectAndUnreliableDrop`: verifies that unreliable packets
  beyond queue capacity are dropped without disconnect, while reliable overflow triggers intentional disconnect.
- `RealUdp_SteadyStateReceive_ZeroManagedAllocations`: verifies zero managed allocations during steady-state UDP
  reception across multiple queue capacities.
