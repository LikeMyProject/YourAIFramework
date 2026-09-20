using System;
using System.Collections.Generic;

namespace YourFramework.UI
{
    /// <summary>Panel layers, bottom to top. Higher layer renders above and is entered later.</summary>
    public enum PanelLayer
    {
        Background = 0,
        Normal = 10,
        Popup = 20,
        Overlay = 30,
        Guide = 40,
    }

    /// <summary>
    /// Panel lifecycle. Closed → Opening → (host completes) → Open → Closing →
    /// (host completes) → Closed. The intermediate states exist so the engine
    /// adapter can run attach/detach (and later animations) between the logic
    /// callbacks without the core caring how long that takes.
    /// </summary>
    public enum PanelState
    {
        Closed = 0,
        Opening = 1,
        Open = 2,
        Closing = 3,
    }

    /// <summary>
    /// One UI panel's logic. Subclass in the engine layer to own a visual tree;
    /// the core only knows names, layers and the four callbacks.
    ///
    /// Callback order guaranteed by <see cref="PanelStack"/>:
    ///     Open()   → OnOpening  → (host attach) → OnOpened
    ///     Close()  → OnClosing  → (host detach) → OnClosed
    /// </summary>
    public abstract class PanelController
    {
        /// <summary>Unique within the stack.</summary>
        public abstract string Name { get; }

        /// <summary>Layer decides z-order and input blocking.</summary>
        public abstract PanelLayer Layer { get; }

        /// <summary>
        /// Exclusive panels (popups, overlays) block input to all OPEN panels on
        /// strictly lower layers while they are open. Default: Popup and above.
        /// </summary>
        public virtual bool Exclusive { get { return Layer >= PanelLayer.Popup; } }

        public virtual void OnOpening() { }
        public virtual void OnOpened() { }
        public virtual void OnClosing() { }
        public virtual void OnClosed() { }

        /// <summary>True while the visual tree should exist (Opening or Open).</summary>
        /// <summary>
        /// Whether the controller currently has a live visual attachment,
        /// managed by the stack: set true on Open, false on CompleteClose.
        /// Public by design: the engine shell reads it to re-attach visuals
        /// after a UI reload.
        /// </summary>
        public bool WantsVisual { get; internal set; }
    }

    /// <summary>
    /// 面板栈内核：注册 → 开合状态机 → 层级排序 → 独占面板输入屏蔽。
    ///
    /// 设计判断：
    /// 1. 纯 C# 内核，视觉树挂接由引擎适配壳在 Opening/Closing 状态完成——核心
    ///    逻辑（状态、层级、屏蔽）可离线逐条检查项；
    /// 2. 开合是两段式（Open/CompleteOpen），为动画与异步加载预留同一接口，
    ///    同帧完成也只是"马上调 Complete"；
    /// 3. 不做导航历史——面板回到上一面板是业务语义（各项目不同），不进内核。
    ///
    /// 合约违规（重名注册、对不存在面板操作）fail-fast 抛异常；对"已开再开"
    /// 这类幂等场景返回 false 而不是抛——调用方只是想确保它开着。
    /// </summary>
    public sealed class PanelStack
    {
        private readonly Dictionary<string, PanelController> _panels =
            new Dictionary<string, PanelController>();
        private readonly List<PanelController> _openOrder = new List<PanelController>();

        /// <summary>Registered panel count.</summary>
        public int Count { get { return _panels.Count; } }

        /// <summary>Registers a panel. Setup phase; duplicate names throw.</summary>
        public PanelStack Register(PanelController panel)
        {
            if (panel == null)
            {
                throw new ArgumentNullException("panel");
            }

            if (_panels.ContainsKey(panel.Name))
            {
                throw new InvalidOperationException(
                    "PanelStack.Register: panel '" + panel.Name + "' is already registered.");
            }

            _panels.Add(panel.Name, panel);
            return this;
        }

        /// <summary>
        /// Begins opening: OnOpening fires, state → Opening. Returns false if the
        /// panel is already Opening or Open (idempotent "make sure it's open").
        /// Unknown names throw -- that is always a bug.
        /// </summary>
        public bool Open(string name)
        {
            PanelController panel = GetOrThrow(name);
            if (StateOf(panel) != PanelState.Closed)
            {
                return false;
            }

            panel.WantsVisual = true;
            _states[panel] = PanelState.Opening;
            _openOrder.Add(panel);
            panel.OnOpening();
            return true;
        }

        /// <summary>
        /// Host confirms the visual tree is attached (same frame, or after an
        /// animation): state → Open, OnOpened fires. Only valid while Opening.
        /// </summary>
        public void CompleteOpen(string name)
        {
            PanelController panel = GetOrThrow(name);
            if (StateOf(panel) != PanelState.Opening)
            {
                throw new InvalidOperationException(
                    "PanelStack.CompleteOpen: '" + name + "' is not Opening.");
            }

            _states[panel] = PanelState.Open;
            panel.OnOpened();
        }

