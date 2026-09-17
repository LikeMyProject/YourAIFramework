using System;
using System.Collections.Generic;
using YourAI.Agent;
using YourAI.Core.Contracts;

namespace YourAI.Presentation
{
    /// <summary>How much a finding should worry the reader.</summary>
    public enum DiagnosticSeverity
    {
        /// <summary>Worth reading once; the agent behaves as intended.</summary>
        Info = 0,

        /// <summary>The agent will work, but with a degraded or surprising edge.</summary>
        Warning = 1,

        /// <summary>Something is missing that will make every turn fail.</summary>
        Error = 2,
    }

    /// <summary>One finding about an agent's configuration. A value, never a throw.</summary>
    public sealed class Diagnostic
    {
        public DiagnosticSeverity Severity;

        /// <summary>One or two sentences. A finding that needs a manual is a bug.</summary>
        public string Message;

        public override string ToString()
        {
            return Severity.ToString().ToLowerInvariant() + ": " + Message;
        }
    }

    /// <summary>
    /// Reads one agent's configuration and reports what a designer would otherwise
    /// discover in playtest.
    ///
    /// This is the substance behind the Editor settings window, and it lives in the
    /// engine-free Presentation assembly on purpose: the rules ("tools registered
    /// but no rounds to run them", "schema declared but nothing enforces it") are
    /// game-agnostic knowledge about the agent layer, so they are unit-tested in
    /// the offline harness and the Unity window is a thin renderer over them.
    ///
    /// The severity choices follow the framework's no-key stance rather than
    /// fighting it: a missing API key is a Warning with the provider's own reason,
    /// not an Error, because the intended behaviour under no key is that turns
    /// come back faulted and the game runs. Errors are reserved for wiring that
    /// makes every turn fail even though the host believes it is configured.
    /// </summary>
    public static class AgentDiagnostics
    {
        /// <summary>
        /// Inspects an agent. Null is tolerated and reported as a finding, matching
        /// how the rest of the framework treats absence.
        /// </summary>
        public static List<Diagnostic> Inspect(AiAgent agent)
        {
            List<Diagnostic> findings = new List<Diagnostic>(8);

            if (agent == null)
            {
                findings.Add(Make(DiagnosticSeverity.Error, "no agent to inspect."));
                return findings;
            }

            // --- the inference source --------------------------------------
            ILlmProvider provider = agent.Provider;
            if (provider == null)
            {
                findings.Add(Make(DiagnosticSeverity.Error,
                    "'" + agent.Name + "' has no provider wired; every turn fails with 'no provider'."));
            }
            else if (!provider.IsAvailable)
            {
                findings.Add(Make(DiagnosticSeverity.Warning,
                    "the provider '" + provider.Name + "' is unavailable: " + provider.UnavailableReason));
            }

            // --- the persona ------------------------------------------------
            if (string.IsNullOrEmpty(agent.SystemPrompt))
            {
                findings.Add(Make(DiagnosticSeverity.Info,
                    "no persona is set; the character speaks with the world context alone."));
            }

            // --- tools -------------------------------------------------------
            int toolCount = CountTools(agent.Tools);
            if (toolCount > 0 && agent.MaxToolRounds <= 0)
            {
                findings.Add(Make(DiagnosticSeverity.Warning,
                    "'" + agent.Name + "' has " + toolCount + " tool(s) registered but MaxToolRounds is 0; "
                    + "the model can ask, the framework will never run any."));
            }
            if (toolCount > 0)
            {
                findings.Add(Make(DiagnosticSeverity.Info,
                    toolCount + " tool(s) registered: " + ToolNames(agent.Tools) + "."));
            }

            // --- memory -------------------------------------------------------
            if (agent.AutoMemory && agent.Memory == null)
            {
                findings.Add(Make(DiagnosticSeverity.Info,
                    "AutoMemory is on but no memory store is attached; exchanges are replayed in-session and never persisted."));
            }

            // --- output policy --------------------------------------------------
            if (agent.Validators == null || agent.Validators.Validators.Count == 0)
            {
                findings.Add(Make(DiagnosticSeverity.Info,
                    "no output validators; whatever the model says reaches the game as-is."));
                if (!string.IsNullOrEmpty(agent.ExpectedSchemaJson))
                {
                    findings.Add(Make(DiagnosticSeverity.Warning,
                        "ExpectedSchemaJson is set but no validators are installed; nothing enforces it."));
                }
            }

            // --- sampling knobs ----------------------------------------------
            if (agent.Temperature < 0.0 || agent.Temperature > 2.0)
            {
                findings.Add(Make(DiagnosticSeverity.Warning,
                    "Temperature " + agent.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " is outside [0, 2]; most vendors clamp or reject it."));
            }

            if (agent.MaxTokens < 0)
            {
                findings.Add(Make(DiagnosticSeverity.Warning,
                    "MaxTokens is negative; the field means 'vendor default' when zero."));
            }

            return findings;
        }

        /// <summary>How many findings carry the given severity. The shape a window's badge wants.</summary>
        public static int Count(List<Diagnostic> findings, DiagnosticSeverity severity)
        {
            if (findings == null)
            {
                return 0;
            }

            int total = 0;
            for (int i = 0; i < findings.Count; i++)
            {
                if (findings[i].Severity == severity)
                {
                    total++;
                }
            }
            return total;
        }

        // ---------------------------------------------------------------- internals

        private static Diagnostic Make(DiagnosticSeverity severity, string message)
        {
            return new Diagnostic { Severity = severity, Message = message };
        }

        private static int CountTools(IToolRegistry tools)
        {
            return tools != null && tools.All != null ? tools.All.Count : 0;
        }

        private static string ToolNames(IToolRegistry tools)
        {
            List<ITool> all = tools.All as List<ITool>;
            if (all == null)
            {
                List<string> names = new List<string>(tools.All.Count);
                for (int i = 0; i < tools.All.Count; i++)
                {
                    names.Add(tools.All[i].Name);
                }
                return string.Join(", ", names.ToArray());
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
            for (int i = 0; i < all.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(all[i].Name);
            }
            return sb.ToString();
        }
    }
}
