using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using YourFramework.Core;
using YourFramework.UI;

namespace YourFramework.UnityRuntime
{
    /// <summary>
    /// 面板栈的引擎侧面板基类：逻辑在 <see cref="PanelController"/>（纯 C#，
    /// 已离线检查），这里只补一件事——<see cref="CreateRoot"/> 提供视觉树，
    /// 由 <see cref="UIPanelManager"/> 在 Opening 时挂到对应层级容器。
    /// </summary>
    public abstract class UIPanel : PanelController
    {
        /// <summary>The attached visual tree, or null while detached.</summary>
        public VisualElement Root { get; private set; }

        /// <summary>Builds the visual tree. Called on every attach; a rebuild must be self-contained.</summary>
        public abstract VisualElement CreateRoot();

        internal void Attach(VisualElement root)
        {
            Root = root;
        }

        internal void Detach()
        {
            Root = null;
        }
    }

    /// <summary>Per-frame tick for panels that own a pump loop (view models, animations).</summary>
    public interface IPanelTickable
    {
        /// <summary>Called once per frame while the panel is Open.</summary>
        void PanelPump();
    }

    /// <summary>
    /// 面板栈的引擎壳：一个 PanelRenderer，根下按 <see cref="PanelLayer"/> 分五个
    /// 容器；面板 Opening 时 <see cref="UIPanel.CreateRoot"/> 并挂进所属层，
    /// Closing 时摘除。开合同帧完成（视觉资产异步化是资源模块 M3 的事，到时
    /// CompleteOpen/CompleteClose 两段式接口原样承接）。
    ///
    /// 刻意保持薄：状态机/层级/屏蔽全在纯内核（ui/* 检查项），这里只有挂接与
    /// 生命周期搬运。Unity 6.5 的 PanelRenderer 重载会整树重建，故分层容器在
    /// 每次 reload 回调里重建，WantsVisual 的面板自动重挂。
    /// </summary>
    public sealed class UIPanelManager : IModule
    {
        private readonly PanelSettings _panelSettings;
        private readonly PanelStack _stack = new PanelStack();
        private readonly List<UIPanel> _panels = new List<UIPanel>();
        private readonly Dictionary<PanelLayer, VisualElement> _layers =
            new Dictionary<PanelLayer, VisualElement>();
        private PanelRenderer _renderer;

        /// <param name="panelSettings">The Panel Settings asset (Create > UI Toolkit > Panel Settings Asset).</param>
        public UIPanelManager(PanelSettings panelSettings)
        {
            if (panelSettings == null)
            {
                throw new ArgumentNullException("panelSettings");
            }

            _panelSettings = panelSettings;
        }

        public string Name { get { return "ui"; } }

        /// <summary>Default order: above infrastructure, below the AI module.</summary>
        public int InitOrder { get { return 50; } }

        /// <summary>The pure core this shell drives (for advanced queries like IsInputBlocked).</summary>
        public PanelStack Stack { get { return _stack; } }

        /// <summary>Registers a panel. Setup phase (before module Init), like ModuleCenter.</summary>
        public UIPanelManager Register(UIPanel panel)
        {
            if (panel == null)
            {
                throw new ArgumentNullException("panel");
            }

            _stack.Register(panel);
            _panels.Add(panel);
            return this;
        }

        public void Init(ModuleCenter host)
        {
            GameObject hostObject = new GameObject("[UIPanelManager]");
            _renderer = hostObject.AddComponent<PanelRenderer>();
            _renderer.panelSettings = _panelSettings;
            _renderer.RegisterUIReloadCallback(OnUIReloaded);
        }

        public void Pump()
        {
            for (int i = 0; i < _panels.Count; i++)
            {
                if (!_stack.IsOpen(_panels[i].Name))
                {
                    continue;
                }

                IPanelTickable tickable = _panels[i] as IPanelTickable;
                if (tickable != null)
                {
                    tickable.PanelPump();
                }
            }
        }

