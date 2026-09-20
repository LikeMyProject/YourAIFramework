using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace YourAI.Agent
{
    /// <summary>How a team turn distributes the work.</summary>
    public enum TeamTurnMode
    {
        /// <summary>
        /// Sequential handoff. Each agent receives the previous one's answer as its
        /// input, so a blacksmith can hand a finished blade to the merchant who prices it.
        /// </summary>
        Relay = 0,

        /// <summary>
        /// Concurrent. Every agent answers the same prompt independently, without seeing
        /// the others -- the shape of a council, a panel, or an A/B of two personas.
        /// </summary>
        Parley = 1,
    }

    /// <summary>One agent's contribution to a team turn.</summary>
    public sealed class TeamStep
    {
        /// <summary>The role that produced this step.</summary>
        public string Role;

        /// <summary>What this agent was given -- the player's line, or the upstream answer.</summary>
        public string Input;

        /// <summary>What this agent said. Null when the step failed before producing text.</summary>
        public string Text;

        public bool Success;

        public string Error;

        public double ElapsedMs;

        public override string ToString()
        {
            if (!Success)
            {
                return Role + ": failed(" + Error + ")";
            }
            return Role + ": " + (Text != null && Text.Length > 40 ? Text.Substring(0, 40) + "..." : Text);
        }
    }

    /// <summary>Everything a finished team turn produced.</summary>
    public sealed class TeamTurnResult
    {
        public bool Success;

        public string Error;

        /// <summary>
        /// The answer to act on. In a relay that is the last agent's text; in a parley
        /// the first, because there is no "last" that means anything.
        /// </summary>
        public string Text;

        /// <summary>Every step, in start order.</summary>
        public List<TeamStep> Steps;

        public TeamTurnMode Mode;

        public double ElapsedMs;

        /// <summary>How many agents contributed successfully.</summary>
        public int Succeeded;

        public override string ToString()
        {
            if (!Success)
            {
                return "failed " + Error;
            }
            return "ok mode=" + Mode + " steps=" + (Steps != null ? Steps.Count : 0)
                 + " ok=" + Succeeded + " ms=" + ElapsedMs.ToString("F0");
        }
    }

    /// <summary>
    /// A multi-agent exchange, driven by <see cref="Pump"/> exactly like a single turn.
    ///
    /// The reason this is a type of its own rather than a list of turns the host loops
    /// over is that orchestration has invariants of its own. A failed step must stop a
    /// relay rather than feed the error downstream as if it were an answer, and a cancel
    /// must reach whichever agent is currently thinking. Both are easy to get wrong when
    /// the loop lives in the host, and impossible to get wrong when it does not.
    /// </summary>
    public interface ITeamTurn
    {
        TeamTurnMode Mode { get; }

        bool IsDone { get; }

        /// <summary>Text so far from whoever is speaking. Empty before any answer lands.</summary>
        string Text { get; }

        /// <summary>Steps so far, in start order. In a relay this matches role order.</summary>
        IReadOnlyList<TeamStep> Steps { get; }

        /// <summary>Non-null only once the turn is done.</summary>
        TeamTurnResult Result { get; }

        /// <summary>Fired as each agent finishes, tagged with its role.</summary>
        event Action<TeamStep> StepFinished;

        /// <summary>Incremental text, tagged with the role it came from.</summary>
        event Action<string, string> Delta;

        /// <summary>Advances the turn. Returns true while more work remains.</summary>
        bool Pump();

        /// <summary>Abandons the turn, including whichever agent is mid-request.</summary>
        void Cancel();
    }

    /// <summary>
    /// Shared machinery for both modes: the step log, the outcome object, the events and
    /// the clock. Subclasses decide only who runs when.
    ///
    /// Steps are registered before their agent starts and announced when it lands. That
    /// split matters: it is what lets a failed step still be in the log together with the
    /// input that caused it, which is the case a host most needs to see.
    ///
    /// Succeed and Fail are one-shot. A turn that reported twice would double-count in a
    /// host's tally, and the first outcome is the true one anyway -- everything after it
    /// happened to a turn that was already over.
    /// </summary>
    public abstract class TeamTurnBase : ITeamTurn
    {
        private readonly List<TeamStep> _steps = new List<TeamStep>(4);
        private readonly Stopwatch _clock = new Stopwatch();
        private TeamTurnResult _result;

        protected TeamTurnBase(TeamTurnMode mode)
        {
            Mode = mode;
            _clock.Start();
        }

        public TeamTurnMode Mode { get; private set; }

        public bool IsDone
        {
            get { return _result != null; }
        }

        public string Text
        {
            get { return _result != null && _result.Text != null ? _result.Text : string.Empty; }
        }

        public IReadOnlyList<TeamStep> Steps
        {
            get { return _steps; }
        }

        public TeamTurnResult Result
        {
            get { return _result; }
        }

        public event Action<TeamStep> StepFinished;
        public event Action<string, string> Delta;

        public abstract bool Pump();

        public virtual void Cancel()
        {
            if (IsDone)
            {
                return;
            }
            CancelWith("cancelled");
        }

        /// <summary>Opens a slot for a step before its agent starts.</summary>
        protected void RegisterStep(TeamStep step)
        {
            _steps.Add(step);
        }

        /// <summary>Announces a step whose fields are now final.</summary>
        protected void AnnounceStep(TeamStep step)
        {
            Action<TeamStep> handler = StepFinished;
            if (handler != null)
            {
                handler(step);
            }
        }

        protected void Publish(string role, string delta)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }

            Action<string, string> handler = Delta;
            if (handler != null)
            {
                handler(role, delta);
            }
        }

        /// <summary>Finishes successfully, taking the answer from the last successful step.</summary>
        protected void Succeed()
        {
            Succeed(null);
        }

        /// <summary>Finishes successfully with an explicit answer. Null falls back to the last success.</summary>
        protected void Succeed(string text)
        {
            if (_result != null)
            {
                return;
            }

            _clock.Stop();
            _result = new TeamTurnResult
            {
                Success = true,
                Text = text != null ? text : LastText(),
                Steps = _steps,
                Mode = Mode,
                ElapsedMs = _clock.Elapsed.TotalMilliseconds,
                Succeeded = CountSucceeded(),
            };
        }

        protected void Fail(string error)
        {
            if (_result != null)
            {
                return;
            }

            _clock.Stop();
            _result = new TeamTurnResult
            {
                Success = false,
                Error = string.IsNullOrEmpty(error) ? "team turn failed" : error,
                Text = LastText(),
                Steps = _steps,
                Mode = Mode,
                ElapsedMs = _clock.Elapsed.TotalMilliseconds,
                Succeeded = CountSucceeded(),
            };
        }

        protected void CancelWith(string reason)
        {
            if (_result != null)
            {
                return;
            }

            _clock.Stop();
            _result = new TeamTurnResult
            {
                Success = false,
                Error = string.IsNullOrEmpty(reason) ? "cancelled" : reason,
                Text = LastText(),
                Steps = _steps,
                Mode = Mode,
                ElapsedMs = _clock.Elapsed.TotalMilliseconds,
                Succeeded = CountSucceeded(),
            };
        }

        /// <summary>The most recent text worth acting on, or null if there is none.</summary>
        protected string LastText()
        {
            for (int i = _steps.Count - 1; i >= 0; i--)
            {
                if (_steps[i].Success && !string.IsNullOrEmpty(_steps[i].Text))
                {
                    return _steps[i].Text;
                }
            }
            return null;
        }

        private int CountSucceeded()
        {
            int n = 0;
            for (int i = 0; i < _steps.Count; i++)
            {
                if (_steps[i].Success)
                {
                    n++;
                }
            }
            return n;
        }
    }
}
