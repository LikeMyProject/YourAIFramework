using System.Collections.Generic;
using System.Text;
using YourAI.Core.Contracts;

namespace YourAI.Core.Ai
{
    /// <summary>
    /// Holds whatever AI service the application currently has, and makes sure the
    /// answer is never "none" and never silent.
    ///
    /// Two jobs, and the second is the one that gets skipped in practice:
    ///
    /// 1. Guarantee a usable object. Every read goes through <see cref="Service"/>,
    ///    which is never null, so callers stay free of defensive code.
    ///
    /// 2. Announce the degraded state. A project with no API key still runs, but it
    ///    must not fail quietly. The first time someone wonders why the NPC is mute,
    ///    the console should already say why -- degradation that is invisible gets
    ///    diagnosed as a bug in the AI layer rather than as absent configuration.
    ///
    /// Installation is swappable at runtime, so a player who enters a key mid-session
    /// can have AI come online without a restart.
    /// </summary>
    public sealed class AiServiceGate
    {
        private readonly IAiDiagnostics _diagnostics;
        private readonly List<string> _installedFrom = new List<string>();
        private IAiService _service;

        public AiServiceGate()
            : this(null, null, null)
        {
        }

        public AiServiceGate(IAiService service)
            : this(service, null, null)
        {
        }

        public AiServiceGate(IAiService service, IAiDiagnostics diagnostics)
            : this(service, diagnostics, null)
        {
        }

        public AiServiceGate(IAiService service, IAiDiagnostics diagnostics, string installedFrom)
        {
            _diagnostics = diagnostics ?? ConsoleDiagnostics.Instance;
            Install(service, installedFrom ?? "constructor");
        }

        /// <summary>The current service. Never null, even before anything is installed.</summary>
        public IAiService Service
        {
            get { return _service; }
        }

        /// <summary>
        /// True only when a real provider is configured. This is a routing hint for
        /// optional UI, not a gate: callers may use AI unconditionally.
        /// </summary>
        public bool IsAiAvailable
        {
            get { return _service != null && _service.IsAvailable; }
        }

        /// <summary>One-sentence explanation of the current state. Never empty.</summary>
        public string Status
        {
            get
            {
                if (_service == null)
                {
                    return "No AI service installed.";
                }
                if (!_service.IsAvailable)
                {
                    return "AI inactive: " + _service.UnavailableReason;
                }
                StringBuilder sb = new StringBuilder();
                sb.Append("AI active via ");
                IReadOnlyList<string> names = _service.ProviderNames;
                if (names == null || names.Count == 0)
                {
                    sb.Append("(unnamed provider)");
                }
                else
                {
                    for (int i = 0; i < names.Count; i++)
                    {
                        if (i > 0) { sb.Append(", "); }
                        sb.Append(names[i]);
                    }
                }
                return sb.ToString();
            }
        }

        /// <summary>How many times a service has been installed, and from where.</summary>
        public IReadOnlyList<string> InstallHistory
        {
            get { return _installedFrom; }
        }

        /// <summary>
        /// Swaps in a service. Passing null reverts to the null object rather than to
        /// nothing, so the invariant holds through every transition.
        /// </summary>
        public void Install(IAiService service, string source)
        {
            _service = service ?? NullAiService.Instance;
            _installedFrom.Add(source ?? "unspecified");

            if (_service.IsAvailable)
            {
                _diagnostics.Info(Status);
            }
            else
            {
                _diagnostics.Warn(
                    "AI module is inactive -- " + _service.UnavailableReason + "\n"
                    + "The rest of the framework is unaffected. AI calls return a faulted "
                    + "handle describing this reason; they do not throw and do not need a "
                    + "null check.");
            }
        }

        public void Install(IAiService service)
        {
            Install(service, null);
        }
    }
}
