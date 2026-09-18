using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using YourAI.Core.Contracts;

namespace YourAI.Agent
{
    /// <summary>
    /// One conversation turn, as a state machine advanced by <see cref="Pump"/>.
    ///
    /// The shape of a turn:
    ///
    ///   Building    assemble context + history, issue the request
    ///   Streaming   consume deltas; the completion handler decides what is next
    ///   RunningTools  the model asked for something; run it, feed the results back,
    ///                 and stream again (bounded by MaxToolRounds)
    ///   Validating  deterministic policy gets the last word
    ///   Completed / Faulted / Cancelled
    ///
    /// Four decisions in here are worth defending.
    ///
    /// Irreversible tools are refused by default. When no approval callback is
    /// configured, a tool marked Irreversible returns a failure to the model instead
    /// of running. The alternative -- executing it because nobody said not to -- means
    /// the safe behaviour depends on the host having remembered to configure
    /// something, and a language model that confidently proposes deleting a save file
    /// is exactly the case where that memory matters.
    ///
    /// A refused tool is reported to the model rather than swallowed. The model can
    /// then explain, choose another route, or ask the player -- which is what a person
    /// would do. Silently dropping the call leaves it waiting for a result that never
    /// arrives.
    ///
    /// Tool calls of one round run through a <see cref="ToolBatch"/>, so several may be
    /// in flight at once. Deferred tools -- pathfinding, loading, a vendor call -- make
    /// progress on every pump instead of blocking the frame, and the turn waits for all
    /// of them without waiting for them one at a time.
    ///
    /// Tool rounds never enter the replay window, only the final answer does. Replaying
    /// them would grow the prompt by every tool call ever made in the session, and the
    /// information is already reflected in the answer the model settled on.
    /// </summary>
    internal sealed class AgentTurn : IAgentTurn
    {
        private readonly AiAgent _agent;
        private readonly string _actorId;
        private readonly string _userMessage;
        private readonly object _worldState;
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly StringBuilder _text = new StringBuilder(256);
        private readonly List<LlmMessage> _roundMessages = new List<LlmMessage>(8);
        private readonly List<ToolInvocation> _toolLog = new List<ToolInvocation>(4);

        private ILlmStreamHandle _handle;
        private ToolBatch _batch;
        private AgentTurnState _state = AgentTurnState.Building;
        private AgentTurnResult _result;

        private List<ToolInvocation> _pendingCalls;
        private string _roundText;
        private string _rawText;
        private LlmStreamResult _lastStream;
        private int _toolRounds;
        private int _toolCallsMade;

        public AgentTurn(AiAgent agent, string actorId, string userMessage, object worldState)
        {
            _agent = agent;
            _actorId = actorId;
            _userMessage = userMessage;
            _worldState = worldState;
            _clock.Start();
        }

        public AgentTurnState State
        {
            get { return _state; }
        }

        public string Text
        {
            get { return _text.ToString(); }
        }

        public bool IsDone
        {
            get
            {
                AgentTurnState state = _state;
                return state == AgentTurnState.Completed
                    || state == AgentTurnState.Faulted
                    || state == AgentTurnState.Cancelled;
            }
        }

        public AgentTurnResult Result
        {
            get { return _result; }
        }

        public event Action<string> Delta;
        public event Action<ToolInvocation, ToolResult> ToolFinished;

        public bool Pump()
        {
            if (IsDone)
            {
                return false;
            }

            if (_state == AgentTurnState.Building)
            {
                if (!StartRequest())
                {
                    return false;
                }
                SetState(AgentTurnState.Streaming);
            }

            if (_state == AgentTurnState.Streaming)
            {
                ILlmStreamHandle handle = _handle;
                if (handle == null)
                {
                    Fail("no stream handle", 0);
                    return false;
                }

                bool active = handle.Pump();

                if (_state == AgentTurnState.Streaming)
                {
                    if (!active)
                    {
                        // The stream went quiet without an outcome. OpenAiStreamHandle
                        // covers this case itself, but a custom provider might not, and
                        // a turn that never terminates is worse than one that fails.
                        Fail("stream ended without reporting an outcome", 0);
                        return false;
                    }
                    return true;
                }

                // A handler advanced the state; fall through to service it in the
                // same Pump rather than making the host wait a frame.
            }

            if (_state == AgentTurnState.RunningTools)
            {
                PumpTools();
                return !IsDone;
            }

            if (_state == AgentTurnState.Validating)
            {
                Validate();
                return false;
            }

            return !IsDone;
        }

