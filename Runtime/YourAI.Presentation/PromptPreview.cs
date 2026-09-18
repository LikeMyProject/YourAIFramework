using System.Collections.Generic;
using System.Text;
using YourAI.Core.Contracts;

namespace YourAI.Presentation
{
    /// <summary>
    /// Renders the exact message list a request will carry into text a human can
    /// read before spending the tokens.
    ///
    /// Why this exists. Every confusing conversation with an LLM character is,
    /// eventually, a prompt problem: the persona did not make it in, the context
    /// got dropped by the budget, the history replayed something the designer had
    /// forgotten about. Those are all invisible from the game side, and each one
    /// costs an hour of staring at a reply that "feels wrong".
    ///
    /// So the preview shows everything the model will see -- each message, its
    /// role, and an estimate of its token cost -- and nothing the model will not
    /// see. It reads the same list the provider receives, which is why the source
    /// is <c>AiAgent.PreviewMessages</c> (the same builder the request uses)
    /// rather than a parallel re-implementation that could drift.
    ///
    /// Pure text in, pure text out: this class knows nothing about Unity, and the
    /// Editor window is one more renderer over it.
    /// </summary>
    public static class PromptPreview
    {
        /// <summary>
        /// Renders messages into a readable listing. The header carries the count
        /// and the estimated total; every message follows as
        /// <c>[role · tokens]</c> plus its content, separated by rules.
        /// </summary>
        public static string Render(IReadOnlyList<LlmMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return "(no messages -- an empty request would be sent)";
            }

            int total = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                total += Estimate(messages[i]);
            }

            StringBuilder sb = new StringBuilder(messages.Count * 48);
            sb.Append("=== ").Append(messages.Count).Append(" messages, ~").Append(total).Append(" tokens ===");

            for (int i = 0; i < messages.Count; i++)
            {
                LlmMessage message = messages[i];

                sb.Append("\n---\n[");
                sb.Append(RoleLabel(message.Role));
                int cost = Estimate(message);
                if (cost > 0)
                {
                    sb.Append(" · ~").Append(cost).Append(" tokens");
                }
                sb.Append("]\n");
                sb.Append(message.Content != null ? message.Content : string.Empty);

                if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                {
                    sb.Append("\n[tool calls: ");
                    for (int t = 0; t < message.ToolCalls.Count; t++)
                    {
                        if (t > 0)
                        {
                            sb.Append("; ");
                        }
                        sb.Append(message.ToolCalls[t].ToolName)
                          .Append('(')
                          .Append(message.ToolCalls[t].ArgumentsJson)
                          .Append(')');
                    }
                    sb.Append(']');
                }
            }

            return sb.ToString();
        }

        /// <summary>One line per finding, severity-prefixed. For logs and quick dumps.</summary>
        public static string RenderFindings(List<Diagnostic> findings)
        {
            if (findings == null || findings.Count == 0)
            {
                return "no findings -- the configuration looks deliberate.";
            }

            StringBuilder sb = new StringBuilder(findings.Count * 64);
            for (int i = 0; i < findings.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(findings[i].ToString());
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- internals

        private static string RoleLabel(LlmRole role)
        {
            switch (role)
            {
                case LlmRole.System: return "system";
                case LlmRole.User: return "user";
                case LlmRole.Assistant: return "assistant";
                case LlmRole.Tool: return "tool";
                default: return role.ToString().ToLowerInvariant();
            }
        }

        // The same arithmetic the grounding layer uses -- called through the closed
        // generic base rather than copied -- so the preview's numbers agree with
        // the context budget's by construction, not by discipline.
        private static int Estimate(LlmMessage message)
        {
            if (message == null || message.Content == null)
            {
                return 0;
            }
            return ContextSection.EstimateTokens(message.Content);
        }
    }
}
