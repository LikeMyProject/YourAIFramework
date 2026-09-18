using System;

namespace YourAI.Core.Contracts
{
    /// <summary>
    /// A handle on one in-flight streaming request.
    ///
    /// The design constraint behind this interface: the core assembly must not
    /// reference UnityEngine, so it cannot use Awaitable, coroutines,
    /// MonoBehaviour.Update, or any other engine construct. The kernel therefore
    /// exposes only two primitives -- poll a state property, and subscribe to events.
    ///
    /// <see cref="Pump"/> is the poll half. Whoever owns the frame loop (a
    /// MonoBehaviour, a coroutine, an editor update hook) calls it. The kernel never
    /// asks the engine what time it is or when the next frame is.
    ///
    /// Threading contract, and this one is not optional:
    /// every event on this interface is raised on the thread that called
    /// <see cref="Pump"/>. A transport that receives bytes on a background thread
    /// must queue them and let Pump drain the queue. Consumers may therefore touch
    /// UI, transforms and other engine state directly inside these handlers.
    /// </summary>
    public interface ILlmStreamHandle
    {
        /// <summary>Current lifecycle state. Safe to read every frame.</summary>
        LlmStreamState State { get; }

        /// <summary>Assistant text accumulated so far. Grows monotonically.</summary>
        string Content { get; }

        /// <summary>Reasoning text for reasoner-style models; empty for ordinary ones.</summary>
        string Reasoning { get; }

        /// <summary>Non-null only when State is Faulted.</summary>
        string Error { get; }

        /// <summary>Raised once per incremental text fragment, on the Pump thread.</summary>
        event Action<string> ContentDelta;

        /// <summary>Raised once per incremental reasoning fragment, on the Pump thread.</summary>
        event Action<string> ReasoningDelta;

        /// <summary>Raised exactly once, on the Pump thread, whatever the outcome.</summary>
        event Action<LlmStreamResult> Completed;

        /// <summary>
        /// Advances the handle: drains pending transport work, raises events, and
        /// finalises state. Returns true while more work remains, false once the
        /// handle has reached a terminal state.
        /// </summary>
        bool Pump();

        /// <summary>Cancels the request. Safe to call after completion; then it does nothing.</summary>
        void Cancel();
    }

    /// <summary>
    /// A source of inference. Implemented per vendor (DeepSeek, OpenAI, a local
    /// mock, ...). Providers are registered by <see cref="Name"/> and selected at
    /// runtime, so application code depends on this interface and never on a vendor.
    /// </summary>
    public interface ILlmProvider
    {
        /// <summary>Stable identifier used for registration, e.g. "deepseek".</summary>
        string Name { get; }

        /// <summary>
        /// False when the provider cannot serve requests, typically because no
        /// credentials are configured. Callers should treat this as a routing hint
        /// rather than an error: <see cref="StartStream"/> still returns a handle,
        /// which simply faults with an explanatory message.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>Human-readable explanation when <see cref="IsAvailable"/> is false.</summary>
        string UnavailableReason { get; }

        /// <summary>
        /// Begins a request. Must not throw and must not block: all failures are
        /// reported through the returned handle's Completed event. This is what lets
        /// business code use the AI layer without a single try/catch or null check.
        /// </summary>
        ILlmStreamHandle StartStream(LlmRequest request);
    }
}
