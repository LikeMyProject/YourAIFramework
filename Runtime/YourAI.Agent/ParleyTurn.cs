using System;
using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Agent
{
    /// <summary>
    /// A parley: every role answers the same prompt independently, without seeing the
    /// others. A council, a panel of experts, a same-input A/B of two personas.
    ///
    /// This is where the polling design pays for itself. All the agents are in flight at
    /// once, each advancing one slice per pump, so a parley of five costs roughly one
    /// agent's latency instead of five -- with no threads, no Task, and no synchronization
    /// to get wrong, because there is still exactly one thread touching this object.
    ///
    /// A missing role does not sink the parley. The step is recorded as failed and the
    /// rest of the council still speaks; a host that configured eleven of twelve agents
    /// should get eleven opinions plus one clear error, not silence.
    ///
    /// Ordering note: <c>Steps</c> is start order, while <c>StepFinished</c> fires in
    /// completion order. That mirrors the tool scheduler deliberately -- what a UI shows
    /// follows reality, what a log or an evaluation reads stays stable.
    /// </summary>
    internal sealed class ParleyTurn : TeamTurnBase
    {
        private sealed class Entry
        {
            public IAgentTurn Turn;
            public TeamStep Step;
            public bool Done;
        }

        private readonly AgentTeam _team;
        private readonly string[] _roles;
        private readonly string _actorId;
        private readonly object _worldState;
        private readonly string _input;
        private readonly List<Entry> _entries = new List<Entry>(4);
        private readonly HashSet<int> _advancedThisPump = new HashSet<int>();

        private bool _started;

        public ParleyTurn(AgentTeam team, string[] roles, string actorId, string userMessage, object worldState)
            : base(TeamTurnMode.Parley)
        {
            _team = team;
            _roles = roles;
            _actorId = actorId;
            _worldState = worldState;
            _input = userMessage;
        }

        public override bool Pump()
        {
            if (IsDone)
            {
                return false;
            }

            if (!_started)
            {
                _started = true;
                if (!StartAll())
                {
                    return false;
                }
            }

            _advancedThisPump.Clear();

            while (true)
            {
                bool progressed = AdvanceAll();

                if (AllSettled())
                {
                    Finish();
                    return false;
                }

                if (!progressed)
                {
                    break;
                }
            }

            return !IsDone;
        }

        public override void Cancel()
        {
            if (IsDone)
            {
                return;
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (entry.Done)
                {
                    continue;
                }

                entry.Turn.Cancel();
                entry.Done = true;
                entry.Step.Success = false;
                entry.Step.Error = "cancelled";
                AnnounceStep(entry.Step);
            }

            CancelWith("cancelled");
        }

        private bool StartAll()
        {
            if (_roles == null || _roles.Length == 0)
            {
                Fail("a parley needs at least one role");
                return false;
            }

            int started = 0;

            for (int i = 0; i < _roles.Length; i++)
            {
                string role = _roles[i];

                AiAgent agent;
                if (!_team.TryGet(role, out agent) || agent == null)
                {
                    TeamStep missing = new TeamStep
                    {
                        Role = role,
                        Input = _input,
                        Success = false,
                        Error = "no agent is registered for role '" + role + "'",
                    };
                    RegisterStep(missing);
                    AnnounceStep(missing);
                    continue;
                }

                IAgentTurn turn = agent.BeginTurn(_actorId, _input, _worldState);
                TeamStep step = new TeamStep { Role = role, Input = _input };
                RegisterStep(step);

                Entry entry = new Entry { Turn = turn, Step = step };
                _entries.Add(entry);
                turn.Delta += MakeDeltaHandler(role);
                started++;
            }

            if (started == 0)
            {
                Fail("no role in the parley had a registered agent");
                return false;
            }

            return true;
        }

        private bool AdvanceAll()
        {
            bool any = false;

            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (entry.Done || _advancedThisPump.Contains(i))
                {
                    continue;
                }

                _advancedThisPump.Add(i);
                any = true;

                entry.Turn.Pump();

                if (entry.Turn.IsDone)
                {
                    entry.Done = true;
                    Fill(entry);
                    AnnounceStep(entry.Step);
                }
            }

            return any;
        }

        private void Fill(Entry entry)
        {
            AgentTurnResult outcome = entry.Turn.Result;
            TeamStep step = entry.Step;

            step.Success = outcome != null && outcome.Success;
            step.Text = outcome != null ? outcome.Text : null;
            step.Error = outcome != null ? outcome.Error : "the agent produced no result";
            step.ElapsedMs = outcome != null ? outcome.ElapsedMs : 0;
        }

        private bool AllSettled()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!_entries[i].Done)
                {
                    return false;
                }
            }
            return true;
        }

        private void Finish()
        {
            // The answer is the first contributor's, in role order. Picking the last
            // would make the outcome depend on who happened to reply fastest, which is a
            // scheduling detail leaking into the game's narrative.
            string first = null;

            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Step.Success && !string.IsNullOrEmpty(_entries[i].Step.Text))
                {
                    first = _entries[i].Step.Text;
                    break;
                }
            }

            if (first == null)
            {
                Fail("no agent in the parley produced an answer");
                return;
            }

            Succeed(first);
        }

        private Action<string> MakeDeltaHandler(string role)
        {
            return delegate (string delta) { Publish(role, delta); };
        }
    }
}
