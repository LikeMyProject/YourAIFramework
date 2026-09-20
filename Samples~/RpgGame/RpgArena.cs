using System.Collections.Generic;
using YourFramework.Event;
using YourFramework.Skills;
using YourFramework.Stats;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// 野外模拟（老名字 RpgArena 沿用）：玩家移动、怪物追击、攻击结算、刷怪节奏、
    /// 区域 Boss（副本守关 / 野外营地复活）—— 全部纯 C# 数学，
    /// 不碰 UnityEngine，可以在无头环境逐拍推演（tools/.rpg/ 的检查项就这么跑）。
    ///
    /// 奖励结算只有一个写入者：这里。金币 / 经验 / 掉落都由 Tick 内的击杀分支落账，
    /// 再发事件告诉别人（存档模块、界面）。多个人写同一份角色数据是 bug 温床。
    /// 每帧稳态零分配：怪列表复用、事件全是 struct。
    /// </summary>
    public sealed class RpgArena
    {
        /// <summary>世界半径：玩家与怪物都被夹在 ±Half 的方框里（60x60 的野外出得开）。</summary>
        public const float Half = 30f;
        public const int AliveMax = 8;
        public const float SpawnInterval = 2.2f;
        public const float PlayerSpeed = 5.5f;
        /// <summary>刷怪环：新怪刷在玩家周围这个半径环上 —— 太近没反应时间，太远追不到人。</summary>
        public const float SpawnRingMin = 6f;
        public const float SpawnRingMax = 12f;
        /// <summary>野外 Boss 死后到复活（秒）。传统 RPG 的世界 Boss 节奏。</summary>
        public const float WildsBossRespawn = 30f;
        public const float AttackRange = 1.9f;
        public const float AttackCooldown = 0.5f;
        public const float ContactRange = 1.0f;
        public const float MonsterAttackCooldown = 1.0f;
        /// <summary>自然回蓝速度（每秒）。技能资源靠时间回，不靠捡药。</summary>
        public const float MpRegenPerSecond = 2f;

        private readonly EventBus _bus;
        private readonly IRandomSource _random;
        private readonly RpgPlayer _player;
        private readonly DamagePipeline _damage;    // 伤害公式走框架，随机用竞技场自己的分流

        private struct MonsterBody
        {
            public RpgMonster Data;
            public float X;
            public float Z;
            public float Cooldown;
            public float Speed;
            public StatSheet Sheet;         // 攻防从属性表读（框架 StatSheet，同类共享）
        }

        private readonly List<MonsterBody> _monsters = new List<MonsterBody>(AliveMax);
        private readonly bool _bossOnly;
        private readonly string _bossId;
        private int _nextInstanceId;
        private float _spawnTimer;
        private float _attackTimer;
        private float _wildsBossTimer;

        public RpgArena(EventBus bus, IRandomSource random, RpgPlayer player) : this(bus, random, player, false, null) { }

        /// <summary>bossOnly=true 走副本规则：Boss 同场只留一只，其余刷小怪。</summary>
        public RpgArena(EventBus bus, IRandomSource random, RpgPlayer player, bool bossOnly)
            : this(bus, random, player, bossOnly, null) { }

        /// <summary>
        /// bossId：区域 Boss（副本=守关 Boss；野外=营地 Boss，死后定时复活）。
        /// 传 null 时副本默认岩石魔王、野外没有 Boss（老构造的行为，检查项兼容）。
        /// </summary>
        public RpgArena(EventBus bus, IRandomSource random, RpgPlayer player, bool bossOnly, string bossId)
        {
            _bus = bus;
            _random = random;
            _player = player;
            _bossOnly = bossOnly;
            _bossId = bossId ?? (bossOnly ? RpgMonsters.GolemKing.Id : null);
            _damage = new DamagePipeline(RpgCombat.DamageConfig, random);
            FaceZ = 1f;                 // 默认面朝 +Z，别让火球开局朝原点飞
        }

        /// <summary>区域规则（视图按这个挑地形和光照）。</summary>
        public bool BossOnly { get { return _bossOnly; } }
        public string BossId { get { return _bossId; } }

        public int AliveCount
        {
            get { return _monsters.Count; }
        }

        public int KillCountThisRun { get; private set; }

        public float PlayerX { get; private set; }
        public float PlayerZ { get; private set; }

        /// <summary>面朝方向（单位向量，默认朝 +Z）。移动时更新，火球直飞弹从这取方向。</summary>
        public float FaceX { get; private set; }
        public float FaceZ { get; private set; }

        /// <summary>玩家血量顺手转发（无头检查和 HUD 都从这里读，别到处摸 player）。</summary>
        public RpgPlayer Player
        {
            get { return _player; }
        }

        /// <summary>按序号读在场怪（统计/调试遍历用；越界返回 false）。</summary>
        public bool TryGetMonsterAt(int index, out string defId, out string name, out int hp)
        {
            if (index < 0 || index >= _monsters.Count)
            {
                defId = null;
                name = null;
                hp = 0;
                return false;
            }
            defId = _monsters[index].Data.Def.Id;
            name = _monsters[index].Data.Def.Name;
            hp = _monsters[index].Data.Hp;
            return true;
        }

        /// <summary>查询某只怪的位置（视图层每帧同步用；查无此怪返回 false）。</summary>
        public bool TryGetMonster(int instanceId, out float x, out float z, out float hpRatio, out string name)
        {
            for (int i = 0; i < _monsters.Count; i++)
            {
                if (_monsters[i].Data.InstanceId == instanceId)
                {
                    x = _monsters[i].X;
                    z = _monsters[i].Z;
                    hpRatio = (float)_monsters[i].Data.Hp / _monsters[i].Data.Def.MaxHp;
                    name = _monsters[i].Data.Def.Name;
                    return true;
                }
            }

            x = 0f;
            z = 0f;
            hpRatio = 0f;
            name = null;
            return false;
        }

        /// <summary>
        /// 收集技能目标候选：施法者自己（team 0）+ 射程内的活怪（team 1）。
        /// 填充式复用调用方的列表 —— 施放是低频操作，但规矩不破：列表不 new。
        /// 怪的 id 写成 "m" + 序号，技能效果落地时按前缀解析回序号。
        /// </summary>
        public void CollectSkillCandidates(List<SkillTargetCandidate> into, float range)
        {
            into.Add(new SkillTargetCandidate("hero", 0, !_player.Dead, 0f,
                _player.MaxHp <= 0 ? 0f : (float)_player.Hp / _player.MaxHp));

            float rangeSq = range * range;
            for (int i = 0; i < _monsters.Count; i++)
            {
                float dx = _monsters[i].X - PlayerX;
                float dz = _monsters[i].Z - PlayerZ;
                float distSq = dx * dx + dz * dz;
                if (distSq <= rangeSq)
                {
                    RpgMonster data = _monsters[i].Data;
                    into.Add(new SkillTargetCandidate("m" + data.InstanceId, 1, data.Hp > 0,
                        (float)System.Math.Sqrt(distSq),
                        data.Def.MaxHp <= 0 ? 0f : (float)data.Hp / data.Def.MaxHp));
                }
            }
        }

        /// <summary>离玩家最近的活怪序号（火球术自动锁最近用）。没有怪返回 false。</summary>
        public bool TryNearestMonsterId(out int instanceId)
        {
            int best = -1;
            float bestDistSq = float.MaxValue;
            for (int i = 0; i < _monsters.Count; i++)
            {
                float dx = _monsters[i].X - PlayerX;
                float dz = _monsters[i].Z - PlayerZ;
                float distSq = dx * dx + dz * dz;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = _monsters[i].Data.InstanceId;
                }
            }

            instanceId = best;
            return best >= 0;
        }

        /// <summary>某点半径内最近的活怪（火球这类直飞弹的碰撞检查用）。纯几何，无头可测。</summary>
        public bool TryMonsterInRadius(float x, float z, float radius, out int instanceId)
        {
            int best = -1;
            float bestDistSq = float.MaxValue;
            float radiusSq = radius * radius;
            for (int i = 0; i < _monsters.Count; i++)
            {
                if (_monsters[i].Data.Hp <= 0)
                {
                    continue;               // 尸体不挡弹
                }

                float dx = _monsters[i].X - x;
                float dz = _monsters[i].Z - z;
                float distSq = dx * dx + dz * dz;
                if (distSq <= radiusSq && distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = _monsters[i].Data.InstanceId;
                }
            }

            instanceId = best;
            return best >= 0;
        }

        /// <summary>开一局：清场、玩家归位回满血。角色等级/装备/背包**保留**（RPG 的成长跨局存在）。</summary>
        public void ResetRun()
        {
            _monsters.Clear();
            // id 只进不退：视图层字典按 id 记账，重置会造成重复键（踩过）。
            // 清场通知走 RpgRunStartedEvent，视图收到就归还全部池对象。
            _spawnTimer = SpawnInterval * 0.5f;     // 开局半拍后来第一只，别让玩家干等
            _attackTimer = 0f;
            _wildsBossTimer = 0f;
            KillCountThisRun = 0;
            PlayerX = 0f;
            PlayerZ = 0f;
            _player.RestoreFull();
            if (!_bossOnly && _bossId != null)
            {
                SpawnBossAtCamp();                  // 野外开局，Boss 就在自己的营地候着
            }
            _bus.Publish(new RpgRunStartedEvent());
        }

        /// <summary>视野朝向（瞄准用）：宿主每帧把相机朝向喂进来，火球/普攻朝它走。
        /// 与移动无关 —— 面向和走位分离，才是传统 RPG 的手感。</summary>
        public void SetFacing(float x, float z)
        {
            float len = (float)System.Math.Sqrt(x * x + z * z);
            if (len > 0.0001f)
            {
                FaceX = x / len;
                FaceZ = z / len;
            }
        }

        /// <summary>野外 Boss 营地：世界东南角的空场（视图在这里摆石头阵做标记）。</summary>
        public static void BossCamp(out float x, out float z)
        {
            x = Half * 0.6f;
            z = Half * 0.6f;
        }

        private bool IsBossAlive(string bossId)
        {
            for (int i = 0; i < _monsters.Count; i++)
            {
                if (_monsters[i].Data.Def.Id == bossId)
                {
                    return true;
                }
            }

            return false;
        }

        private void SpawnBossAtCamp()
        {
            float x;
            float z;
            BossCamp(out x, out z);
            RpgMonsterDef def = RpgMonsters.Find(_bossId);
            _nextInstanceId++;
            RpgMonster monster = new RpgMonster
            {
                InstanceId = _nextInstanceId,
                Def = def,
                Hp = def.MaxHp,
            };
            _monsters.Add(new MonsterBody
            {
                Data = monster,
                X = x,
                Z = z,
                Cooldown = 0.6f,
                Speed = 1.6f * def.SpeedScale,
                Sheet = RpgMonsters.SheetOf(def),
            });
            _bus.Publish(new RpgMonsterSpawnedEvent { InstanceId = monster.InstanceId, Name = def.Name, DefId = def.Id });
        }

        /// <summary>推进一帧。dir 是移动意图（长度不必归一，内部归一）；attack 为 true 表示这帧想出手。
        /// 玩家倒下后世界停摆：不再移动、不再结算，死亡事件也就不会重复发。</summary>
        public void Tick(float dt, float dirX, float dirZ, bool attackRequested)
        {
            if (_player.Dead)
            {
                return;
            }

            _player.RestoreMp(MpRegenPerSecond * dt);
            MovePlayer(dt, dirX, dirZ);

            _attackTimer -= dt;
            if (attackRequested && _attackTimer <= 0f)
            {
                TryAttack();
            }

            TickMonsters(dt);
            TickSpawning(dt);
        }

        private void MovePlayer(float dt, float dirX, float dirZ)
        {
            float len = (float)System.Math.Sqrt(dirX * dirX + dirZ * dirZ);
            if (len > 0.0001f)
            {
                PlayerX += dirX / len * PlayerSpeed * dt;
                PlayerZ += dirZ / len * PlayerSpeed * dt;
                ClampToArena();
            }
        }

        private void ClampToArena()
        {
            if (PlayerX > Half) { PlayerX = Half; }
            if (PlayerX < -Half) { PlayerX = -Half; }
            if (PlayerZ > Half) { PlayerZ = Half; }
            if (PlayerZ < -Half) { PlayerZ = -Half; }
        }

        private void TryAttack()
        {
            int bestIndex = -1;
            float bestDistSq = AttackRange * AttackRange;
            for (int i = 0; i < _monsters.Count; i++)
            {
                float dx = _monsters[i].X - PlayerX;
                float dz = _monsters[i].Z - PlayerZ;
                float distSq = dx * dx + dz * dz;
                if (distSq <= bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                return;                             // 空挥不算数：不进冷却，连打不亏
            }

            MonsterBody body = _monsters[bestIndex];
            DamageResult result = _damage.Resolve(_player.Stats, body.Sheet, DamageRequest.Of(1f));
            if (!result.Ok)
            {
                return;                             // 攻方缺属性是装配错（配置对不上），不出手不进冷却
            }

            _attackTimer = AttackCooldown;
            ApplyDamageTo(bestIndex, result.Amount);
        }

        /// <summary>直伤入口：不带公式的定值扣血（检查项/特殊规则用）。</summary>
        public bool DamageMonsterById(int instanceId, int amount)
        {
            int index = IndexOfMonster(instanceId);
            if (index < 0)
            {
                return false;
            }

            ApplyDamageTo(index, amount);
            return true;
        }

        /// <summary>
        /// 技能/火球伤害入口：倍率与固定加成由技能表给，**公式走框架
        /// <see cref="DamagePipeline"/>**（随机用竞技场自己的分流，同种子可复现）。
        /// 查无此怪（已死）返回 false，dealt=0。
        /// </summary>
        public bool DamageMonsterById(int instanceId, DamageRequest request, out int dealt)
        {
            int index = IndexOfMonster(instanceId);
            if (index < 0)
            {
                dealt = 0;
                return false;
            }

            DamageResult result = _damage.Resolve(_player.Stats, _monsters[index].Sheet, request);
            if (!result.Ok)
            {
                dealt = 0;
                return false;
            }

            dealt = result.Amount;
            ApplyDamageTo(index, dealt);
            return true;
        }

        private int IndexOfMonster(int instanceId)
        {
            for (int i = 0; i < _monsters.Count; i++)
            {
                if (_monsters[i].Data.InstanceId == instanceId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>落伤 + 击杀结算的唯一写入点（事件从这发出，存档/界面各取所需）。</summary>
        private void ApplyDamageTo(int index, int amount)
        {
            MonsterBody body = _monsters[index];
            body.Data.Hp -= amount;
            bool died = body.Data.Hp <= 0;
            _bus.Publish(new RpgMonsterHurtEvent
            {
                InstanceId = body.Data.InstanceId,
                Damage = amount,
                Died = died,
                HpLeft = died ? 0 : body.Data.Hp,
            });

            if (died)
            {
                SettleKill(index);
            }
        }

        private void SettleKill(int index)
        {
            MonsterBody body = _monsters[index];
            _monsters.RemoveAt(index);

            int gold = RpgCombat.RollGold(_random, body.Data.Def);
            int levels = _player.AddExp(body.Data.Def.ExpReward);
            _player.AddGold(gold);

            string dropName = null;
            string dropId = body.Data.Def.DropId;
            if (dropId != null && _random.Chance(body.Data.Def.DropChance))
            {
                RpgItemDef drop = RpgItems.Find(dropId);
                bool taken = _player.TryAddItem(dropId, 1);
                _bus.Publish(new RpgItemGainedEvent
                {
                    DefId = dropId,
                    Name = drop.Name,
                    Count = 1,
                    Rejected = !taken,
                });
                if (taken)
                {
                    dropName = drop.Name;
                }
            }

            KillCountThisRun++;
            _bus.Publish(new RpgMonsterDiedEvent
            {
                InstanceId = body.Data.InstanceId,
                Name = body.Data.Def.Name,
                DefId = body.Data.Def.Id,
                Exp = body.Data.Def.ExpReward,
                Gold = gold,
                DropName = dropName,
            });

            for (int i = 0; i < levels; i++)
            {
                _bus.Publish(new RpgPlayerLeveledUpEvent { Level = _player.Level - levels + 1 + i });
            }

            _bus.Publish(new RpgStatsChangedEvent());
        }

        private void TickMonsters(float dt)
        {
            bool playerDead = false;
            for (int i = _monsters.Count - 1; i >= 0; i--)
            {
                MonsterBody body = _monsters[i];
                float dx = PlayerX - body.X;
                float dz = PlayerZ - body.Z;
                float dist = (float)System.Math.Sqrt(dx * dx + dz * dz);

                if (dist > ContactRange * 0.9f)
                {
                    body.X += dx / dist * body.Speed * dt;
                    body.Z += dz / dist * body.Speed * dt;
                }

                // 冷却扣完就写回：MonsterBody 是 struct，不写回的话列表里永远停在初值，
                // 怪会贴着玩家站着却永远咬不下去（无头检查抓出来的真 bug）。
                body.Cooldown -= dt;
                _monsters[i] = body;
                if (dist <= ContactRange && body.Cooldown <= 0f && !playerDead)
                {
                    body.Cooldown = MonsterAttackCooldown;
                    _monsters[i] = body;

                    DamageResult hit = _damage.Resolve(body.Sheet, _player.Stats, DamageRequest.Of(1f));
                    int damage = hit.Ok ? hit.Amount : 1;
                    bool died = _player.TakeDamage(damage);
                    _bus.Publish(new RpgPlayerHurtEvent
                    {
                        Damage = damage,
                        Hp = _player.Hp,
                        MaxHp = _player.MaxHp,
                    });

                    if (died)
                    {
                        playerDead = true;
                    }
                }
            }

            if (playerDead)
            {
                _bus.Publish(new RpgPlayerDiedEvent());
            }
        }

        private void TickSpawning(float dt)
        {
            // 野外 Boss 的世界节奏：死了别急着复活，30 秒后再回营地
            if (!_bossOnly && _bossId != null && !IsBossAlive(_bossId))
            {
                _wildsBossTimer += dt;
                if (_wildsBossTimer >= WildsBossRespawn)
                {
                    _wildsBossTimer = 0f;
                    SpawnBossAtCamp();
                }
            }

            _spawnTimer += dt;
            if (_spawnTimer < SpawnInterval || _monsters.Count >= AliveMax)
            {
                return;
            }

            _spawnTimer = 0f;
            SpawnOne();
        }

        /// <summary>刷一只：种类按玩家等级加权，位置刷在玩家周围的环上 ——
        /// 太近没反应时间，太远追不到人；大世界里贴场边刷等于刷给空气看。</summary>
        private void SpawnOne()
        {
            RpgMonsterDef def = PickMonsterDef();
            float ang = (float)(_random.NextUnit() * System.Math.PI * 2.0);
            float r = SpawnRingMin + (float)_random.NextUnit() * (SpawnRingMax - SpawnRingMin);
            float x = PlayerX + (float)System.Math.Sin(ang) * r;
            float z = PlayerZ + (float)System.Math.Cos(ang) * r;
            if (x > Half - 1f) { x = Half - 1f; }
            if (x < -Half + 1f) { x = -Half + 1f; }
            if (z > Half - 1f) { z = Half - 1f; }
            if (z < -Half + 1f) { z = -Half + 1f; }

            _nextInstanceId++;
            RpgMonster monster = new RpgMonster
            {
                InstanceId = _nextInstanceId,
                Def = def,
                Hp = def.MaxHp,
            };

            _monsters.Add(new MonsterBody
            {
                Data = monster,
                X = x,
                Z = z,
                Cooldown = 0.6f,            // 落地半拍后才咬第一口
                Speed = (1.6f + (float)_random.NextUnit() * 0.5f) * def.SpeedScale,
                Sheet = RpgMonsters.SheetOf(def),
            });

            _bus.Publish(new RpgMonsterSpawnedEvent { InstanceId = monster.InstanceId, Name = def.Name, DefId = def.Id });
        }

        private RpgMonsterDef PickMonsterDef()
        {
            if (_bossOnly)
            {
                // 副本：Boss 在场就不重复刷；其余名额刷小石魔陪练
                if (IsBossAlive(_bossId))
                {
                    return RpgMonsters.Golem;
                }

                return RpgMonsters.Find(_bossId);
            }

            double roll = _random.NextUnit();
            if (_player.Level <= 2)
            {
                return roll < 0.5 ? RpgMonsters.Slime : roll < 0.8 ? RpgMonsters.Wolf : RpgMonsters.Spider;
            }

            if (_player.Level <= 4)
            {
                return roll < 0.25 ? RpgMonsters.Slime : roll < 0.55 ? RpgMonsters.Wolf
                    : roll < 0.8 ? RpgMonsters.Spider : RpgMonsters.Boar;
            }

            return roll < 0.1 ? RpgMonsters.Slime : roll < 0.35 ? RpgMonsters.Wolf
                : roll < 0.55 ? RpgMonsters.Spider : roll < 0.8 ? RpgMonsters.Boar : RpgMonsters.Golem;
        }
    }
}
