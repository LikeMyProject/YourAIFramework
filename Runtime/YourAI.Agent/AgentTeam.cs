using System;
using System.Collections.Generic;

namespace YourAI.Agent
{
    /// <summary>
    /// A cast of agents, addressed by role.
    ///
    /// The framework has always allowed several agents to exist -- each <see cref="AiAgent"/>
    /// is independent and owns its own conversation -- but nothing tied them together or
    /// described how one should follow another. This is that missing piece and nothing
    /// more: a registry plus two ways to run a group.
    ///
    /// Roles are matched case-insensitively for the same reason tool names are. A host
    /// that writes "Smith" once and "smith" later means the same person, and failing on
    /// the difference would be a bug report about the framework rather than about the game.
    ///
    /// Registration order is preserved because it is the default relay order, and a team
    /// that reshuffled between runs would make every replay a different story.
    /// </summary>
    public sealed class AgentTeam
    {
        private readonly Dictionary<string, AiAgent> _byRole =
            new Dictionary<string, AiAgent>(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _order = new List<string>(4);

        public AgentTeam(string name)
        {
            Name = string.IsNullOrEmpty(name) ? "team" : name;
        }

        public string Name { get; private set; }

        /// <summary>
        /// Rewrites an upstream answer into the next agent's input.
        ///
        /// The default prefixes the speaker's role, and that default is not cosmetic: an
        /// agent handed a bare sentence will assume the player said it, and will answer
        /// the player instead of building on the previous expert's work.
        ///
        /// A callback that throws falls back to the default rather than failing the relay.
        /// Formatting an input is not worth losing a turn over.
        /// </summary>
        public Func<string, string, string> Handoff;

        public int Count
        {
            get { return _order.Count; }
        }

        /// <summary>Registered roles, in registration order.</summary>
        public IReadOnlyList<string> Roles
        {
            get { return _order; }
        }

        public void Register(string role, AiAgent agent)
        {
            if (string.IsNullOrEmpty(role) || agent == null)
            {
                return;
            }

            if (!_byRole.ContainsKey(role))
            {
                _order.Add(role);
            }

            _byRole[role] = agent;
        }

        public bool Unregister(string role)
        {
            if (string.IsNullOrEmpty(role))
            {
                return false;
            }

            if (!_byRole.Remove(role))
            {
                return false;
            }

            // The lookup is case-insensitive but _order holds the spelling the host
            // registered with, so removing by the caller's spelling would miss "Smith"
            // when asked for "SMITH" -- leaving the role gone from the map but still
            // present in Roles, which is exactly the kind of half-removed state that
            // makes a "why is this agent still being called" bug hard to see.
            for (int i = 0; i < _order.Count; i++)
            {
                if (string.Equals(_order[i], role, StringComparison.OrdinalIgnoreCase))
                {
                    _order.RemoveAt(i);
                    break;
                }
            }

            return true;
        }

        public bool TryGet(string role, out AiAgent agent)
        {
            if (string.IsNullOrEmpty(role))
            {
                agent = null;
                return false;
            }

            return _byRole.TryGetValue(role, out agent);
        }

        public void Clear()
        {
            _byRole.Clear();
            _order.Clear();
        }

        // ------------------------------------------------------------------- running

        /// <summary>
        /// Runs the given roles in sequence, each receiving the previous answer.
        /// Returns a turn even when the roles are unusable, so the caller always has
        /// something to pump and an error to read.
        /// </summary>
        public ITeamTurn BeginRelay(string[] roles, string actorId, string userMessage, object worldState)
        {
            return new RelayTurn(this, roles, actorId, userMessage, worldState);
        }

        /// <summary>Relay across every registered role, in registration order.</summary>
        public ITeamTurn BeginRelayAll(string actorId, string userMessage, object worldState)
        {
            return new RelayTurn(this, _order.ToArray(), actorId, userMessage, worldState);
        }

        /// <summary>Runs the given roles concurrently, each answering the same prompt.</summary>
        public ITeamTurn BeginParley(string[] roles, string actorId, string userMessage, object worldState)
        {
            return new ParleyTurn(this, roles, actorId, userMessage, worldState);
        }

        /// <summary>Parley across every registered role, in registration order.</summary>
        public ITeamTurn BeginParleyAll(string actorId, string userMessage, object worldState)
        {
            return new ParleyTurn(this, _order.ToArray(), actorId, userMessage, worldState);
        }

        internal string FormatHandoff(string fromRole, string text)
        {
            Func<string, string, string> handler = Handoff;
            if (handler != null)
            {
                try
                {
                    string custom = handler(fromRole, text);
                    if (!string.IsNullOrEmpty(custom))
                    {
                        return custom;
                    }
                }
                catch (Exception)
                {
                    // Fall through to the default: a broken formatter must not cost a turn.
                }
            }

            return fromRole + "说：" + (text ?? string.Empty);
        }
    }
}
