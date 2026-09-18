using System;
using System.Collections.Generic;
using YourAI.Core.Contracts;

namespace YourAI.Agent
{
    /// <summary>
    /// The default tool registry: a name-indexed set that can be changed at runtime.
    ///
    /// Lookup is case-insensitive. That is not laxity -- models capitalise, pluralise,
    /// and occasionally re-case a tool name they were shown once, and failing the call
    /// over that teaches the model nothing while costing a turn. Two tools whose names
    /// differ only in case are a host mistake, and the later registration wins.
    ///
    /// <see cref="All"/> returns registration order rather than dictionary order. The
    /// tools array goes into the prompt, and a set that reshuffles between turns makes
    /// every prompt a cache miss and every diff unreadable.
    /// </summary>
    public sealed class ToolRegistry : IToolRegistry
    {
        private readonly Dictionary<string, ITool> _byName =
            new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);

        private readonly List<ITool> _ordered = new List<ITool>(8);

        public int Count
        {
            get { return _ordered.Count; }
        }

        public IReadOnlyList<ITool> All
        {
            get { return _ordered; }
        }

        public void Register(ITool tool)
        {
            if (tool == null || string.IsNullOrEmpty(tool.Name))
            {
                return;
            }

            ITool existing;
            if (_byName.TryGetValue(tool.Name, out existing))
            {
                // Replace in place so ordering survives a hot swap; the prompt the
                // model sees should not reorder because a tool was reconfigured.
                int at = _ordered.IndexOf(existing);
                if (at >= 0)
                {
                    _ordered[at] = tool;
                }
                else
                {
                    _ordered.Add(tool);
                }
            }
            else
            {
                _ordered.Add(tool);
            }

            _byName[tool.Name] = tool;
        }

        public bool Unregister(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            ITool tool;
            if (!_byName.TryGetValue(name, out tool))
            {
                return false;
            }

            _byName.Remove(name);
            _ordered.Remove(tool);
            return true;
        }

        public bool TryGet(string name, out ITool tool)
        {
            if (string.IsNullOrEmpty(name))
            {
                tool = null;
                return false;
            }
            return _byName.TryGetValue(name, out tool);
        }

        public void Clear()
        {
            _byName.Clear();
            _ordered.Clear();
        }

        /// <summary>
        /// Snapshots the registry into the shape a request advertises. Returns null
        /// when there is nothing to advertise, so the caller can omit the tools key
        /// entirely rather than send an empty array.
        /// </summary>
        public List<ToolDefinition> BuildDefinitions()
        {
            if (_ordered.Count == 0)
            {
                return null;
            }

            List<ToolDefinition> definitions = new List<ToolDefinition>(_ordered.Count);
            for (int i = 0; i < _ordered.Count; i++)
            {
                ToolDefinition definition = ToolDefinition.From(_ordered[i]);
                if (definition != null)
                {
                    definitions.Add(definition);
                }
            }

            return definitions.Count > 0 ? definitions : null;
        }

        /// <summary>Names only, for the validator input.</summary>
        public List<string> BuildNames()
        {
            if (_ordered.Count == 0)
            {
                return null;
            }

            List<string> names = new List<string>(_ordered.Count);
            for (int i = 0; i < _ordered.Count; i++)
            {
                if (!string.IsNullOrEmpty(_ordered[i].Name))
                {
                    names.Add(_ordered[i].Name);
                }
            }

            return names.Count > 0 ? names : null;
        }
    }
}
