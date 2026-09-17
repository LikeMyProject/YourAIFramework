using System.Collections;
using System.Text;
using UnityEngine;
using YourFramework.Core;
using YourFramework.Event;
using YourFramework.Pool;
using YourFramework.UnityRuntime;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// Demo 01-03：内核三件套——ModuleCenter / EventBus / 对象池。
    /// 每个 Run() 都是一条完整的协程时间线，Console 逐行上演。
    /// </summary>
    public static class DemoCore
    {
        // ==================================================================
        // Demo 01：模块中心——"一切皆模块，依赖就是数字"
        // ==================================================================
        public static IEnumerator ModuleCenter()
        {
            var center = new ModuleCenter();

            // 注册：FuelTank 是 0 号，Engine 是 10 号，Engine 依赖 FuelTank。
            center.Register(new DemoFuelTank());
            center.Register(new DemoEngine());

            Debug.Log("[Demo01] 注册完成（FuelTank=0, Engine=10）。Initialize() 按序初始化：");
            center.Initialize();

            for (int frame = 0; frame < 3; frame++)
            {
                center.Pump();      // GameEntry 的 Update 每帧替你调这个
                yield return null;
            }

            Debug.Log("[Demo01] Pump 了 3 帧（看上面每帧的日志）。Shutdown() 逆序关停：");
            center.Shutdown();

            // 重复注册是布线 bug：当场抛异常（fail-fast）。
            var another = new ModuleCenter();
            another.Register(new DemoFuelTank());
            try
            {
                another.Register(new DemoFuelTank());
            }
            catch (System.InvalidOperationException ex)
            {
                Debug.Log("[Demo01] 重复注册被当场拦截: " + ex.Message);
            }
        }

        private sealed class DemoFuelTank : IModule
        {
            public string Name { get { return "FuelTank"; } }
            public int InitOrder { get { return 0; } }
            public void Init(ModuleCenter host) { Debug.Log("[Demo01]   FuelTank.Init（先初始化，因为 0 < 10）"); }
            public void Pump() { }
            public void Shutdown() { Debug.Log("[Demo01]   FuelTank.Shutdown（后关停，逆序）"); }
        }

        private sealed class DemoEngine : IModule
        {
            private int _pumps;
            public string Name { get { return "Engine"; } }
            public int InitOrder { get { return 10; } }

            public void Init(ModuleCenter host)
            {
                var fuel = host.Get<DemoFuelTank>();    // 只能依赖 InitOrder 更小的模块
                Debug.Log("[Demo01]   Engine.Init，依赖已就绪: " + fuel.Name);
            }

            public void Pump()
            {
                _pumps++;
                Debug.Log("[Demo01]   Engine.Pump 第 " + _pumps + " 帧");
            }

            public void Shutdown() { Debug.Log("[Demo01]   Engine.Shutdown（先关停，逆序）"); }
        }

        // ==================================================================
        // Demo 02：事件总线——Publish 同步直发，Post + Pump 延迟到下一拍
        // ==================================================================
        public static IEnumerator EventBus()
        {
            var bus = new EventBus();   // 也可以直接用 EventBus.Global

            bus.Subscribe<PlayerDied>(e =>
                Debug.Log("[Demo02] 订阅者收到 PlayerDied，原因: " + e.Reason));
            bus.Subscribe<PlayerLevelUp>(e =>
                Debug.Log("[Demo02] 订阅者收到 PlayerLevelUp，等级: " + e.Level));

            // Publish：同步、立即、零装箱。
            Debug.Log("[Demo02] Publish —— 调用栈里立刻派发：");
            bus.Publish(new PlayerDied { Reason = "坠落" });

            // Post：先入队，Pump 时按序派发（一次 Pump = 一帧的量）。
            Debug.Log("[Demo02] Post —— 已入队，但此刻还没人收到：");
            bus.Post(new PlayerLevelUp { Level = 2 });
            Debug.Log("[Demo02] （此处没有任何订阅者被调用）");
            bus.Pump();
            Debug.Log("[Demo02] Pump 之后，队列里的事件按序派发完毕。");

            yield break;
        }

        private struct PlayerDied { public string Reason; }
        private struct PlayerLevelUp { public int Level; }

        // ==================================================================
        // Demo 03：对象池——C# 对象池 + GameObject 池，借还必须配平
        // ==================================================================
        public static IEnumerator ObjectPools()
        {
            // ---- C# 对象池 ----
            var pool = new ObjectPool<StringBuilder>(
                () => new StringBuilder(),
                null,
                sb => sb.Clear(),
                maxIdle: 16);

            StringBuilder sb = pool.Get();          // 借
            sb.Append("池化对象复用，第 ").Append(1).Append(" 次");
            Debug.Log("[Demo03] 借出 StringBuilder: " + sb);
            pool.Release(sb);                       // 还（会执行归还钩子 Clear）

            Debug.Log("[Demo03] 归还后：空闲 " + pool.CountIdle + " 个 / 活跃 " + pool.CountActive + " 个（LIFO，刚还的最先借出）");

            try
            {
                pool.Release(sb);                   // 重复归还是程序 bug：当场抛
            }
            catch (System.InvalidOperationException ex)
            {
                Debug.Log("[Demo03] 借还失衡被当场拦截: " + ex.Message);
            }

            // ---- GameObject 池 ----
            var goPool = DemoPools.Shared;
            GameObject fx = goPool.Get();
            fx.transform.position = new Vector3(0f, 1f, 0f);
            Debug.Log("[Demo03] 借出方块 " + fx.name + "（SetActive(true)），1 秒后归还");
            yield return new WaitForSeconds(1f);
            goPool.Release(fx);                     // 自动 SetActive(false) 归位
            Debug.Log("[Demo03] 归还后：空闲 " + goPool.CountIdle + " / 活跃 " + goPool.CountActive);
        }
    }

    /// <summary>GameObject 池的共享宿主：用运行时生成的方块当"预制体"，演示零资产依赖。</summary>
    public sealed class DemoPools
    {
        private static GameObjectPool _shared;
        public static GameObjectPool Shared
        {
            get
            {
                if (_shared == null)
                {
                    GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = "DemoCube";
                    Object.DontDestroyOnLoad(cube);
                    cube.SetActive(false);
                    _shared = new GameObjectPool(cube, null, maxIdle: 8);
                }

                return _shared;
            }
        }
    }
}
