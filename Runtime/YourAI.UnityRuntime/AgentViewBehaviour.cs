using System;
using UnityEngine;
using UnityEngine.UIElements;
using YourAI.Agent;
using YourAI.Presentation;

namespace YourAI.UnityRuntime
{
    /// <summary>
    /// The Unity half of the MVVM pair: a UI Toolkit chat window driven by an
    /// <see cref="AgentViewModel"/>. Two shapes share one builder
    /// (<see cref="ChatUiBuilder"/>):
    ///
    /// 1. standalone -- this MonoBehaviour on a PanelRenderer, self-pumping in
    ///    Update (the legacy shape, kept working with zero migration);
    /// 2. <see cref="ChatPanel"/> -- the same window as a YourFramework panel,
    ///    attached to the panel stack and pumped by UIPanelManager.
    ///
    /// Wiring, the whole story:
    ///
    ///     var agent = AiRuntimeFactory.CreateDeepSeekAgent("smith", apiKey, "……");
    ///     GetComponent<AgentViewBehaviour>().Bind(agent);
    /// </summary>
    [RequireComponent(typeof(PanelRenderer))]
    public sealed class AgentViewBehaviour : MonoBehaviour
    {
        private AgentViewModel _viewModel;
        private VisualElement _root;
        private ChatUiBuilder.ChatElements _elements;

        private string _pendingUserLine = string.Empty;

        /// <summary>The live view model, once bound. Null before that.</summary>
        public AgentViewModel ViewModel { get { return _viewModel; } }

        private void Awake()
        {
            // Register at the earliest lifecycle point: the callback is how the
            // root is delivered, and a registration that arrives late would miss
            // the first reload entirely.
            PanelRenderer panel = GetComponent<PanelRenderer>();
            panel.RegisterUIReloadCallback(OnUIReloaded);
        }

        private void OnDestroy()
        {
            PanelRenderer panel = GetComponent<PanelRenderer>();
            if (panel != null)
            {
                panel.UnregisterUIReloadCallback(OnUIReloaded);
            }

            Unbind();
        }

        private void Update()
        {
            if (_viewModel != null)
            {
                _viewModel.Pump();
            }
        }

        /// <summary>Binds a pre-built view model, for hosts that manage agents themselves.</summary>
        public void Bind(AgentViewModel viewModel)
        {
            Unbind();

            _viewModel = viewModel;
            if (_viewModel == null)
            {
                return;
            }

            ConnectViewModel();
        }

        /// <summary>Convenience: builds a view model over an agent and binds it.</summary>
        public void Bind(AiAgent agent)
        {
            Bind(new AgentViewModel(agent));
        }

        private void Unbind()
        {
            if (_viewModel == null)
            {
                return;
            }

            _viewModel.TextChanged -= OnTextChanged;
            _viewModel.StateChanged -= OnStateChanged;
            _viewModel.ErrorChanged -= OnErrorChanged;
            _viewModel.BusyChanged -= OnBusyChanged;
            _viewModel = null;
        }

        // ------------------------------------------------- panel reload (the root)

        private void OnUIReloaded(PanelRenderer panelRenderer, VisualElement rootElement)
        {
            // A reload discards the previous tree wholesale: the named lookups and
            // any code-built elements are stale the moment this callback runs.
            bool sameRoot = _root == rootElement && _elements != null && _elements.Complete;
            _root = rootElement;

            if (!sameRoot && _root != null)
            {
                _elements = ChatUiBuilder.Build(_root);
                ChatUiBuilder.WireInput(_elements, OnSendClicked);
            }

            // Binding may have happened before this reload; a fresh tree starts
            // blank, so the live truth is written into it here. Connect is
            // idempotent (unsubscribe-then-subscribe), so a double call is safe.
            if (_viewModel != null)
            {
                ConnectViewModel();
            }
        }

        private void ConnectViewModel()
        {
            _viewModel.TextChanged -= OnTextChanged;
            _viewModel.StateChanged -= OnStateChanged;
            _viewModel.ErrorChanged -= OnErrorChanged;
            _viewModel.BusyChanged -= OnBusyChanged;

            _viewModel.TextChanged += OnTextChanged;
            _viewModel.StateChanged += OnStateChanged;
            _viewModel.ErrorChanged += OnErrorChanged;
            _viewModel.BusyChanged += OnBusyChanged;

            // Sync once so a bound-late view shows the current truth rather than
            // waiting for the next change.
            OnTextChanged();
            OnStateChanged();
            OnErrorChanged();
            OnBusyChanged();
        }

        // ------------------------------------------------------------- interaction

        private void OnSendClicked()
        {
            if (_viewModel == null || _elements == null || _elements.Input == null)
            {
                return;
            }

            string line = _elements.Input.value;
            if (string.IsNullOrEmpty(line) || !_viewModel.Send(line))
            {
                return;
            }

            _pendingUserLine = line;
            ChatUiBuilder.AppendLine(_elements, "你", line);
            _elements.Input.value = string.Empty;
        }

        // ------------------------------------------------------- view model events

        private void OnTextChanged()
        {
            if (_elements != null && _elements.Text != null && _viewModel != null)
            {
                _elements.Text.text = _viewModel.Text;
            }
        }

        private void OnStateChanged()
        {
            if (_elements != null && _elements.Status != null && _viewModel != null)
            {
                _elements.Status.text = PhaseLabel(_viewModel.State);
            }
        }

        private void OnErrorChanged()
        {
            if (_elements != null && _elements.Error != null && _viewModel != null)
            {
                string error = _viewModel.Error;
                _elements.Error.text = error;
                _elements.Error.style.display =
                    string.IsNullOrEmpty(error) ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        private void OnBusyChanged()
        {
            if (_viewModel == null)
            {
                return;
            }

            if (_elements != null && _elements.Send != null)
            {
                _elements.Send.SetEnabled(_viewModel.CanSend);
            }

            // The turn just ended with words on the stage: move them into the log.
            // This is where a chat window stops looking like a text probe.
            if (!_viewModel.IsBusy && _viewModel.Text.Length > 0 && _pendingUserLine.Length > 0)
            {
                ChatUiBuilder.AppendLine(_elements, "他", _viewModel.Text);
                _pendingUserLine = string.Empty;
            }
        }

        private static string PhaseLabel(AgentTurnState state)
        {
            switch (state)
            {
                case AgentTurnState.Building: return "思考中…";
                case AgentTurnState.Streaming: return "说话中…";
                case AgentTurnState.RunningTools: return "动手中…";
                case AgentTurnState.Validating: return "斟酌中…";
                case AgentTurnState.Completed: return "说完了";
                case AgentTurnState.Faulted: return "出错了";
                case AgentTurnState.Cancelled: return "已打断";
                default: return "待命";
            }
        }
    }
}
