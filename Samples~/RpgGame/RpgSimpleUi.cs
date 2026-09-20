using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// 简易程序化 UI：主菜单与城镇两个场景共用。跟 <see cref="RpgUi"/> 一样
    /// 不依赖任何预制体与字体资产 —— Canvas / Text / Button 全代码搭。
    ///
    /// 结构：标题、副标题、一列按钮、一行状态字、底部加载进度条。
    /// 进度条数据来自场景流程（SceneFlow.Progress），这里只负责画。
    ///
    /// 分辨率口径：参考分辨率 1920x1080（设计空间 y 范围 ±540），
    /// 所有元素垂直收在 -500 以内 —— 用 720 做参考时 1080p 会整体放大 1.5 倍，
    /// 底部元素直接跑出屏幕（踩过）。
    /// </summary>
    public sealed class RpgSimpleUi : MonoBehaviour
    {
        private Text _subtitle;
        private Text _line;
        private Text _prompt;
        private RectTransform _progressFill;
        private GameObject _progressGroup;
        private Text _progressText;
        private readonly System.Collections.Generic.List<Button> _buttons =
            new System.Collections.Generic.List<Button>();
        private Font _font;
        private bool _compact;

        public void Build(string title, string subtitle)
        {
            Build(title, subtitle, false);
        }

        /// <summary>
        /// compact = 角落模式：标题/状态收左上角、按钮收右上角、交互提示收左下角，
        /// 全部小一号 —— 城镇是能逛的 3D 世界，居中大控件挡视角（踩过：提示横在屏幕中央）。
        /// 主菜单是纯面板场景，仍用居中布局。
        /// </summary>
        public void Build(string title, string subtitle, bool compact)
        {
            _compact = compact;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            GameObject canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(transform, false);
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasGo.AddComponent<GraphicRaycaster>();
            if (Object.FindObjectOfType<EventSystem>() == null)
            {
                GameObject es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            RectTransform root = canvasGo.GetComponent<RectTransform>();

            if (compact)
            {
                Text titleText = MakeCornerText(root, "Title", TextAnchor.UpperLeft,
                    new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(18f, -14f),
                    new Vector2(640f, 38f), 26);
                titleText.text = title;

                _subtitle = MakeCornerText(root, "Subtitle", TextAnchor.UpperLeft,
                    new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(18f, -54f),
                    new Vector2(640f, 30f), 16);
                _subtitle.text = subtitle == null ? "" : subtitle;

                _line = MakeCornerText(root, "Line", TextAnchor.UpperLeft,
                    new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(18f, -88f),
                    new Vector2(640f, 32f), 18);

                // 交互提示：左下角小字，走近交互点才亮，不挡中间视野
                GameObject promptGo = new GameObject("InteractPrompt");
                promptGo.transform.SetParent(root, false);
                _prompt = promptGo.AddComponent<Text>();
                _prompt.font = _font;
                _prompt.fontSize = 18;
                _prompt.alignment = TextAnchor.MiddleLeft;
                _prompt.color = new Color(1f, 0.88f, 0.5f);
                _prompt.supportRichText = false;
                _prompt.text = "";
                RectTransform promptRt = promptGo.GetComponent<RectTransform>();
                promptRt.anchorMin = new Vector2(0f, 0f);
                promptRt.anchorMax = new Vector2(0f, 0f);
                promptRt.pivot = new Vector2(0f, 0f);
                promptRt.anchoredPosition = new Vector2(18f, 14f);
                promptRt.sizeDelta = new Vector2(720f, 28f);
                promptGo.SetActive(false);
            }
            else
            {
                Text titleText = MakeText(root, "Title", new Vector2(0f, -80f), new Vector2(1200f, 80f), 52);
                titleText.text = title;

                _subtitle = MakeText(root, "Subtitle", new Vector2(0f, -175f), new Vector2(1200f, 50f), 22);
                _subtitle.text = subtitle == null ? "" : subtitle;

                _line = MakeText(root, "Line", new Vector2(0f, -240f), new Vector2(1200f, 56f), 24);

                // 交互提示：屏幕下方居中，走近交互点才亮（面板场景用不到，留给以后）
                GameObject promptGo = new GameObject("InteractPrompt");
                promptGo.transform.SetParent(root, false);
                _prompt = promptGo.AddComponent<Text>();
                _prompt.font = _font;
                _prompt.fontSize = 28;
                _prompt.alignment = TextAnchor.MiddleCenter;
                _prompt.color = new Color(1f, 0.88f, 0.5f);
                _prompt.supportRichText = false;
                _prompt.text = "";
                RectTransform promptRt = promptGo.GetComponent<RectTransform>();
                promptRt.anchorMin = new Vector2(0.5f, 0f);
                promptRt.anchorMax = new Vector2(0.5f, 0f);
                promptRt.pivot = new Vector2(0.5f, 0f);
                promptRt.anchoredPosition = new Vector2(0f, 120f);
                promptRt.sizeDelta = new Vector2(1000f, 44f);
                promptGo.SetActive(false);
            }

            BuildProgress(root);
        }

        /// <summary>加一个按钮：居中布局纵向自动排列（从 -300 开始，步进 64）；
        /// 角落模式收右上角，小一号，不挡 3D 视野。</summary>
        public void AddButton(string label, System.Action onClick)
        {
            GameObject go = new GameObject("Btn_" + label);
            go.AddComponent<Image>().color = new Color(0.18f, 0.32f, 0.5f, 1f);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(transform.Find("Canvas"), false);
            int index = _buttons.Count;
            Vector2 size;
            int fontSize;
            if (_compact)
            {
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-16f, -14f - index * 46f);
                size = new Vector2(200f, 36f);
                fontSize = 16;
            }
            else
            {
                rt.anchoredPosition = new Vector2(0f, -300f - index * 64f);
                size = new Vector2(340f, 52f);
                fontSize = 24;
            }

            rt.sizeDelta = size;

            GameObject labelGo = new GameObject("Text");
            labelGo.transform.SetParent(go.transform, false);
            Text text = labelGo.AddComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.supportRichText = false;
            text.text = label;
            RectTransform labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.sizeDelta = Vector2.zero;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;

            Button button = go.AddComponent<Button>();
            button.onClick.AddListener(delegate { if (onClick != null) { onClick(); } });
            _buttons.Add(button);
        }

        /// <summary>场景加载期间锁按钮，防止手快点出重叠请求。</summary>
        public void SetButtonsEnabled(bool on)
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                _buttons[i].interactable = on;
            }
        }

        /// <summary>一行状态字（属性行 / 拒绝原因 / 提示）。</summary>
        public void SetLine(string text)
        {
            _line.text = text == null ? "" : text;
        }

        /// <summary>交互提示（走近泉水/传送门/城门显示），null 或空串隐藏。</summary>
        public void SetPrompt(string text)
        {
            if (_prompt == null)
            {
                return;
            }

            bool show = !string.IsNullOrEmpty(text);
            _prompt.gameObject.SetActive(show);
            if (show)
            {
                _prompt.text = text;
            }
        }

        /// <summary>
        /// 切场景遮罩：ratio 0..1 显示全屏半透明黑（盖住底层界面与场景切换跳变），
        /// 中央是进度条 + 文字；ratio 传负数隐藏。空场景加载毫秒级，
        /// 配合后端的 minVisibleSeconds，进度条会钉在满格把保底时间演完。
        /// </summary>
        public void ShowProgress(float ratio, string text)
        {
            if (_progressGroup == null)
            {
                return;
            }
            bool visible = ratio >= 0f;
            _progressGroup.SetActive(visible);
            if (!visible)
            {
                return;
            }
            if (ratio > 1f) ratio = 1f;
            _progressFill.anchorMax = new Vector2(ratio, 1f);
            _progressText.text = text == null ? "" : text;
        }

        // ------------------------------------------------------------------ 内部

        private void BuildProgress(Transform root)
        {
            // 全屏遮罩：挡住按钮与场景，切换期间整屏一个进度条
            _progressGroup = new GameObject("SceneOverlay");
            _progressGroup.transform.SetParent(root, false);
            RectTransform group = _progressGroup.AddComponent<RectTransform>();
            group.anchorMin = Vector2.zero;
            group.anchorMax = Vector2.one;
            group.sizeDelta = Vector2.zero;
            group.offsetMin = Vector2.zero;
            group.offsetMax = Vector2.zero;

            Image dim = _progressGroup.AddComponent<Image>();
            dim.color = new Color(0.03f, 0.04f, 0.06f, 0.9f);
            dim.raycastTarget = true;                       // 挡住底下的点击

            // 中央进度条（挂遮罩中心）
            GameObject barGo = new GameObject("Bar");
            barGo.transform.SetParent(_progressGroup.transform, false);
            RectTransform bar = barGo.AddComponent<RectTransform>();
            bar.anchorMin = new Vector2(0.5f, 0.5f);
            bar.anchorMax = new Vector2(0.5f, 0.5f);
            bar.anchoredPosition = Vector2.zero;
            bar.sizeDelta = new Vector2(800f, 34f);
            Image barBg = barGo.AddComponent<Image>();
            barBg.color = new Color(0.12f, 0.14f, 0.18f, 1f);
            barBg.raycastTarget = false;

            GameObject fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(barGo.transform, false);
            _progressFill = fillGo.AddComponent<RectTransform>();
            _progressFill.anchorMin = new Vector2(0f, 0f);
            _progressFill.anchorMax = new Vector2(0f, 1f);
            _progressFill.offsetMin = new Vector2(3f, 3f);
            _progressFill.offsetMax = new Vector2(-3f, -3f);
            Image fill = fillGo.AddComponent<Image>();
            fill.color = new Color(0.3f, 0.65f, 0.4f, 1f);
            fill.raycastTarget = false;

            // 进度条下方文字
            GameObject textGo = new GameObject("Text");
            textGo.transform.SetParent(_progressGroup.transform, false);
            _progressText = textGo.AddComponent<Text>();
            _progressText.font = _font;
            _progressText.fontSize = 26;
            _progressText.alignment = TextAnchor.UpperCenter;
            _progressText.color = Color.white;
            _progressText.supportRichText = false;
            RectTransform textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = new Vector2(0.5f, 0.5f);
            textRt.anchorMax = new Vector2(0.5f, 0.5f);
            textRt.anchoredPosition = new Vector2(0f, -60f);
            textRt.sizeDelta = new Vector2(900f, 60f);

            _progressGroup.SetActive(false);
        }

        private Text MakeText(Transform parent, string name, Vector2 pos, Vector2 size, int fontSize)
        {
            return MakeCornerText(parent, name, TextAnchor.UpperCenter,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size, fontSize);
        }

        /// <summary>可摆角落的文字：锚点/轴心/对齐全可调，居中布局和角落模式共用。</summary>
        private Text MakeCornerText(Transform parent, string name, TextAnchor align,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size, int fontSize)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Text text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.alignment = align;
            text.color = Color.white;
            text.supportRichText = false;
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return text;
        }
    }
}
