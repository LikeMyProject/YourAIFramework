using System;
using System.Collections.Generic;
using YourAI.Core.Contracts;
using YourAI.Grounding;

namespace YourAI.Agent
{
    /// <summary>
    /// One LLM-driven character: a provider, some context, some memory, some tools,
    /// and the policy that connects them.
    ///
    /// What this class is responsible for, and what it deliberately is not:
    ///
    /// It owns the conversation window and the turn lifecycle. It assembles the
    /// prompt, runs the request, executes whatever tools the model asks for, feeds
    /// the results back, validates the final output, and records the exchange.
    ///
    /// It does not decide what the character can see (that is the registered context
    /// providers), what it can do (the tool registry), or what it is allowed to say
    /// (the validators). All three are injected. That is the practical meaning of
    /// "game-agnostic": the framework knows how to run a turn, the game knows what a
    /// turn is about.
    ///
    /// The default configuration is a working agent. A host that sets nothing but a
    /// provider and a system prompt gets a functioning conversation; every other knob
    /// exists because some game needed it.
    /// </summary>
    public sealed class AiAgent
    {
        private readonly List<LlmMessage> _conversation = new List<LlmMessage>(16);
        private string _lastRenderedContext = string.Empty;

        public AiAgent(string name, ILlmProvider provider)
        {
            Name = string.IsNullOrEmpty(name) ? "agent" : name;
            Provider = provider;
        }

        /// <summary>Stable identifier, used in diagnostics and memory records.</summary>
        public string Name { get; private set; }

        /// <summary>
        /// The inference source. Null is tolerated at construction and reported as a
        /// failed turn, because an agent that crashes on a missing provider is worse
        /// than one that says it has nothing to say.
        /// </summary>
        public ILlmProvider Provider { get; set; }

        /// <summary>Collects prompt material from registered players of the world state.</summary>
        public ContextAssembler Context { get; private set; } = new ContextAssembler();

        /// <summary>Long-term storage. Optional; null disables automatic writes.</summary>
        public IMemoryStore Memory { get; set; }

        /// <summary>What the character may ask for. Optional; null sends no tools.</summary>
        public IToolRegistry Tools { get; set; }

        /// <summary>The enforcement point for output policy.</summary>
        public ValidatorChain Validators { get; private set; } = new ValidatorChain();

        /// <summary>
        /// Consulted before an irreversible tool runs. Null means no irreversible tool
        /// ever runs -- see <see cref="AgentTurn"/> for why that is the safe default.
        /// </summary>
        public Func<ToolInvocation, ITool, bool> ToolApproval;

        // ------------------------------------------------------------------- tuning

        /// <summary>Persona and standing instructions. Rendered above assembled context.</summary>
        public string SystemPrompt;

        /// <summary>Overrides the provider's default model when non-null.</summary>
        public string Model;

        public double Temperature = 0.7;
        public int MaxTokens;

        /// <summary>Vendor hint such as "json_object"; null for free-form output.</summary>
        public string ResponseFormat;

        /// <summary>
        /// JSON Schema the output is expected to satisfy. Handed to validators through
        /// ValidationInput; the framework does not enforce it itself, because schema
        /// validation is a policy decision and policies live in validators.
        /// </summary>
        public string ExpectedSchemaJson;

        /// <summary>"auto" or "none". Null lets the vendor decide, which is "auto".</summary>
        public string ToolChoice;

        /// <summary>
        /// How many prior messages are replayed. Twelve is roughly six exchanges:
        /// enough that the character remembers the current scene, small enough that a
        /// long session does not quietly become a long prompt.
        /// </summary>
        public int MaxHistoryMessages = 12;

        /// <summary>
        /// Extra request rounds allowed for tool use. Each round costs a full
        /// inference, and a model that keeps calling tools has misunderstood the task;
        /// the cap turns a runaway loop into a finished turn with an explanation.
        /// </summary>
        public int MaxToolRounds = 4;

        /// <summary>
        /// Upper bound on tool calls in flight at once within one round. Zero means no
        /// bound, which is the right default for the common case: a model that asks for
        /// three lookups should get all three started in the same frame.
        ///
        /// Set it when a burst is expensive. A round can carry ten calls, and a game
        /// that opened ten pathfinding queries at once would deserve what it got.
        /// </summary>
        public int MaxConcurrentToolJobs;

        /// <summary>When true, a successful exchange is written to memory as episodic.</summary>
        public bool AutoMemory = true;

        /// <summary>Salience assigned to automatic memory records.</summary>
        public float AutoMemorySalience = 0.4f;

        // ---------------------------------------------------------------- inspection

        /// <summary>The replayed conversation, oldest first.</summary>
        public IReadOnlyList<LlmMessage> Conversation
        {
            get { return _conversation; }
        }

        /// <summary>Context exactly as it was rendered for the last turn. For debugging prompts.</summary>
        public string LastRenderedContext
        {
            get { return _lastRenderedContext; }
        }

        public int LastContextTokens { get; private set; }

        public int LastDroppedSections { get; private set; }

        public void ClearConversation()
        {
            _conversation.Clear();
        }

        /// <summary>Adds a message to the replay window without running a turn.</summary>
        public void Seed(LlmMessage message)
        {
            if (message != null)
            {
                _conversation.Add(message);
            }
        }

        public void Seed(string userText, string assistantText)
        {
            if (!string.IsNullOrEmpty(userText))
            {
                _conversation.Add(LlmMessage.User(userText));
            }
            if (!string.IsNullOrEmpty(assistantText))
            {
                _conversation.Add(LlmMessage.Assistant(assistantText));
            }
        }

        // ------------------------------------------------------------------ the turn

