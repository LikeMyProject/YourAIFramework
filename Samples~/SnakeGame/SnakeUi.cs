using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YourAIFramework.SnakeGame
{
    /// <summary>
    /// UGUI 面板：Canvas、文字、按钮全部程序化生成，不依赖任何预制体与字体资产。
    /// 中文字体用系统的微软雅黑（macOS 请把 FontName 换成 "PingFang SC"）。
    ///
    /// 文本只在事件到来时改写（吃食物、切状态、AI 回话），稳态每帧零分配。
    /// 按钮回调由宿主注入 —— 面板不知道流程机存在，只暴露"有人点了开始"。
    /// </summary>
    public sealed class SnakeUi : MonoBehaviour
    {
        /// <summary>OS 字体名。Windows 微软雅黑；macOS 改 "PingFang SC"。</summary>
        public const string FontName = "Microsoft YaHei";

        private Text _scoreText;
        private Text _aiText;
        private Text _centerText;
        private GameObject _startButton;
        private GameObject _restartButton;

        /// <summary>开始按钮被点（宿主接走：切流程机到 Playing）。</summary>
        public System.Action OnStartClicked;

        /// <summary>重新开始按钮被点（宿主接走：Reset + 切 Playing）。</summary>
        public System.Action OnRestartClicked;

        /// <summary>AI 操控开关被点（宿主接走：切 AI 模式）。</summary>
        public System.Action OnToggleAiClicked;

        public void Build()
        {
            Font font = Font.CreateDynamicFontFromOSFont(FontName, 26);

            GameObject canvasGo = new GameObject("SnakeCanvas");
            canvasGo.transform.SetParent(transform, false);
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            canvasGo.AddComponent<GraphicRaycaster>();

            // 没有 EventSystem 按钮是死的 —— 这是 uGUI 新手第二个坑，演示直接摆正。
            GameObject es = new GameObject("SnakeEventSystem");
            es.transform.SetParent(transform, false);
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();

            _scoreText = MakeText(canvasGo.transform, "Score", font, 30,
                new Vector2(0f, 1f), new Vector2(20f, -16f), TextAnchor.UpperLeft);
            _aiText = MakeText(canvasGo.transform, "AiStatus", font, 22,
                new Vector2(0f, 1f), new Vector2(20f, -58f), TextAnchor.UpperLeft);
            _aiText.color = new Color(0.25f, 0.35f, 0.55f);

            _centerText = MakeText(canvasGo.transform, "Center", font, 44,
                new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), TextAnchor.MiddleCenter);
            _centerText.gameObject.SetActive(false);

            _startButton = MakeButton(canvasGo.transform, "StartButton", font, "开始游戏",
                new Vector2(0.5f, 0.5f), new Vector2(0f, -40f), new Vector2(220f, 56f));
            BindClick(_startButton, delegate { if (OnStartClicked != null) { OnStartClicked(); } });

            _restartButton = MakeButton(canvasGo.transform, "RestartButton", font, "重新开始",
                new Vector2(0.5f, 0.5f), new Vector2(0f, -40f), new Vector2(220f, 56f));
            BindClick(_restartButton, delegate { if (OnRestartClicked != null) { OnRestartClicked(); } });
            _restartButton.SetActive(false);

            GameObject aiButton = MakeButton(canvasGo.transform, "AiButton", font, "AI 操控",
                new Vector2(1f, 1f), new Vector2(-130f, -46f), new Vector2(200f, 52f));
            BindClick(aiButton, delegate { if (OnToggleAiClicked != null) { OnToggleAiClicked(); } });
        }

        /// <summary>得分行（吃食物、开新局时刷新）。</summary>
        public void SetScore(int score, int high, bool newRecord)
        {
            _scoreText.text = "得分 " + score + "    最高 " + high + (newRecord ? "（新纪录！）" : "");
        }

        /// <summary>AI 状态行：思考中 / 回答了什么 / 有没有落到启发式，全都如实显示。</summary>
        public void SetAiStatus(string status)
        {
            _aiText.text = status;
        }

        public void ShowMenu()
        {
            _centerText.gameObject.SetActive(false);
            _restartButton.SetActive(false);
            _startButton.SetActive(true);
            SetScore(0, 0, false);
        }

        public void ShowPlaying()
        {
            _centerText.gameObject.SetActive(false);
            _startButton.SetActive(false);
            _restartButton.SetActive(false);
        }

        public void ShowGameOver(int score, bool newRecord)
        {
            _centerText.text = score > 0
                ? "游戏结束：得分 " + score + (newRecord ? "\n新纪录！" : "")
                : "游戏结束";
            _centerText.gameObject.SetActive(true);
            _startButton.SetActive(false);
            _restartButton.SetActive(true);
        }

        // ------------------------------------------------------------------ build

        private Text MakeText(Transform parent, string name, Font font, int size,
            Vector2 anchor, Vector2 position, TextAnchor alignment)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = Color.black;
            text.rectTransform.anchorMin = anchor;
            text.rectTransform.anchorMax = anchor;
            text.rectTransform.anchoredPosition = position;
            text.rectTransform.sizeDelta = new Vector2(600f, 80f);
            return text;
        }

        private GameObject MakeButton(Transform parent, string name, Font font, string label,
            Vector2 anchor, Vector2 position, Vector2 size)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>();
            Button button = go.AddComponent<Button>();
            go.GetComponent<RectTransform>().anchorMin = anchor;
            go.GetComponent<RectTransform>().anchorMax = anchor;
            go.GetComponent<RectTransform>().anchoredPosition = position;
            go.GetComponent<RectTransform>().sizeDelta = size;

            GameObject labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            Text text = labelGo.AddComponent<Text>();
            text.font = font;
            text.fontSize = 26;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.black;
            text.text = label;
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.sizeDelta = Vector2.zero;
            return go;
        }

        private static void BindClick(GameObject buttonGo, UnityEngine.Events.UnityAction action)
        {
            buttonGo.GetComponent<Button>().onClick.AddListener(action);
        }
    }
}
