# Native event polling follow-up

Recorded while diagnosing the Windows allocation-test failure in PR #44.

## Confirmed dependency defect

The pinned LiteNetLib 1.3.5 `NetManager.PollEvents(maxProcessedEvents)` detaches the entire pending
event list, processes up to the budget, and does not restore the remaining tail. Events beyond the
budget can disappear, including reliable receive events already acknowledged by the library.
Both client and server adapters use this API, so this is a runtime networking issue as well as a
test concern. It is independent of the infantry import changes.

Source: [the exact NuGet source commit](https://github.com/RevenantX/LiteNetLib/blob/cf54c7d7a15a45bd2460cd4a460807329ee7026a/LiteNetLib/NetManager.cs#L1542).

Reproduction during diagnosis: send 100 reliable messages, use a one-packet application queue,
poll one native event at a time, and drain after every poll. The connection stays admitted and
application overflow counters remain zero, but only two messages are received before the timeout.
The exact received count depends on how background receive batches align with polls.

## Allocation-test correction

The former allocation test sent 100 reliable messages into a 64-packet application queue and
polled up to 100 callbacks before draining. A burst could therefore trigger the intentional
reliable-overflow disconnect. Its warmup also did not wait for delivery, leaving stray packets.

The test now waits for and drains warmup traffic, sends batches no larger than the configured
receive capacity, and drains each batch before sending more. It tests capacities of one and 64,
still requires all 100 messages and zero receive allocations, and asserts no overflow or disconnect.
This removes the test's accidental overload; it does not repair the dependency defect above.

## Required networking follow-up

- Preserve unread native events across limited polls without unbounded main-thread work.
- Keep the application receive-memory limits, reliable-overflow disconnects, and unreliable-drop policy.
- Add a reliable backlog regression that receives a burst larger than one poll budget over multiple
  polls, with no extra sends needed to make progress. Check message identities, not just totals.
- Recheck client and server allocation bounds, fairness, overload, and disconnect lifecycle.
- Do not treat raising receive capacity, using an unlimited poll, or merely rerunning CI as a fix.

An upgrade or adapter change needs its own networking review. It is not part of the asset-import fix.