        /// <summary>
        /// Begins closing: OnClosing fires, state → Closing. Returns false if the
        /// panel is not Open or Opening. Unknown names throw.
        /// </summary>
        public bool Close(string name)
        {
            PanelController panel = GetOrThrow(name);
            PanelState state = StateOf(panel);
            if (state != PanelState.Opening && state != PanelState.Open)
            {
                return false;
            }

            _states[panel] = PanelState.Closing;
            _openOrder.Remove(panel);
            panel.OnClosing();
            return true;
        }

        /// <summary>Host confirms the visual tree is detached: state → Closed, OnClosed fires.</summary>
        public void CompleteClose(string name)
        {
            PanelController panel = GetOrThrow(name);
            if (StateOf(panel) != PanelState.Closing)
            {
                throw new InvalidOperationException(
                    "PanelStack.CompleteClose: '" + name + "' is not Closing.");
            }

            _states[panel] = PanelState.Closed;
            panel.WantsVisual = false;
            panel.OnClosed();
        }

        /// <summary>
        /// Begins closing every open/opening panel, top-most first (reverse open
        /// order). Returns how many Close operations were started.
        /// </summary>
        public int CloseAll()
        {
            int started = 0;
            for (int i = _openOrder.Count - 1; i >= 0; i--)
            {
                if (Close(_openOrder[i].Name))
                {
                    started++;
                }
            }

            return started;
        }

        /// <summary>
        /// 同步开：Open + CompleteOpen 一拍完成，返回是否真的开了（已开返回 false）。
        /// 无动画面板的常规路径 —— RPG demo 接入时发现两段式对简单面板太啰嗦，
        /// 加的便捷口；带开关动画的宿主仍走 Open/CompleteOpen 两段。
        /// </summary>
        public bool OpenNow(string name)
        {
            if (!Open(name))
            {
                return false;
            }

            CompleteOpen(name);
            return true;
        }

        /// <summary>同步关：Close + CompleteClose 一拍完成，返回是否真的关了（本来就关着返回 false）。</summary>
        public bool CloseNow(string name)
        {
            if (!Close(name))
            {
                return false;
            }

            CompleteClose(name);
            return true;
        }

        /// <summary>True when the panel is Opening or Open (visual should exist).</summary>
        public bool WantsVisual(string name)
        {
            PanelController panel = GetOrThrow(name);
            return panel.WantsVisual;
        }

        /// <summary>True when the panel reached Open (host attach completed).</summary>
        public bool IsOpen(string name)
        {
            return State(name) == PanelState.Open;
        }

        /// <summary>Current lifecycle state of a panel.</summary>
        public PanelState State(string name)
        {
            PanelState state;
            return _states.TryGetValue(GetOrThrow(name), out state) ? state : PanelState.Closed;
        }

        /// <summary>
        /// True when this panel's input is blocked: some OPEN exclusive panel sits
        /// on a strictly higher layer. Closing panels stop blocking immediately.
        /// </summary>
        public bool IsInputBlocked(string name)
        {
            PanelController self = GetOrThrow(name);
            if (!self.WantsVisual)
            {
                return true; // nothing on screen to receive input
            }

            for (int i = 0; i < _openOrder.Count; i++)
            {
                PanelController other = _openOrder[i];
                if (other != self
                    && other.Exclusive
                    && other.Layer > self.Layer
                    && State(other.Name) == PanelState.Open)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Open/opening panels ordered bottom → top (layer, then open order).</summary>
        public List<PanelController> VisibleStack()
        {
            List<PanelController> visible = new List<PanelController>(_openOrder);
            visible.Sort(CompareByLayerThenOpenOrder);
            return visible;
        }

        // ------------------------------------------------------------------ core

        private readonly Dictionary<PanelController, PanelState> _states =
            new Dictionary<PanelController, PanelState>();

        private PanelState StateOf(PanelController panel)
        {
            PanelState state;
            return _states.TryGetValue(panel, out state) ? state : PanelState.Closed;
        }

        private PanelController GetOrThrow(string name)
        {
            PanelController panel;
            if (name == null || !_panels.TryGetValue(name, out panel))
            {
                throw new InvalidOperationException(
                    "PanelStack: no panel named '" + (name ?? "null") + "' is registered.");
            }

            return panel;
        }

        private int CompareByLayerThenOpenOrder(PanelController a, PanelController b)
        {
            int byLayer = a.Layer.CompareTo(b.Layer);
            if (byLayer != 0)
            {
                return byLayer;
            }

            return _openOrder.IndexOf(a).CompareTo(_openOrder.IndexOf(b));
        }
    }
}
