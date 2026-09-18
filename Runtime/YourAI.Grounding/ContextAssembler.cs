using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Grounding
{
    /// <summary>
    /// Collects sections from registered providers and fits them into a token budget.
    ///
    /// Why budgeting lives here rather than in the provider. A provider knows how to
    /// describe its own corner of the world; it has no idea what the other providers
    /// are saying or how much room is left. Asking each one to self-limit produces
    /// prompt material that is individually reasonable and collectively incoherent --
    /// the persona block gets trimmed while the weather report survives, because
    /// neither knew the other existed. One assembler, seeing everything, can make
    /// that trade properly.
    ///
    /// The policy is drop-whole-sections, never truncate. A half-sentence of persona
    /// text is worse than no persona text: it actively misinforms. Sections are
    /// dropped lowest-priority-first, and dropping continues rather than stopping --
    /// a small low-priority section can still fit in whatever the important ones left
    /// behind.
    /// </summary>
    public sealed class ContextAssembler
    {
        private readonly List<IContextProvider> _providers = new List<IContextProvider>(8);
        private readonly List<Candidate> _candidates = new List<Candidate>(8);

        /// <summary>
        /// Estimated tokens allowed across all sections. Zero means unlimited.
        ///
        /// The default is sized for a companion NPC, not for a document Q&amp;A bot: a
        /// character who needs two thousand tokens of world state to answer "good
        /// morning" is a character whose prompt the author has stopped reading.
        /// </summary>
        public int TokenBudget = 1500;

        /// <summary>Sections dropped by the last <see cref="Assemble"/> call, for diagnostics.</summary>
        public int LastDroppedCount { get; private set; }

        public int ProviderCount
        {
            get { return _providers.Count; }
        }

        public void Add(IContextProvider provider)
        {
            if (provider != null)
            {
                _providers.Add(provider);
            }
        }

        public void AddRange(IEnumerable<IContextProvider> providers)
        {
            if (providers == null)
            {
                return;
            }
            foreach (IContextProvider provider in providers)
            {
                Add(provider);
            }
        }

        public bool Remove(IContextProvider provider)
        {
            return _providers.Remove(provider);
        }

        public void Clear()
        {
            _providers.Clear();
        }

        /// <summary>Returns the registered providers, for callers that need to inspect them.</summary>
        public IReadOnlyList<IContextProvider> Providers
        {
            get { return _providers; }
        }

        public AssembledContext Assemble(object worldState)
        {
            _candidates.Clear();
            LastDroppedCount = 0;

            for (int i = 0; i < _providers.Count; i++)
            {
                IContextProvider provider = _providers[i];

                ContextSection section = provider.Build(worldState);
                if (section == null || section.IsEmpty)
                {
                    // An empty section is the normal way for a provider to say
                    // "nothing relevant right now", so it is not a drop.
                    continue;
                }

                _candidates.Add(new Candidate
                {
                    Section = section,
                    Priority = section.Priority != 0 ? section.Priority : provider.Priority,
                    Order = i,
                });
            }

            _candidates.Sort(CompareCandidates);

            AssembledContext context = new AssembledContext();
            int used = 0;

            for (int i = 0; i < _candidates.Count; i++)
            {
                ContextSection section = _candidates[i].Section;

                if (TokenBudget > 0 && used + section.EstimatedTokens > TokenBudget)
                {
                    LastDroppedCount++;
                    continue;
                }

                used += section.EstimatedTokens;
                context.Add(section);
            }

            return context;
        }

        /// <summary>Highest priority first; ties keep registration order so results are reproducible.</summary>
        private static int CompareCandidates(Candidate a, Candidate b)
        {
            int byPriority = b.Priority.CompareTo(a.Priority);
            if (byPriority != 0)
            {
                return byPriority;
            }
            return a.Order.CompareTo(b.Order);
        }

        private struct Candidate
        {
            public ContextSection Section;
            public int Priority;
            public int Order;
        }
    }
}