        /// <summary>
        /// Starts a turn. Must be called from the same thread that will pump it.
        ///
        /// <paramref name="actorId"/> scopes memory writes; <paramref name="worldState"/>
        /// is handed to every registered context provider and is not inspected here.
        /// </summary>
        public IAgentTurn BeginTurn(string actorId, string userMessage, object worldState)
        {
            return new AgentTurn(this, actorId, userMessage, worldState);
        }

        /// <summary>
        /// Renders exactly what the next request would contain -- persona, world
        /// context, replayed history, the user line -- without sending anything.
        ///
        /// This is the same builder the turn uses, deliberately, so a preview can
        /// never drift from what is actually sent. The side effect is that the
        /// preview refreshes LastRenderedContext and the context token counters,
        /// which is what a turn would have done anyway.
        /// </summary>
        public List<LlmMessage> PreviewMessages(object worldState, string userMessage)
        {
            return BuildMessages(worldState, userMessage);
        }

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// Builds the message list for one request: persona, assembled world context,
        /// replayed history, and the new user line.
        ///
        /// Persona and context are merged into a single system message rather than
        /// sent as two. Multiple system messages are legal in the OpenAI shape but
        /// not universally supported, and the failure mode is not an error -- some
        /// servers silently keep only the first, which would drop the world state and
        /// leave the character answering with no idea where it is.
        /// </summary>
        internal List<LlmMessage> BuildMessages(object worldState, string userMessage)
        {
            AssembledContext context = Context.Assemble(worldState);
            _lastRenderedContext = context.Render();
            LastContextTokens = context.TotalEstimatedTokens;
            LastDroppedSections = Context.LastDroppedCount;

            string system = ComposeSystem(_lastRenderedContext);

            int room = _conversation.Count + 4;
            List<LlmMessage> messages = new List<LlmMessage>(room);

            if (!string.IsNullOrEmpty(system))
            {
                messages.Add(LlmMessage.System(system));
            }

            // Trim from the front: the oldest exchanges are the least relevant, and
            // dropping them cannot break the pairing of a tool call with its result
            // because tool rounds never enter the replay window.
            int skip = _conversation.Count - MaxHistoryMessages;
            if (skip < 0)
            {
                skip = 0;
            }
            for (int i = skip; i < _conversation.Count; i++)
            {
                messages.Add(_conversation[i]);
            }

            if (!string.IsNullOrEmpty(userMessage))
            {
                messages.Add(LlmMessage.User(userMessage));
            }

            return messages;
        }

        private string ComposeSystem(string renderedContext)
        {
            bool hasPersona = !string.IsNullOrEmpty(SystemPrompt);
            bool hasContext = !string.IsNullOrEmpty(renderedContext);

            if (hasPersona && hasContext)
            {
                return SystemPrompt + "\n\n" + renderedContext;
            }
            if (hasPersona)
            {
                return SystemPrompt;
            }
            return hasContext ? renderedContext : null;
        }

        internal List<ToolDefinition> BuildToolDefinitions()
        {
            ToolRegistry registry = Tools as ToolRegistry;
            if (registry != null)
            {
                return registry.BuildDefinitions();
            }

            if (Tools == null || Tools.All == null || Tools.All.Count == 0)
            {
                return null;
            }

            // A custom registry: fall back to a generic walk. Registration order is
            // whatever the implementation reports.
            List<ToolDefinition> definitions = new List<ToolDefinition>(Tools.All.Count);
            for (int i = 0; i < Tools.All.Count; i++)
            {
                ToolDefinition definition = ToolDefinition.From(Tools.All[i]);
                if (definition != null)
                {
                    definitions.Add(definition);
                }
            }
            return definitions.Count > 0 ? definitions : null;
        }

        internal List<string> BuildToolNames()
        {
            ToolRegistry registry = Tools as ToolRegistry;
            if (registry != null)
            {
                return registry.BuildNames();
            }

            if (Tools == null || Tools.All == null || Tools.All.Count == 0)
            {
                return null;
            }

            List<string> names = new List<string>(Tools.All.Count);
            for (int i = 0; i < Tools.All.Count; i++)
            {
                ITool tool = Tools.All[i];
                if (tool != null && !string.IsNullOrEmpty(tool.Name))
                {
                    names.Add(tool.Name);
                }
            }
            return names.Count > 0 ? names : null;
        }

        /// <summary>
        /// Records a finished exchange: the replay window always, memory when enabled.
        ///
        /// Only the user line and the final answer enter the window. Tool rounds are
        /// deliberately excluded -- replaying them would multiply the prompt by the
        /// number of tools used, and their effect is already reflected in the answer.
        /// </summary>
        internal void RecordTurn(string actorId, string userMessage, string assistantText)
        {
            if (!string.IsNullOrEmpty(userMessage))
            {
                _conversation.Add(LlmMessage.User(userMessage));
            }
            if (!string.IsNullOrEmpty(assistantText))
            {
                _conversation.Add(LlmMessage.Assistant(assistantText));
            }

            if (!AutoMemory || Memory == null || string.IsNullOrEmpty(assistantText))
            {
                return;
            }

            Memory.Append(new MemoryRecord
            {
                ActorId = actorId ?? string.Empty,
                Kind = MemoryKind.Episodic,
                Text = DescribeExchange(userMessage, assistantText),
                Salience = AutoMemorySalience,
            });
        }

        private string DescribeExchange(string userMessage, string assistantText)
        {
            string them = string.IsNullOrEmpty(userMessage) ? "(no line)" : userMessage;
            return "玩家说：" + them + "\n" + Name + "答：" + assistantText;
        }
    }
}
