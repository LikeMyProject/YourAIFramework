using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Core.Ai
{
    /// <summary>
    /// The AI module switched off, expressed as an object rather than as an absence.
    ///
    /// This is the reason business code never needs a null check. Something is always
    /// installed; sometimes that something declines to do anything. Calls return a
    /// faulting handle, the fault explains itself, and the surrounding feature keeps
    /// working -- a quest log, an inventory, a save system, all of which have nothing
    /// to do with language models and must not be held hostage by them.
    /// </summary>
    public sealed class NullAiService : IAiService
    {
        private static readonly string[] NoProviders = new string[0];

        /// <summary>Shared instance for the common "no key configured" case.</summary>
        public static readonly NullAiService Instance =
            new NullAiService("No provider is configured. Set an API key or install a provider.");

        private readonly string _reason;

        public NullAiService(string reason)
        {
            _reason = string.IsNullOrEmpty(reason) ? "AI is unavailable." : reason;
        }

        public bool IsAvailable { get { return false; } }

        public string UnavailableReason { get { return _reason; } }

        public IReadOnlyList<string> ProviderNames { get { return NoProviders; } }

        public ILlmProvider Resolve(string providerName)
        {
            return new NullLlmProvider(_reason);
        }

        public ILlmStreamHandle Chat(LlmRequest request)
        {
            return new FailedStreamHandle(_reason);
        }

        public ILlmStreamHandle Chat(string providerName, LlmRequest request)
        {
            return new FailedStreamHandle(_reason);
        }
    }
}