        public void Cancel()
        {
            if (IsDone)
            {
                return;
            }

            _clock.Stop();
            if (_handle != null)
            {
                _handle.Cancel();
            }
            if (_batch != null)
            {
                // In-flight tool jobs are stopped too. A cancelled turn that left a
                // pathfinding job running would keep writing into a conversation that
                // no longer exists.
                _batch.Cancel();
            }

            _result = new AgentTurnResult
            {
                Success = false,
                Error = "cancelled",
                Text = _text.ToString(),
                ToolRounds = _toolRounds,
                ToolCallsMade = _toolCallsMade,
                ToolLog = _toolLog,
                ElapsedMs = _clock.Elapsed.TotalMilliseconds,
                LastStream = _lastStream,
            };
            SetState(AgentTurnState.Cancelled);
        }

        // ------------------------------------------------------------------ requests

        private bool StartRequest()
        {
            if (_agent.Provider == null)
            {
                Fail("agent '" + _agent.Name + "' has no provider", 0);
                return false;
            }

            List<LlmMessage> messages = _agent.BuildMessages(_worldState, _userMessage);
            for (int i = 0; i < _roundMessages.Count; i++)
            {
                messages.Add(_roundMessages[i]);
            }

            LlmRequest request = new LlmRequest
            {
                Model = _agent.Model,
                Temperature = _agent.Temperature,
                MaxTokens = _agent.MaxTokens,
                Stream = true,
                ResponseFormat = _agent.ResponseFormat,
                Messages = messages,
                Tools = _agent.BuildToolDefinitions(),
                ToolChoice = _agent.ToolChoice,
            };

            // Each round starts its own visible text. The host shows the current
            // round's output; the turn's final answer is the last round's, and mixing
            // "let me check" from round one into the final text is how a character ends
            // up narrating its own tool use to the player.
            _text.Length = 0;

            ILlmStreamHandle handle = _agent.Provider.StartStream(request);
            if (handle == null)
            {
                Fail("provider returned no stream handle", 0);
                return false;
            }

            _handle = handle;
            handle.ContentDelta += OnContentDelta;
            handle.Completed += OnStreamCompleted;
            return true;
        }

        private void OnContentDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }

            _text.Append(delta);

