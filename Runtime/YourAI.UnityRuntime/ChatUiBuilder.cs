using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace YourAI.UnityRuntime
{
    /// <summary>
    /// 聊天 UI 的共享构建器：设计器路径（按名查找 log/status/text/error/input/send）
    /// 与代码路径（同一结构原地手搭）两条腿。AgentViewBehaviour（独立形态）与
    /// ChatPanel（面板栈形态）共用，杜绝两份手搭代码漂移。
    ///
    /// 六个元素名是行为与 UXML 之间的契约，改名要同步样例 UXML 与文档。
    /// 输入接线是"每个 ChatElements 实例只挂一次、经 OnSend 委托转发"——重建出
    /// 新实例自然重新挂，同一实例重复 Wire 不会双发。
    /// </summary>
    public static class ChatUiBuilder
    {
        /// <summary>The six named elements both shapes of the chat window rely on.</summary>
        public sealed class ChatElements
        {
            public ScrollView Log;
            public Label Status;
            public Label Error;
            public Label Text;
            public TextField Input;
            public Button Send;

            /// <summary>Current send handler, swapped freely by the owner.</summary>
            public Action OnSend;

            internal bool Wired;

            /// <summary>True when every contract element was found or built.</summary>
            public bool Complete
            {
                get
                {
                    return Log != null && Status != null && Error != null
                        && Text != null && Input != null && Send != null;
                }
            }

            internal void FireSend()
            {
                Action handler = OnSend;
                if (handler != null)
                {
                    handler();
                }
            }
        }

        /// <summary>
        /// Builds (or looks up) the chat structure under <paramref name="root"/>.
        /// Call once per fresh visual tree: a PanelRenderer reload discards the old
        /// one wholesale, so the caller rebuilds from the new root each reload.
        /// </summary>
        public static ChatElements Build(VisualElement root)
        {
            // Designer path: a UXML asset instantiated into the root already
            // contains the named elements.
            ChatElements elements = new ChatElements
            {
                Log = root.Q<ScrollView>("log"),
                Status = root.Q<Label>("status"),
                Error = root.Q<Label>("error"),
                Text = root.Q<Label>("text"),
                Input = root.Q<TextField>("input"),
                Send = root.Q<Button>("send"),
            };

            if (elements.Complete)
            {
                return elements;
            }

            // Code path: same structure, built here, so the window runs with no
            // asset wiring at all.
            root.style.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;

            elements.Status = new Label(string.Empty);
            elements.Status.style.unityFontStyleAndWeight = FontStyle.Bold;
            elements.Status.style.color = new Color(0.6f, 0.7f, 0.85f);
            root.Add(elements.Status);

            elements.Log = new ScrollView();
            elements.Log.style.flexGrow = 1f;
            root.Add(elements.Log);

            elements.Text = new Label(string.Empty);
            elements.Text.style.whiteSpace = WhiteSpace.Normal;
            elements.Text.style.color = new Color(0.9f, 0.9f, 0.9f);
            elements.Text.style.marginBottom = 4;
            root.Add(elements.Text);

            elements.Error = new Label(string.Empty);
            elements.Error.style.whiteSpace = WhiteSpace.Normal;
            elements.Error.style.color = new Color(0.9f, 0.45f, 0.4f);
            elements.Error.style.marginBottom = 4;
            root.Add(elements.Error);

            VisualElement row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            root.Add(row);

            elements.Input = new TextField();
            elements.Input.style.flexGrow = 1f;
            row.Add(elements.Input);

            elements.Send = new Button(null) { text = "送出" };
            row.Add(elements.Send);

            return elements;
        }

        /// <summary>
        /// Wires send-button and Enter-key input to <paramref name="onSend"/>.
        /// One-time per ChatElements instance (the closures read OnSend, so the
        /// handler itself can be swapped later); a rebuilt tree means a new
        /// instance, which wires itself exactly once.
        /// </summary>
        public static void WireInput(ChatElements elements, Action onSend)
        {
            if (elements == null || !elements.Complete || elements.Wired)
            {
                return;
            }

            elements.OnSend = onSend;
            elements.Send.clicked += elements.FireSend;
            elements.Input.RegisterCallback<KeyDownEvent>(
                delegate(KeyDownEvent evt)
                {
                    if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                    {
                        elements.FireSend();
                        evt.StopPropagation();
                    }
                });
            elements.Wired = true;
        }

        /// <summary>Appends one line to the log and pins the scroll to the bottom.</summary>
        public static void AppendLine(ChatElements elements, string speaker, string line)
        {
            if (elements == null || elements.Log == null)
            {
                return;
            }

            Label entry = new Label(speaker + "：" + line);
            entry.style.whiteSpace = WhiteSpace.Normal;
            entry.style.marginBottom = 2;
            elements.Log.Add(entry);
            // ScrollView has no ScrollToEnd in Unity 6; pinning the offset to the
            // maximum clamps to the bottom, which is the same effect.
            elements.Log.scrollOffset = new Vector2(0f, float.MaxValue);
        }
    }
}
