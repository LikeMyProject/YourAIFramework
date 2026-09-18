using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Core.Ai
{
    /// <summary>
    /// The single entry point application code talks to.
    ///
    /// The point of this interface is that it has no failure mode. It is never null,
    /// it never throws, and it is always safe to call even when the project has no
    /// API key, no network, and no provider registered. Code that uses AI reads as
    /// ordinary code rather than as a defensive maze.
    ///
    ///    var reply = ai.Chat(request);
    ///    // ... subscribe, pump, done
    ///
    /// When AI is unavailable, <see cref="IsAvailable"/> is false and the handle
    /// faults with an explanation instead of the call throwing. Nothing upstream
    /// needs a null check.
    /// </summary>
    public interface IAiService
    {
        /// <summary>
        /// False when no usable provider is configured. Callers that need to know
        /// (a UI that should hide a chat button, say) can ask; callers that do not
        /// care can ignore it entirely.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>Explains the false case in one sentence. Empty when available.</summary>
        string UnavailableReason { get; }

        /// <summary>Names of registered providers, in resolution order.</summary>
        IReadOnlyList<string> ProviderNames { get; }

        /// <summary>
        /// Returns the provider that would serve a request, or a faulting stand-in.
        /// Never returns null.
        /// </summary>
        ILlmProvider Resolve(string providerName);

        /// <summary>Starts a request on the default provider.</summary>
        ILlmStreamHandle Chat(LlmRequest request);

        /// <summary>Starts a request on a named provider.</summary>
        ILlmStreamHandle Chat(string providerName, LlmRequest request);
    }
}