            Action<string> handler = Delta;
            if (handler != null)
            {
                handler(delta);
            }
        }

        private void OnStreamCompleted(LlmStreamResult result)
        {
            _lastStream = result;

            if (result == null)
            {
                Fail("provider reported completion with no result", 0);
                return;
            }

            if (!result.Success)
            {
                Fail(string.IsNullOrEmpty(result.Error) ? "request failed" : result.Error, result.HttpStatus);
                return;
            }

            if (result.HasToolCalls)
            {
                if (_toolRounds >= _agent.MaxToolRounds)
                {
                    // Out of rounds. What the model has said so far is still an answer,
                    // so the turn finishes normally; ToolRounds in the result tells a
                    // host that the cap was reached.
                    _rawText = result.Content ?? string.Empty;
                    SetState(AgentTurnState.Validating);
                    return;
                }

                _pendingCalls = result.ToolCalls;
                _roundText = result.Content;
                SetState(AgentTurnState.RunningTools);
                return;
            }

            _rawText = result.Content ?? string.Empty;
            SetState(AgentTurnState.Validating);
        }

        // --------------------------------------------------------------- tool rounds

        /// <summary>
        /// Advances the round's tool calls.
        ///
        /// The calls go through a ToolBatch rather than a for-loop, which is what lets
        /// several of them be in flight at once. Two properties of the serial version are
        /// preserved deliberately: refusal wording is unchanged, and results are fed back
        /// in call order no matter what order they finished in.
        /// </summary>
        private void PumpTools()
        {
            ToolBatch batch = _batch;

            if (batch == null)
            {
                List<ToolInvocation> calls = _pendingCalls;
                _pendingCalls = null;

                if (calls == null || calls.Count == 0)
                {
                    if (!StartRequest())
                    {
                        return;
                    }
                    SetState(AgentTurnState.Streaming);
                    return;
                }

                _toolRounds++;

                // The assistant turn that requested the tools must be replayed before
                // the results. Sending tool messages on their own reads as the model
                // having answered itself, and vendors reject it or ignore it.
                LlmMessage assistant = new LlmMessage(LlmRole.Assistant, _roundText);
                assistant.ToolCalls = calls;
                _roundMessages.Add(assistant);

                for (int i = 0; i < calls.Count; i++)
                {
                    _toolCallsMade++;
                    _toolLog.Add(calls[i]);
                }

                batch = new ToolBatch(
                    calls,
                    _agent.Tools,
                    _agent.ToolApproval,
                    _agent.MaxConcurrentToolJobs,
                    OnToolFinished);
                _batch = batch;
            }

            if (batch.Pump())
            {
                return;
            }

            // Every call has settled. Read the results back by call position, never by
            // completion order, so tool message i answers tool_call i.
            ToolResult[] results = batch.Results;
            IReadOnlyList<ToolInvocation> settled = batch.Calls;
            for (int i = 0; i < settled.Count; i++)
            {
                ToolInvocation call = settled[i];
                ToolResult result = results[i] ?? ToolResult.Fail("tool call produced no result");
                _roundMessages.Add(LlmMessage.Tool(call.CallId, call.ToolName, result.Content));
            }

            _batch = null;

            if (!StartRequest())
            {
                return;
            }
            SetState(AgentTurnState.Streaming);
        }

        /// <summary>
        /// Surfaces a finished call. Fires in completion order, not call order -- a fast
        /// lookup should reach the host's UI before a slow one that started earlier.
        /// </summary>
        private void OnToolFinished(ToolInvocation call, ToolResult result)
        {
            Action<ToolInvocation, ToolResult> handler = ToolFinished;
            if (handler != null)
            {
                handler(call, result);
            }
        }

        // --------------------------------------------------------------- completion

        private void Validate()
        {
            string raw = _rawText ?? string.Empty;

            ValidationInput input = new ValidationInput
            {
                RawText = raw,
                ExpectedSchemaJson = _agent.ExpectedSchemaJson,
                AllowedToolNames = _agent.BuildToolNames(),
            };

            ValidationResult verdict = _agent.Validators.Run(input);

            if (verdict == null || !verdict.Accepted)
            {
                string reason = verdict != null ? verdict.Reason : "validators returned null";
                Fail("output rejected: " + reason, 0);
                return;
            }

            string text = verdict.NormalizedText != null ? verdict.NormalizedText : raw;

            _text.Length = 0;
            _text.Append(text);

            _agent.RecordTurn(_actorId, _userMessage, text);
            Succeed(text, raw);
        }

        private void Succeed(string text, string raw)
        {
            _clock.Stop();
            _result = new AgentTurnResult
            {
                Success = true,
                Text = text,
                RawText = raw,
                ToolRounds = _toolRounds,
                ToolCallsMade = _toolCallsMade,
                ToolLog = _toolLog,
                ElapsedMs = _clock.Elapsed.TotalMilliseconds,
                LastStream = _lastStream,
                ContextTokens = _agent.LastContextTokens,
                ContextSectionsDropped = _agent.LastDroppedSections,
            };
            SetState(AgentTurnState.Completed);
        }

        private void Fail(string error, long status)
        {
            if (IsDone)
            {
                return;
            }

            _clock.Stop();
            _result = new AgentTurnResult
            {
                Success = false,
                Error = string.IsNullOrEmpty(error) ? "unknown failure" : error,
                HttpStatus = status,
                Text = _text.ToString(),
                RawText = _rawText,
                ToolRounds = _toolRounds,
                ToolCallsMade = _toolCallsMade,
                ToolLog = _toolLog,
                ElapsedMs = _clock.Elapsed.TotalMilliseconds,
                LastStream = _lastStream,
                ContextTokens = _agent.LastContextTokens,
                ContextSectionsDropped = _agent.LastDroppedSections,
            };
            SetState(AgentTurnState.Faulted);
        }

        private void SetState(AgentTurnState state)
        {
            _state = state;
        }
    }
}