        public void Shutdown()
        {
            // Full teardown: close, detach and complete every panel, then drop
            // the renderer GameObject.
            for (int i = 0; i < _panels.Count; i++)
            {
                UIPanel panel = _panels[i];
                // Close everything (logic-only panels included): skipping their
                // lifecycle would leave the stack inconsistent. DetachVisual is
                // a no-op for panels that never attached a visual.
                _stack.Close(panel.Name);
                DetachVisual(panel);
                _stack.CompleteClose(panel.Name);
            }

            if (_renderer != null)
            {
                UnityEngine.Object.Destroy(_renderer.gameObject);
                _renderer = null;
            }

            _layers.Clear();
        }

        /// <summary>Opens a panel: OnOpening → attach → OnOpened, same frame.</summary>
        public bool Open(string name)
        {
            UIPanel panel = GetPanelOrThrow(name);
            if (!_stack.Open(name))
            {
                return false;
            }

            AttachVisual(panel);
            _stack.CompleteOpen(name);
            return true;
        }

        /// <summary>Closes a panel: OnClosing → detach → OnClosed, same frame.</summary>
        public bool Close(string name)
        {
            UIPanel panel = GetPanelOrThrow(name);
            if (!_stack.Close(name))
            {
                return false;
            }

            DetachVisual(panel);
            _stack.CompleteClose(name);
            return true;
        }

        /// <summary>Closes everything, top-most first. Returns how many closes started.</summary>
        public int CloseAll()
        {
            List<UIPanel> open = new List<UIPanel>();
            foreach (UIPanel panel in _panels)
            {
                if (_stack.IsOpen(panel.Name))
                {
                    open.Add(panel);
                }
            }

            open.Sort(CompareTopFirst);
            int started = 0;
            for (int i = 0; i < open.Count; i++)
            {
                if (Close(open[i].Name))
                {
                    started++;
                }
            }

            return started;
        }

        /// <summary>The registered engine panel, or null.</summary>
        public UIPanel GetPanel(string name)
        {
            return _panels.Find(delegate(UIPanel p) { return p.Name == name; });
        }

        // ------------------------------------------------------------------ core

        private void OnUIReloaded(PanelRenderer panelRenderer, VisualElement root)
        {
            // A reload discards the tree wholesale: rebuild the layer containers
            // and re-attach whatever wanted to be visible.
            _layers.Clear();
            foreach (PanelLayer layer in Enum.GetValues(typeof(PanelLayer)))
            {
                VisualElement container = new VisualElement { name = "layer-" + layer };
                root.Add(container);
                _layers[layer] = container;
            }

            foreach (UIPanel panel in _panels)
            {
                if (panel.WantsVisual && _stack.IsOpen(panel.Name))
                {
                    AttachVisual(panel);
                }
            }
        }

        private void AttachVisual(UIPanel panel)
        {
            VisualElement layer;
            if (!_layers.TryGetValue(panel.Layer, out layer) || layer == null)
            {
                throw new InvalidOperationException(
                    "UIPanelManager: layer containers are not built yet -- a UI reload "
                    + "has not run. Open panels after the manager initialized (and, when "
                    + "using a UXML tree, after the first reload).");
            }

            VisualElement root = panel.CreateRoot();
            panel.Attach(root);
            layer.Add(root);
        }

        private void DetachVisual(UIPanel panel)
        {
            if (panel.Root != null)
            {
                panel.Root.RemoveFromHierarchy();
            }

            panel.Detach();
        }

        private UIPanel GetPanelOrThrow(string name)
        {
            UIPanel panel = GetPanel(name);
            if (panel == null)
            {
                throw new InvalidOperationException(
                    "UIPanelManager: no panel named '" + (name ?? "null") + "' is registered.");
            }

            return panel;
        }

        private int CompareTopFirst(UIPanel a, UIPanel b)
        {
            // Higher layer first; same layer, later-opened first.
            int byLayer = b.Layer.CompareTo(a.Layer);
            return byLayer;
        }
    }
}
