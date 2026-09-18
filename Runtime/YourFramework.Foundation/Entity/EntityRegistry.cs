using System;
using System.Collections.Generic;
using YourFramework.Core;

namespace YourFramework.Entity
{
    /// <summary>Base identity: a registry-unique name for diagnostics.</summary>
    public interface IEntity
    {
        /// <summary>Entity name; must be unique in the registry.</summary>
        string Name { get; }
    }

    /// <summary>Called once when the entity joins the registry.</summary>
    public interface ISpawnable
    {
        void OnSpawn();
    }

    /// <summary>Called once when the entity leaves the registry (also on Shutdown).</summary>
    public interface IDespawnable
    {
        void OnDespawn();
    }

    /// <summary>Per-frame tick with a caller-supplied time delta.</summary>
    public interface IEntityTickable
    {
        void EntityPump(float deltaSeconds);
    }

    /// <summary>
    /// 实体注册表：NPC/机关/子弹/系统组件的统一住址 —— 进门 OnSpawn、每帧
    /// EntityPump、出门 OnDespawn，单一顺序、单一真相。
    ///
    /// 设计判断：
    /// 1. 组件式而不是继承树 —— IEntity 是任意小接口的拼装（可 Spawn/可 Tick/可
    ///    Despawn），注册表按能力分派，实体类不被迫继承框架基类；
    /// 2. 错误隔离与 ModuleCenter 同款 —— 单个实体 Tick 抛异常不拖垮整帧，
    ///    经 AiDiagnosticsHost 上报后继续；
    /// 3. 时间注入 —— Pump(delta) 与 NetClient 同一哲学，回放可测；
    ///    IModule.Pump() 以 0 增量推进（事件语义），引擎宿主显式调用 Pump(dt)。
    /// </summary>
    public sealed class EntityRegistry : IModule
    {
        private readonly List<IEntity> _entities = new List<IEntity>();
        private readonly Dictionary<string, IEntity> _byName =
            new Dictionary<string, IEntity>(StringComparer.Ordinal);

        /// <summary>Live entity count.</summary>
        public int Count { get { return _entities.Count; } }

        /// <summary>The entity, or null when absent (missing is business).</summary>
        public IEntity Get(string name)
        {
            IEntity entity;
            return name != null && _byName.TryGetValue(name, out entity) ? entity : null;
        }

        /// <summary>
        /// Joins an entity: registers, fires OnSpawn if present. Duplicate name
        /// is a wiring bug and fails fast. Entities whose OnSpawn throws are
        /// removed again (a half-spawned entity is worse than an absent one).
        /// </summary>
        public void Spawn(IEntity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException("entity");
            }

            if (string.IsNullOrEmpty(entity.Name))
            {
                throw new ArgumentException("EntityRegistry.Spawn: entity name must not be empty.");
            }

            if (_byName.ContainsKey(entity.Name))
            {
                throw new ArgumentException(
                    "EntityRegistry.Spawn: duplicate entity name '" + entity.Name + "'.");
            }

            try
            {
                ISpawnable spawnable = entity as ISpawnable;
                if (spawnable != null)
                {
                    spawnable.OnSpawn();
                }
            }
            catch (Exception ex)
            {
                // A spawn failure means the entity never joined: report and drop.
                AiDiagnosticsHost.ReportModuleError("EntityRegistry", "spawn '" + entity.Name + "'", ex);
                return;
            }

            _entities.Add(entity);
            _byName.Add(entity.Name, entity);
        }

        /// <summary>
        /// Removes an entity: fires OnDespawn if present (failures reported, not
        /// thrown), then deregisters. Unknown names are business: returns false.
        /// </summary>
        public bool Despawn(string name)
        {
            IEntity entity = Get(name);
            if (entity == null)
            {
                return false;
            }

            IDespawnable despawnable = entity as IDespawnable;
            if (despawnable != null)
            {
                try
                {
                    despawnable.OnDespawn();
                }
                catch (Exception ex)
                {
                    AiDiagnosticsHost.ReportModuleError("EntityRegistry", "despawn '" + name + "'", ex);
                }
            }

            _entities.Remove(entity);
            _byName.Remove(name);
            return true;
        }

        /// <summary>
        /// Advances every tickable one frame with the given delta, in spawn
        /// order. One entity throwing never stops the others.
        /// </summary>
        public void Pump(float deltaSeconds)
        {
            for (int i = 0; i < _entities.Count; i++)
            {
                IEntityTickable tickable = _entities[i] as IEntityTickable;
                if (tickable == null)
                {
                    continue;
                }

                try
                {
                    tickable.EntityPump(deltaSeconds);
                }
                catch (Exception ex)
                {
                    AiDiagnosticsHost.ReportModuleError(
                        "EntityRegistry", "tick '" + _entities[i].Name + "'", ex);
                }
            }
        }

        /// <summary>Module plumbing.</summary>
        public string Name { get { return "Entities"; } }

        /// <summary>Module plumbing: depends on nothing.</summary>
        public int InitOrder { get { return 25; } }

        /// <summary>Module plumbing. Nothing to fetch.</summary>
        public void Init(ModuleCenter host)
        {
        }

        /// <summary>Module plumbing: advances with a zero delta (event-only entities).</summary>
        public void Pump()
        {
            Pump(0f);
        }

        /// <summary>Module plumbing: despawn everything, spawn order reversed.</summary>
        public void Shutdown()
        {
            for (int i = _entities.Count - 1; i >= 0; i--)
            {
                IDespawnable despawnable = _entities[i] as IDespawnable;
                if (despawnable != null)
                {
                    try
                    {
                        despawnable.OnDespawn();
                    }
                    catch (Exception ex)
                    {
                        AiDiagnosticsHost.ReportModuleError(
                            "EntityRegistry", "despawn '" + _entities[i].Name + "'", ex);
                    }
                }
            }

            _entities.Clear();
            _byName.Clear();
        }
    }
}
