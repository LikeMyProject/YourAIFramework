using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YourFramework.UI;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// UGUI 面板：全部程序化搭建（Canvas / EventSystem / Text / Button），
    /// 不依赖任何预制体与字体资产。
    ///
    /// 三层界面：
    /// - HUD（常驻）：血条、经验条、属性一行字、战报日志；
    /// - 背包（I 键或按钮开合）：12 格按钮，点药水喝药、点装备穿上；
    /// - 覆盖层：菜单 / 阵亡，各带一个主按钮。
    /// </summary>
    public sealed class RpgUi : MonoBehaviour
    {
        public System.Action<int> OnSlotClicked;
        public System.Action OnStartClicked;
        public System.Action OnRestartClicked;
        /// <summary>回城（城镇场景）。战败或主动撤退都会触发。</summary>
        public System.Action OnHomeClicked;
        /// <summary>副本通关后回城。</summary>
        public System.Action OnVictoryHomeClicked;

        private Text _hud;
        private Text _log;
        private Text _prompt;
        private Image _hpFill;
        private Image _expFill;
        private Image _mpFill;
        private readonly List<Image> _skillMasks = new List<Image>();
        private readonly List<RectTransform> _skillRects = new List<RectTransform>();
        private readonly List<Text> _skillCooldownTexts = new List<Text>();
        private readonly List<Text> _skillLabels = new List<Text>();

        public System.Action<int> OnSkillClicked;
        private GameObject _inventoryPanel;
        private Text[] _slotLabels;
        private Text _equippedLabel;
        private GameObject _menuOverlay;
        private GameObject _deadOverlay;
        private Text _deadSummary;
        /// <summary>框架面板栈：开合状态、层级、互斥屏蔽都交给它，这里只搭视觉树。</summary>
        private PanelStack _panels;

        public void Build()
        {
            GameObject canvasGo = new GameObject("RpgCanvas");
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);   // 不设默认 800x600，1080p 全放大约 1.8 倍（踩过）
            canvasGo.AddComponent<GraphicRaycaster>();
            canvasGo.transform.SetParent(transform, false);

            Object[] standouts = Resources.FindObjectsOfTypeAll(typeof(EventSystem));
            if (standouts.Length == 0)
            {
                GameObject es = new GameObject("RpgEventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
                es.transform.SetParent(transform, false);
            }

            Font font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 26);

            BuildHud(canvasGo.transform, font);
            BuildLog(canvasGo.transform, font);
            BuildInventory(canvasGo.transform, font);
            BuildOverlays(canvasGo.transform, font);

            // 面板开合交给框架 PanelStack：注册四个面板，状态机和层级互斥不自己写
            //（"能走框架的走框架"）。GoPanel 只负责把"栈说开/关"翻译成 SetActive。
            _panels = new PanelStack()
                .Register(new GoPanel("hud", PanelLayer.Background, canvasGo))
                .Register(new GoPanel("inventory", PanelLayer.Normal, _inventoryPanel))
                .Register(new GoPanel("menu", PanelLayer.Popup, _menuOverlay))
                .Register(new GoPanel("dead", PanelLayer.Overlay, _deadOverlay));
            _panels.OpenNow("hud");
            _menuOverlay.SetActive(false);      // 初始视觉与栈状态对齐：栈里全关着，但搭出来的菜单默认是开的
            // 技能栏的位置（底部中央）在 Build 之后由宿主按技能个数摆（AddSkillSlot）。
        }

        // ------------------------------------------------------------------ HUD

        private void BuildHud(Transform parent, Font font)
        {
            // 左上角头像框：勇者的"脸"（程序化色块 + 一个字，教学 demo 不碰美术资产）。
            GameObject avatar = new GameObject("Avatar");
            avatar.transform.SetParent(parent, false);
            Image avatarImg = avatar.AddComponent<Image>();
            avatarImg.color = new Color(0.16f, 0.24f, 0.4f, 0.95f);
            RectTransform avatarRt = avatar.GetComponent<RectTransform>();
            avatarRt.anchorMin = new Vector2(0f, 1f);
            avatarRt.anchorMax = new Vector2(0f, 1f);
            avatarRt.pivot = new Vector2(0f, 1f);
            avatarRt.anchoredPosition = new Vector2(20f, -20f);
            avatarRt.sizeDelta = new Vector2(72f, 72f);
            GameObject faceGo = new GameObject("Face");
            faceGo.transform.SetParent(avatar.transform, false);
            Text face = faceGo.AddComponent<Text>();
            face.font = font;
            face.fontSize = 40;
            face.alignment = TextAnchor.MiddleCenter;
            face.color = new Color(0.85f, 0.9f, 1f);
            face.supportRichText = false;
            face.text = "勇";
            RectTransform faceRt = faceGo.GetComponent<RectTransform>();
            faceRt.anchorMin = Vector2.zero;
            faceRt.anchorMax = Vector2.one;
            faceRt.sizeDelta = Vector2.zero;
            faceRt.offsetMin = Vector2.zero;
            faceRt.offsetMax = Vector2.zero;

            // 头像右侧一列：血 / 蓝 / 经验
            MakeBar(parent, "HpBg", new Vector2(104f, -24f), new Vector2(320f, 26f), new Color(0f, 0f, 0f, 0.45f));
            _hpFill = MakeBar(parent, "HpFill", new Vector2(106f, -26f), new Vector2(316f, 22f), new Color(0.85f, 0.25f, 0.2f));
            _hpFill.sprite = WhiteSprite();     // 没 sprite 时 UGUI 忽略 Filled 直接画满格（踩过：血条永远满）
            _hpFill.type = Image.Type.Filled;
            _hpFill.fillMethod = Image.FillMethod.Horizontal;

            MakeBar(parent, "MpBg", new Vector2(104f, -58f), new Vector2(320f, 14f), new Color(0f, 0f, 0f, 0.45f));
            _mpFill = MakeBar(parent, "MpFill", new Vector2(106f, -60f), new Vector2(316f, 10f), new Color(0.35f, 0.45f, 0.9f));
            _mpFill.sprite = WhiteSprite();
            _mpFill.type = Image.Type.Filled;
            _mpFill.fillMethod = Image.FillMethod.Horizontal;

            MakeBar(parent, "ExpBg", new Vector2(104f, -80f), new Vector2(320f, 10f), new Color(0f, 0f, 0f, 0.45f));
            _expFill = MakeBar(parent, "ExpFill", new Vector2(106f, -82f), new Vector2(316f, 6f), new Color(0.3f, 0.65f, 0.95f));
            _expFill.sprite = WhiteSprite();
            _expFill.type = Image.Type.Filled;
            _expFill.fillMethod = Image.FillMethod.Horizontal;

            _hud = MakeText(parent, "HudText", new Vector2(20f, -104f), new Vector2(420f, 150f), 22, font, TextAnchor.UpperLeft);

            // 交互提示：左下角小字（不挡中间视野 —— 居中提示横在技能栏上方，踩过）
            GameObject promptGo = new GameObject("InteractPrompt");
            promptGo.transform.SetParent(parent, false);
            _prompt = promptGo.AddComponent<Text>();
            _prompt.font = font;
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

        private static Sprite _white;

        /// <summary>程序生成的纯白图。UGUI 的 Image 在没有 sprite 时会跳过 Filled
        /// 直接整格绘制 —— fillAmount 整个失效（血条永远满、冷却遮罩永远全黑，
        /// 都是这一个坑）。所有用 Filled 的图都必须挂上它。</summary>
        private static Sprite WhiteSprite()
        {
            if (_white == null)
            {
                Texture2D tex = Texture2D.whiteTexture;
                _white = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), tex.width);
            }

            return _white;
        }

        private static Image MakeBar(Transform parent, string name, Vector2 anchor, Vector2 size, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Image image = go.AddComponent<Image>();
            image.color = color;
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.pivot = new Vector2(0f, 1f);
            rt.anchorMin = new Vector2(0f, 1f);     // 左上角锚：anchor 参数的负 y = 从顶往下（之前是左下锚，血条全画到屏幕外了）
            rt.anchorMax = new Vector2(0f, 1f);
            rt.anchoredPosition = anchor;
            rt.sizeDelta = size;
            return image;
        }

        private static Text MakeText(Transform parent, string name, Vector2 anchor, Vector2 size,
            int fontSize, Font font, TextAnchor align)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = fontSize;
            text.alignment = align;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.pivot = new Vector2(0f, 1f);
            rt.anchorMin = new Vector2(0f, 1f);     // 左上角锚（与 MakeBar 同口径）
            rt.anchorMax = new Vector2(0f, 1f);
            rt.anchoredPosition = anchor;
            rt.sizeDelta = size;
            return text;
        }

        private void BuildLog(Transform parent, Font font)
        {
            _log = MakeText(parent, "LogText", new Vector2(20f, -430f), new Vector2(560f, 170f), 20, font, TextAnchor.LowerLeft);
        }

        // ------------------------------------------------------------------ 背包

        private void BuildInventory(Transform parent, Font font)
        {
            _inventoryPanel = MakePanel(parent, "InventoryPanel", new Vector2(0f, 0f), new Vector2(520f, 460f),
                new Color(0.08f, 0.08f, 0.1f, 0.92f));

            Text title = MakeText(_inventoryPanel.transform, "Title", new Vector2(30f, -20f), new Vector2(460f, 40f),
                30, font, TextAnchor.UpperLeft);
            title.text = "背包（I 键关闭）";

            _slotLabels = new Text[RpgPlayer.SlotMax];
            GameObject[] slotButtons = new GameObject[RpgPlayer.SlotMax];
            for (int i = 0; i < RpgPlayer.SlotMax; i++)
            {
                int index = i;                              // 闭包捕获：按钮 i 永远报 i
                GameObject button = MakeButton(_inventoryPanel.transform, "Slot" + i,
                    new Vector2(30f + (i % 4) * 118f, -80f - (i / 4) * 92f),
                    new Vector2(110f, 84f), font, 20, string.Empty,
                    delegate { if (OnSlotClicked != null) { OnSlotClicked(index); } });
                slotButtons[i] = button;
                _slotLabels[i] = button.GetComponentInChildren<Text>();
            }

            _equippedLabel = MakeText(_inventoryPanel.transform, "Equipped", new Vector2(30f, -368f), new Vector2(460f, 70f),
                22, font, TextAnchor.UpperLeft);
            _inventoryPanel.SetActive(false);
        }

        private static GameObject MakePanel(Transform parent, string name, Vector2 anchoredPosition,
            Vector2 size, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Image image = go.AddComponent<Image>();
            image.color = color;
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = size;
            return go;
        }

        private static GameObject MakeButton(Transform parent, string name, Vector2 anchoredPosition,
            Vector2 size, Font font, int fontSize, string label, System.Action onClick)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.22f, 0.38f, 0.6f);
            Button button = go.AddComponent<Button>();
            if (onClick != null)
            {
                button.onClick.AddListener(delegate { onClick(); });
            }

            RectTransform rt = go.GetComponent<RectTransform>();
            if (parent.GetComponent<RectTransform>().sizeDelta.x > 1f)
            {
                // 面板内：挂在面板中心
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
            }
            else
            {
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
            }

            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = size;

            GameObject labelGo = new GameObject("Text");
            labelGo.transform.SetParent(go.transform, false);
            Text text = labelGo.AddComponent<Text>();
            text.font = font;
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
            return go;
        }

        // ------------------------------------------------------------------ 覆盖层

        private void BuildOverlays(Transform parent, Font font)
        {
            _menuOverlay = MakePanel(parent, "MenuOverlay", Vector2.zero, new Vector2(600f, 400f),
                new Color(0.06f, 0.07f, 0.1f, 0.94f));
            Text menuTitle = MakeText(_menuOverlay.transform, "Title", new Vector2(50f, -60f), new Vector2(500f, 60f),
                40, font, TextAnchor.UpperLeft);
            menuTitle.text = "勇者试炼场（框架 RPG 演示）";
            Text menuHelp = MakeText(_menuOverlay.transform, "Help", new Vector2(50f, -140f), new Vector2(500f, 120f),
                22, font, TextAnchor.UpperLeft);
            menuHelp.text = "WASD 移动　空格攻击　I 背包\n杀怪得经验金币，捡装备穿上变强\n等级越高，来的怪越凶";
            MakeButton(_menuOverlay.transform, "StartButton", new Vector2(0f, -110f),
                new Vector2(300f, 60f), font, 28, "开始冒险",
                delegate { if (OnStartClicked != null) { OnStartClicked(); } });

            _deadOverlay = MakePanel(parent, "DeadOverlay", Vector2.zero, new Vector2(600f, 380f),
                new Color(0.12f, 0.04f, 0.04f, 0.94f));
            Text deadTitle = MakeText(_deadOverlay.transform, "Title", new Vector2(50f, -70f), new Vector2(500f, 60f),
                40, font, TextAnchor.UpperLeft);
            deadTitle.text = "你倒下了……";
            _deadSummary = MakeText(_deadOverlay.transform, "Summary", new Vector2(50f, -140f), new Vector2(500f, 50f),
                24, font, TextAnchor.UpperLeft);
            MakeButton(_deadOverlay.transform, "AgainButton", new Vector2(-140f, -120f),
                new Vector2(220f, 60f), font, 28, "原地再战",
                delegate { if (OnRestartClicked != null) { OnRestartClicked(); } });
            MakeButton(_deadOverlay.transform, "HomeButton", new Vector2(140f, -120f),
                new Vector2(220f, 60f), font, 28, "回城休整",
                delegate { if (OnHomeClicked != null) { OnHomeClicked(); } });
            _deadOverlay.SetActive(false);
        }

        // ------------------------------------------------------------------ 对外接口

        public void ShowMenu()
        {
            _panels.CloseNow("dead");
            _panels.CloseNow("inventory");
            _panels.OpenNow("menu");
        }

        public void ShowPlaying()
        {
            _panels.CloseNow("menu");
            _panels.CloseNow("dead");
        }

        public void ShowDead(int kills)
        {
            _deadSummary.text = "本局击杀 " + kills + " 只。等级、装备、背包都还在。";
            _panels.CloseNow("inventory");
            _panels.OpenNow("dead");
        }

        /// <summary>副本通关：魔王倒下，结算后回城。</summary>
        public void ShowVictory(int kills)
        {
            _deadSummary.text = "副本通关！本局击杀 " + kills + " 只，魔王必掉好装备。";
            _panels.CloseNow("inventory");
            _panels.OpenNow("dead");
        }

        public void SetHud(RpgPlayer player)
        {
            string weapon = player.WeaponId == null ? "空手" : RpgItems.Find(player.WeaponId).Name;
            string armor = player.ArmorId == null ? "无" : RpgItems.Find(player.ArmorId).Name;
            _hud.text = "Lv." + player.Level + "　金币 " + player.Gold
                + "\n攻击 " + player.Attack + "（基础" + player.BaseAttack + "）　防御 " + player.Defense
                + "　法力 " + player.Mp.ToString("0") + "/" + player.MaxMp
                + "\n武器 " + weapon + "　护甲 " + armor
                + "\n经验 " + player.Exp + " / " + player.ExpToNext
                + "\n背包 " + player.Slots.Count + " / " + RpgPlayer.SlotMax + "　（I 键开背包）";
        }

        public void SetHpBar(int hp, int maxHp)
        {
            _hpFill.fillAmount = maxHp <= 0 ? 0f : (float)hp / maxHp;
        }

        public void SetMpBar(float mp, float maxMp)
        {
            _mpFill.fillAmount = maxMp <= 0f ? 0f : mp / maxMp;
        }

        /// <summary>
        /// 摆一个技能格（底部中央横排）。个数由宿主按技能表决定 —— 配置驱动，
        /// 表里加一个技能，界面自动多一格，代码不动。
        /// </summary>
        /// <summary>
        /// 建一格技能：76x76 方格，中央热键大字、格子底边露技能名、
        /// 全格冷却遮罩 + 中央倒计时数字。摆位延后到 <see cref="LayoutSkillSlots"/>
        /// （要等格子总数齐了才能对称居中，逐格算必然挤成一团——踩过）。
        /// </summary>
        public void AddSkillSlot(int index, string label, string hotkey)
        {
            GameObject go = new GameObject("Skill" + index);
            go.transform.SetParent(_log.transform.parent, false);
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.15f, 0.2f, 0.35f, 0.95f);
            Button button = go.AddComponent<Button>();
            int captured = index;
            button.onClick.AddListener(delegate { if (OnSkillClicked != null) { OnSkillClicked(captured); } });

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(76f, 76f);
            _skillRects.Add(rt);

            Font font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 22);

            // 中央热键（被遮罩压在下面，冷却时看得见格子进入冷却）
            GameObject hotGo = new GameObject("Hotkey");
            hotGo.transform.SetParent(go.transform, false);
            Text hot = hotGo.AddComponent<Text>();
            hot.font = font;
            hot.fontSize = 30;
            hot.alignment = TextAnchor.MiddleCenter;
            hot.color = new Color(1f, 0.95f, 0.7f);
            hot.supportRichText = false;
            hot.text = hotkey;
            FillParent(hotGo);

            // 冷却遮罩：半透明黑，从上往下收
            GameObject maskGo = new GameObject("CooldownMask");
            maskGo.transform.SetParent(go.transform, false);
            Image mask = maskGo.AddComponent<Image>();
            mask.color = new Color(0f, 0f, 0f, 0.6f);
            mask.sprite = WhiteSprite();        // 同血条的坑：没 sprite Filled 失效，遮罩会永远全黑
            mask.type = Image.Type.Filled;
            mask.fillMethod = Image.FillMethod.Vertical;
            mask.fillOrigin = (int)Image.OriginVertical.Top;
            mask.raycastTarget = false;
            mask.fillAmount = 0f;
            FillParent(maskGo);
            _skillMasks.Add(mask);

            // 冷却倒计时数字（遮罩正中央，就绪时隐藏）
            GameObject cdGo = new GameObject("CooldownText");
            cdGo.transform.SetParent(go.transform, false);
            Text cd = cdGo.AddComponent<Text>();
            cd.font = font;
            cd.fontSize = 26;
            cd.alignment = TextAnchor.MiddleCenter;
            cd.color = Color.white;
            cd.supportRichText = false;
            cd.raycastTarget = false;
            cd.text = "";
            FillParent(cdGo);
            _skillCooldownTexts.Add(cd);

            // 技能名：格子下方一条
            GameObject labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            Text text = labelGo.AddComponent<Text>();
            text.font = font;
            text.fontSize = 16;
            text.alignment = TextAnchor.UpperCenter;
            text.color = Color.white;
            text.supportRichText = false;
            text.text = label;
            RectTransform labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(1f, 0f);
            labelRt.pivot = new Vector2(0.5f, 1f);
            labelRt.anchoredPosition = Vector2.zero;
            labelRt.sizeDelta = new Vector2(0f, 22f);
            _skillLabels.Add(text);
        }

        /// <summary>技能个数齐了之后调一次：底部中央对称摆开，间距 12。</summary>
        public void LayoutSkillSlots()
        {
            int count = _skillRects.Count;
            if (count == 0)
            {
                return;
            }
            const float cell = 76f;
            const float gap = 12f;
            float total = count * cell + (count - 1) * gap;
            for (int i = 0; i < count; i++)
            {
                _skillRects[i].anchoredPosition = new Vector2(-total / 2f + cell / 2f + i * (cell + gap), 34f);
            }
        }

        private static void FillParent(GameObject go)
        {
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>冷却表现：遮罩比例 + 倒计时秒数（<=0 隐藏数字）。</summary>
        public void SetSkillCooldown(int index, float ratio, float secondsLeft)
        {
            if (index >= _skillMasks.Count)
            {
                return;
            }
            float clamped = Mathf.Clamp01(ratio);
            _skillMasks[index].fillAmount = clamped;
            Text cd = _skillCooldownTexts[index];
            bool show = clamped > 0.01f && secondsLeft > 0.05f;
            cd.gameObject.SetActive(show);
            if (show)
            {
                cd.text = secondsLeft > 1f
                    ? ((int)secondsLeft).ToString()
                    : secondsLeft.ToString("0.0");
            }
        }

        public void SetExpBar(int exp, int expToNext)
        {
            _expFill.fillAmount = expToNext <= 0 ? 0f : (float)exp / expToNext;
        }

        public void RefreshInventory(RpgPlayer player)
        {
            for (int i = 0; i < _slotLabels.Length; i++)
            {
                if (i < player.Slots.Count)
                {
                    RpgItemDef def = RpgItems.Find(player.Slots[i].DefId);
                    string suffix = def.Kind == RpgItemKind.Potion ? " x" + player.Slots[i].Count : "";
                    _slotLabels[i].text = def.Name + suffix;
                }
                else
                {
                    _slotLabels[i].text = "空";
                }
            }

            string weapon = player.WeaponId == null ? "空手" : RpgItems.Find(player.WeaponId).Name;
            string armor = player.ArmorId == null ? "无" : RpgItems.Find(player.ArmorId).Name;
            _equippedLabel.text = "已装备：武器 " + weapon + "　护甲 " + armor
                + "\n点药水直接喝，点装备直接穿";
        }

        public void ToggleInventory(bool show)
        {
            if (show)
            {
                _panels.OpenNow("inventory");
            }
            else
            {
                _panels.CloseNow("inventory");
            }
        }

        public bool InventoryVisible
        {
            get { return _panels != null && _panels.IsOpen("inventory"); }
        }

        public void AddLog(string line)
        {
            string current = _log.text;
            int lines = 1;
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] == '\n')
                {
                    lines++;
                }
            }

            if (lines > 6)
            {
                int cut = current.IndexOf('\n');
                current = current.Substring(cut + 1);
            }

            _log.text = current.Length == 0 ? line : current + "\n" + line;
        }

        public void ClearLog()
        {
            _log.text = "";
        }

        /// <summary>交互提示（走近回城传送石显示），null 或空串隐藏。</summary>
        public void SetPrompt(string text)
        {
            bool show = !string.IsNullOrEmpty(text);
            _prompt.gameObject.SetActive(show);
            if (show)
            {
                _prompt.text = text;
            }
        }

        // ------------------------------------------------------------------ 面板适配

        /// <summary>
        /// 把一块已搭好的 GameObject 树挂进框架 PanelStack 的最小适配器：
        /// 栈说开 → SetActive(true)，栈说关 → SetActive(false)。
        /// 开关状态、层级、互斥屏蔽全是 PanelStack 的事，这里只管看得见看不见。
        /// 无动画面板走 OpenNow/CloseNow 同步开合，OnClosed 里关视觉与两段式
        /// 的"Closing 后宿主拆树"同拍等价。
        /// </summary>
        private sealed class GoPanel : PanelController
        {
            private readonly string _name;
            private readonly PanelLayer _layer;
            private readonly GameObject _go;

            public GoPanel(string name, PanelLayer layer, GameObject go)
            {
                _name = name;
                _layer = layer;
                _go = go;
            }

            public override string Name { get { return _name; } }

            public override PanelLayer Layer { get { return _layer; } }

            public override void OnOpening()
            {
                _go.SetActive(true);
            }

            public override void OnClosed()
            {
                _go.SetActive(false);
            }
        }
    }
}
