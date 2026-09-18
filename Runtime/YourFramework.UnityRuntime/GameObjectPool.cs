using System;
using System.Collections.Generic;
using UnityEngine;

namespace YourFramework.Pool
{
    /// <summary>
    /// 预制体对象池：同一条借还契约（Get/Release/配平校验）的引擎侧形态。
    ///
    /// 设计边界：池不做资源加载（那是资源模块的事），只认一个已加载
    /// 的 prefab；不做优先级淘汰，只做空闲上限（超限实例销毁，不留暗账）。
    ///
    /// 必须守的规矩对应：
    /// - 性能：Get 走栈顶弹出，Release 入栈，均零分配；实例 SetActive(false)
    ///   回收而不是 Destroy，杜绝高频 Instantiate/Destroy 的 GC 尖峰；
    /// - 合约违规 fail-fast：重复 Release、归还非本池对象，直接抛——这是程序 bug。
    ///
    /// 归还已被外部 Destroy 的实例：静默忽略（Unity 的伪 null 判断补上），因为
    /// 场景卸载时引擎批量销毁属运行期事实，不是程序 bug。
    /// 线程：仅限主线程（引擎 API 决定）。
    /// </summary>
    public sealed class GameObjectPool
    {
        private readonly GameObject _prefab;
        private readonly Transform _parent;
        private readonly Stack<GameObject> _idle = new Stack<GameObject>();
        private readonly HashSet<GameObject> _active = new HashSet<GameObject>();
        private int _maxIdle;

        /// <summary>
        /// <param name="prefab">已加载的预制体（资源模块负责加载，本池不碰 I/O）。</param>
        /// <param name="parent">回收后的父节点；null = 场景根。</param>
        /// <param name="maxIdle">空闲上限，超出即 Destroy。默认 256。</param>
        /// </summary>
        public GameObjectPool(GameObject prefab, Transform parent = null, int maxIdle = 256)
        {
            if (prefab == null)
            {
                throw new ArgumentNullException("prefab");
            }

            _prefab = prefab;
            _parent = parent;
            _maxIdle = Math.Max(0, maxIdle);
        }

        /// <summary>Instances waiting for rent.</summary>
        public int CountIdle { get { return _idle.Count; } }

        /// <summary>Instances currently rented out.</summary>
        public int CountActive { get { return _active.Count; } }

        public int MaxIdle { get { return _maxIdle; } }

        /// <summary>Rents an instance, activated, parented, in default pose.</summary>
        public GameObject Get()
        {
            GameObject instance = _idle.Count > 0 ? _idle.Pop() : InstantiateFresh();
            instance.SetActive(true);
            if (!_active.Add(instance))
            {
                throw new InvalidOperationException("GameObjectPool.Get: instance is already marked active.");
            }

            return instance;
        }

        /// <summary>
        /// Returns an instance: deactivated, re-parented, pooled -- or destroyed when
        /// the idle cap is full. Destroyed instances are silently accepted (scene
        /// teardown owns them); everything else that fails the contract throws.
        /// </summary>
        public void Release(GameObject instance)
        {
            if (instance == null)
            {
                return; // destroyed (Unity fake-null): scene teardown already handled it
            }

            if (!_active.Remove(instance))
            {
                throw new InvalidOperationException(
                    "GameObjectPool.Release: object was not rented from this pool "
                    + "(double release or foreign object).");
            }

            instance.SetActive(false);
            if (_parent != null && instance.transform.parent != _parent)
            {
                instance.transform.SetParent(_parent, false);
            }

            if (_idle.Count < _maxIdle)
            {
                _idle.Push(instance);
            }
            else
            {
                UnityEngine.Object.Destroy(instance);
            }
        }

        /// <summary>True while the instance is rented out.</summary>
        public bool IsActive(GameObject instance)
        {
            return instance != null && _active.Contains(instance);
        }

        /// <summary>Creates <paramref name="count"/> idle instances up front (no rents).</summary>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count && _idle.Count < _maxIdle; i++)
            {
                GameObject instance = InstantiateFresh();
                instance.SetActive(false);
                _idle.Push(instance);
            }
        }

        /// <summary>Destroys every idle instance. Rented ones belong to their renters.</summary>
        public void Clear()
        {
            while (_idle.Count > 0)
            {
                UnityEngine.Object.Destroy(_idle.Pop());
            }
        }

        private GameObject InstantiateFresh()
        {
            return _parent != null
                ? UnityEngine.Object.Instantiate(_prefab, _parent)
                : UnityEngine.Object.Instantiate(_prefab);
        }
    }
}
