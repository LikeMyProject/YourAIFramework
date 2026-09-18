using System.Collections.Generic;

namespace YourAI.Core.Contracts
{
    /// <summary>
    /// One block of text destined for the prompt, plus what it cost to produce.
    /// A section is the unit of budget management: the assembler can drop or
    /// shorten a whole section without understanding its contents.
    /// </summary>
    public sealed class ContextSection
    {
        public string Title;
        public string Body;

        /// <summary>Rough token estimate, used for budgeting. Never precise.</summary>
        public int EstimatedTokens;

        /// <summary>Lower priority sections are dropped first when the budget is tight.</summary>
        public int Priority;

        public static readonly ContextSection Empty = new ContextSection();

        public bool IsEmpty
        {
            get { return string.IsNullOrEmpty(Body); }
        }

        public static ContextSection Of(string title, string body, int priority)
        {
            if (string.IsNullOrEmpty(body))
            {
                return Empty;
            }
            return new ContextSection
            {
                Title = title,
                Body = body,
                Priority = priority,
                EstimatedTokens = EstimateTokens(body),
            };
        }

        /// <summary>
        /// Deliberately crude: CJK sits near one token per character while Latin
        /// text sits near one per four. Being roughly right is enough for budgeting,
        /// and guessing wrong costs a little money rather than correctness.
        /// </summary>
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            int cjk = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 0x2E80 && c <= 0x9FFF)
                {
                    cjk++;
                }
                else if (c >= 0xF900 && c <= 0xFAFF)
                {
                    cjk++;
                }
            }
            int rest = text.Length - cjk;
            return cjk + (rest + 3) / 4;
        }
    }

    /// <summary>
    /// Turns game state into prompt material. This is the seam that keeps the
    /// framework game-agnostic: the framework knows how to assemble a prompt, the
    /// game knows what is worth saying about itself.
    /// </summary>
    public interface IContextProvider
    {
        /// <summary>Stable identifier, used for diagnostics and selective disabling.</summary>
        string Name { get; }

        /// <summary>Higher runs earlier. Lets the host pin the most important blocks.</summary>
        int Priority { get; }

        /// <summary>
        /// Produces a section from the host's world state. Returning an empty section
        /// is normal and means "nothing relevant right now".
        /// </summary>
        ContextSection Build(object worldState);
    }

    /// <summary>
    /// Convenience base that restores type safety without giving up the framework's
    /// ability to hold providers of unrelated state types in one list.
    /// </summary>
    public abstract class ContextProvider<TState> : IContextProvider
    {
        public abstract string Name { get; }
        public virtual int Priority { get { return 0; } }

        public abstract ContextSection Build(TState state);

        ContextSection IContextProvider.Build(object worldState)
        {
            if (worldState is TState typed)
            {
                return Build(typed);
            }
            return ContextSection.Empty;
        }
    }

    /// <summary>A prompt assembled from sections, ready to be handed to a provider.</summary>
    public sealed class AssembledContext
    {
        public readonly List<ContextSection> Sections = new List<ContextSection>();

        public int TotalEstimatedTokens
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < Sections.Count; i++)
                {
                    sum += Sections[i].EstimatedTokens;
                }
                return sum;
            }
        }

        public void Add(ContextSection section)
        {
            if (section != null && !section.IsEmpty)
            {
                Sections.Add(section);
            }
        }

        /// <summary>Renders the sections into a single prompt block.</summary>
        public string Render()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
            for (int i = 0; i < Sections.Count; i++)
            {
                ContextSection s = Sections[i];
                if (sb.Length > 0)
                {
                    sb.Append('\n').Append('\n');
                }
                if (!string.IsNullOrEmpty(s.Title))
                {
                    sb.Append("### ").Append(s.Title).Append('\n');
                }
                sb.Append(s.Body);
            }
            return sb.ToString();
        }
    }
}
