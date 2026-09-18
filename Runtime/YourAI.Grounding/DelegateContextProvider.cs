using System;
using YourAI.Core.Contracts;

namespace YourAI.Grounding
{
    /// <summary>
    /// Builds a section from a delegate.
    ///
    /// This is the provider most projects should reach for first. A game that wants
    /// to tell the model "the player is bleeding, it is raining, and the guard is
    /// watching" does not need a class hierarchy to say so -- it needs somewhere to
    /// put eight lines of formatting. A subclass is the right answer only when the
    /// same state feeds several sections or when the provider owns configuration.
    /// </summary>
    public sealed class DelegateContextProvider<TState> : ContextProvider<TState>
    {
        private readonly string _name;
        private readonly int _priority;
        private readonly Func<TState, ContextSection> _build;

        public DelegateContextProvider(string name, Func<TState, ContextSection> build)
            : this(name, 0, build)
        {
        }

        public DelegateContextProvider(string name, int priority, Func<TState, ContextSection> build)
        {
            _name = string.IsNullOrEmpty(name) ? "context" : name;
            _priority = priority;
            _build = build;
        }

        public override string Name
        {
            get { return _name; }
        }

        public override int Priority
        {
            get { return _priority; }
        }

        public override ContextSection Build(TState state)
        {
            if (_build == null)
            {
                return ContextSection.Empty;
            }
            ContextSection section = _build(state);
            return section ?? ContextSection.Empty;
        }
    }

    /// <summary>
    /// A section that does not depend on world state: persona, output rules, tone.
    /// Fixed text is worth a type of its own because the section is built once at
    /// construction rather than reformatted every turn -- and because persona text is
    /// the one block a host usually wants to pin above everything else.
    /// </summary>
    public sealed class ConstantContextProvider : IContextProvider
    {
        private readonly string _name;
        private readonly int _priority;
        private readonly ContextSection _section;

        public ConstantContextProvider(string name, string title, string body, int priority)
        {
            _name = string.IsNullOrEmpty(name) ? "constant" : name;
            _priority = priority;
            _section = ContextSection.Of(title, body, priority);
        }

        public string Name
        {
            get { return _name; }
        }

        public int Priority
        {
            get { return _priority; }
        }

        public ContextSection Build(object worldState)
        {
            return _section;
        }
    }
}
