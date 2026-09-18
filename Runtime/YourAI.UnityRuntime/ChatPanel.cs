using System;
using UnityEngine.UIElements;
using YourAI.Agent;
using YourAI.Presentation;
using YourFramework.UnityRuntime;
using YourFramework.UI;

namespace YourAI.UnityRuntime
{
    /// <summary>
    /// 聊天窗的面板栈形态：与 AgentViewBehaviour（独立形态）共用
    /// <see cref="ChatUiBuilder"/>，挂在 UIPanelManager 的 Normal 层。
    ///
    /// 生命周期契约：
    /// - CreateRoot（Opening）：用共享构建器搭树并接线；
    /// - OnOpened：绑定 AgentViewModel（复用同一 AiAgent——对话历史在 agent 上，
    ///   关窗再开不丢）；
    /// - OnClosing：解绑 VM，停掉事件串扰；
    /// - PanelPump（Open 期间每帧）：驱动 VM 的回合泵。
    ///
    /// 引导示例（agent 先建，喂给 AiModule 与 ChatPanel 同一个实例）：
    ///
    ///     AiAgent agent = AiRuntimeFactory.CreateDeepSeekAgent(...);
    ///     entry.Register(new AiModule(() => agent, EventBus.Global));
    ///     ui.Register(new ChatPanel(agent));
    /// </summary>
    public sealed class ChatPanel : UIPanel, IPanelTickable
    {
        private readonly AiAgent _agent;
        private readonly string _actorId;
        private AgentViewModel _viewModel;
        private ChatUiBuilder.ChatElements _elements;
        private string _pendingUserLine = string.Empty;

        public ChatPanel(AiAgent agent, string actorId = "player")
        {
            if (agent == null)
            {
                throw new ArgumentNullException("agent");
            }

            _agent = agent;
            _actorId = actorId;
        }

        public override string Name { get { return "chat"; } }

        public override PanelLayer Layer { get { return PanelLayer.Normal; } }

        public override VisualElement CreateRoot()
        {
            VisualElement root = new VisualElement();
            _elements = ChatUiBuilder.Build(root);
            ChatUiBuilder.WireInput(_elements, OnSendClicked);
            return root;
        }

        public override void OnOpened()
        {
            if (_viewModel == null)
            {
                _viewModel = new AgentViewModel(_agent, _actorId);
            }

            ConnectViewModel();
        }

        public override void OnClosing()
        {
            UnbindViewModel();
        }

        public void PanelPump()
        {
            if (_viewModel != null)
            {
                _viewModel.Pump();
            }
        }

        /// <summary>The live view model while the panel is open, else null.</summary>
        public AgentViewModel ViewModel { get { return _viewModel; } }

        // ------------------------------------------------------------------ core

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

            // Sync once so a reopened panel shows the current truth immediately.
            OnTextChanged();
            OnStateChanged();
            OnErrorChanged();
            OnBusyChanged();
        }

        private void UnbindViewModel()
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
