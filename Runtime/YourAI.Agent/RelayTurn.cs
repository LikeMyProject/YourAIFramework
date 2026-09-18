using System;
using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Agent
{
    /// <summary>
    /// A relay: the roles run one after another, and each agent's answer becomes the
    /// next agent's input.
    ///
    /// The invariant worth stating out loud is that a failed step ends the relay. It is
    /// tempting to pass the error downstream so the chain "completes", but the next agent
    /// has no way to tell an error string from an answer -- it was told this is what the
    /// previous expert said. A merchant handed "cannot be undone" as if it were a price
    /// will confidently quote something, and the real fault is now three agents away from
    /// where it is reported.
    ///
    /// One pump can carry the whole chain when agents answer immediately; the loop exists
    /// so a fast agent does not cost a wasted frame.
    /// </summary>
    internal sealed class RelayTurn : TeamTurnBase
    {
        private readonly AgentTeam _team;
        private readonly string[] _roles;
        private readonly string _actorId;
        private readonly object _worldState;
        private readonly string _originalInput;

        private int _index = -1;
        private IAgentTurn _current;
        private TeamStep _pending;

        public RelayTurn(AgentTeam team, string[] roles, string actorId, string userMessage, object worldState)
            : base(TeamTurnMode.Relay)
        {
            _team = team;
            _roles = roles;
            _actorId = actorId;
            _worldState = worldState;
            _originalInput = userMessage;
        }

        public override bool Pump()
        {
            if (IsDone)
            {
                return false;
            }

            if (_roles == null || _roles.Length == 0)
            {
                Fail("a relay needs at least one role");
                return false;
            }

            while (true)
            {
                if (_current == null)
                {
                    _index++;
                    if (_index >= _roles.Length)
                    {
                        Succeed();
                        return false;
                    }

                    if (!StartStep())
                    {
                        return false;
                    }
                }

                _current.Pump();

                if (!_current.IsDone)
                {
                    return true;
                }

                if (!FinishStep())
                {
                    return false;
                }

                _current = null;
            }
        }

        public override void Cancel()
        {
            if (IsDone)
            {
                return;
            }

            if (_current != null)
            {
                _current.Cancel();
            }

            CancelWith("cancelled");
        }

        private bool StartStep()
        {
            string role = _roles[_index];

            AiAgent agent;
            if (!_team.TryGet(role, out agent) || agent == null)
            {
                Fail("no agent is registered for role '" + role + "'");
                return false;
            }

            string input = _index == 0
                ? _originalInput
                : _team.FormatHandoff(_roles[_index - 1], _steps[_index - 1].Text);

            TeamStep step = new TeamStep { Role = role, Input = input };
            RegisterStep(step);
            _pending = step;

            IAgentTurn turn = agent.BeginTurn(_actorId, input, _worldState);
            if (turn == null)
            {
                step.Error = "agent '" + role + "' returned no turn";
                Fail("agent '" + role + "' returned no turn");
                return false;
            }

            _current = turn;
            _current.Delta += MakeDeltaHandler(role);
            return true;
        }

        private bool FinishStep()
        {
            AgentTurnResult outcome = _current.Result;
            TeamStep step = _pending;
            _pending = null;

            if (step == null)
            {
                Fail("the relay lost track of the step in flight");
                return false;
            }

            step.Success = outcome != null && outcome.Success;
            step.Text = outcome != null ? outcome.Text : null;
            step.Error = outcome != null ? outcome.Error : "the agent produced no result";
            step.ElapsedMs = outcome != null ? outcome.ElapsedMs : 0;

            AnnounceStep(step);

            if (!step.Success)
            {
                // Stop here rather than hand the error to the next agent. See the class
                // comment: downstream agents cannot tell a failure from a contribution.
                Fail("role '" + step.Role + "' failed: " + step.Error);
                return false;
            }

            return true;
        }

        /// <summary>Captures the role by value so the handler never reports the wrong speaker.</summary>
        private Action<string> MakeDeltaHandler(string role)
        {
            return delegate (string delta) { Publish(role, delta); };
        }

        private IReadOnlyList<TeamStep> _steps
        {
            get { return Steps; }
        }
    }
}
