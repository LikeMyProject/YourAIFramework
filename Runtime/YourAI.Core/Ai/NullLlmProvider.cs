using YourAI.Core.Contracts;

namespace YourAI.Core.Ai
{
    /// <summary>
    /// A provider that exists so lookups never come back empty. Same reasoning as
    /// <see cref="NullAiService"/>, one layer down: <c>Resolve</c> returning null
    /// would push a null check onto every call site that only exists because of a
    /// configuration mistake.
    /// </summary>
    public sealed class NullLlmProvider : ILlmProvider
    {
        private readonly string _reason;

        public NullLlmProvider(string reason)
        {
            _reason = string.IsNullOrEmpty(reason) ? "Provider is unavailable." : reason;
        }

        public NullLlmProvider()
            : this(null)
        {
        }

        public string Name { get { return "null"; } }

        public bool IsAvailable { get { return false; } }

        public string UnavailableReason { get { return _reason; } }

        public ILlmStreamHandle StartStream(LlmRequest request)
        {
            return new FailedStreamHandle(_reason);
        }
    }
}
