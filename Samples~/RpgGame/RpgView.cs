using System.Collections.Generic;
using UnityEngine;
using YourFramework.Event;
using YourFramework.Pool;
using YourFramework.UnityRuntime;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// 视图：把竞技场状态同步到程序化生成的几何体上 —— 传统 RPG 的第三人称跟随相机，
    /// 怪按种类拼装（史莱姆绿团子、野狼灰狼身、毒蛛八条腿、野猪獠牙、石魔堆石头、
    /// 两个 Boss 各有行头），野外是草地树林大地图、副本是黑石洞窟火把石柱。
    /// 全是基本体拼的，不依赖任何美术资产。
    ///
    /// 对象池教学的落点：怪死了回池子、刷了再借，<see cref="ObjectPool{T}"/> 借还配平，
    /// 多借少还当场抛错。挂掉怪身上的视图对象跟着回池，长跑不漏。
    /// 怪的生死靠事件驱动（Spawned → 借、Died → 还），每帧只同步位置和颜色。
    /// </summary>
    public sealed class RpgView : MonoBehaviour
    {
        private RpgArena _arena;
        private EventBus _bus;
        private RpgControl _control;
        private Camera _cam;
        private System.Action<string> _onProjectileHit;
        private GameObjectPool _monsterPool;        // 怪壳池（框架 Unity 池：借还配平，满员销毁）
        private GameObjectPool _fireballPool;       // 火球弹池
        private readonly Dictionary<int, GameObject> _active = new Dictionary<int, GameObject>(16);
        private readonly Dictionary<int, Renderer> _mainRends = new Dictionary<int, Renderer>(16);
        private readonly Dictionary<int, Color> _mainColors = new Dictionary<int, Color>(16);
        private GameObject _playerBody;
        private Transform _root;
        private float _attackFlash;

        // ---- 交互点（回城传送石）：走近出提示，按 F 触发，宿主接 ----
        private struct InteractPoint { public float X, Z, Radius; public string Id, Label; }
        private readonly List<InteractPoint> _points = new List<InteractPoint>(2);
        private string _currentId;
        private string _currentLabel;

        /// <summary>当前站着的交互点（不在任何点边上为 null）。</summary>
        public string CurrentInteractionId { get { return _currentId; } }
        public string CurrentInteractionLabel { get { return _currentLabel; } }

        /// <summary>加一个交互点并搭出它的视觉（野外回城传送石）。副本不加，进出只认死亡与通关。</summary>
        public void AddInteractPoint(float x, float z, float radius, string id, string label)
        {
            _points.Add(new InteractPoint { X = x, Z = z, Radius = radius, Id = id, Label = label });
            MakeReturnStone(x, z);
        }

        // ---- 火球直飞弹参数 ----
        private const float FireballSpeed = 7f;         // 每秒 7 格，横穿半场约一秒多
        private const float FireballLifetime = 10f;     // 没撞上 10 秒自毁
        private const float FireballHitRadius = 0.8f;   // 弹体中心进怪这个半径算撞上

        // ---- 技能特效（纯程序化几何体，事件驱动，到点自毁）----
        private struct FlyFx { public GameObject Go; public float DirX; public float DirZ; public float T; }
        private readonly List<FlyFx> _flies = new List<FlyFx>(8);           // 火球（直线飞，撞怪结算）
        private struct SpinFx { public GameObject Go; public float Phase; public float T; }
        private readonly List<SpinFx> _blades = new List<SpinFx>(12);       // 旋风刃
        private struct PulseFx { public GameObject Go; public float T; }
        private readonly List<PulseFx> _pillars = new List<PulseFx>(4);     // 治疗光柱
        private readonly List<float> _delayedHeals = new List<float>(4);    // 治疗读条倒计时

        public void Init(RpgArena arena, EventBus bus, System.Action<string> onProjectileHit, RpgControl control)
        {
            _arena = arena;
            _bus = bus;
            _onProjectileHit = onProjectileHit;
            _control = control;
            // 相机先取好再用 —— MakeTerrain 末尾的 ApplyAtmosphere 要设天空色/雾，
            // 相机放在后面取就是城镇视图同款的首帧空引用（两个视图一起踩过）
            _cam = Camera.main;
            _cam.orthographic = false;
            _cam.fieldOfView = 60f;
            _cam.farClipPlane = 400f;
            _root = new GameObject("[RpgView]").transform;
            MakeTerrain();
            MakePlayer();

            // 对象池走框架 GameObjectPool（Unity 实例池）：怪壳是"空壳 prefab"，
            // 形状借出来再按种类拼；火球弹同池思路单独一池。
            GameObject monsterShell = new GameObject("MonsterShell");
            monsterShell.SetActive(false);
            _monsterPool = new GameObjectPool(monsterShell, _root);

            GameObject fireballPrefab = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            fireballPrefab.name = "Fireball";
            fireballPrefab.transform.localScale = new Vector3(0.45f, 0.45f, 0.45f);
            fireballPrefab.GetComponent<Renderer>().material.color = new Color(1f, 0.45f, 0.1f);
            Object.Destroy(fireballPrefab.GetComponent<Collider>());    // 特效不参与物理
            fireballPrefab.SetActive(false);
            _fireballPool = new GameObjectPool(fireballPrefab, _root);

            _bus.Subscribe<RpgMonsterSpawnedEvent>(OnSpawned);
            _bus.Subscribe<RpgMonsterDiedEvent>(OnDied);
            _bus.Subscribe<RpgMonsterHurtEvent>(OnHurt);
            _bus.Subscribe<RpgRunStartedEvent>(OnRunStarted);
            _bus.Subscribe<RpgSkillCastEvent>(OnSkillCast);
        }

        private void OnDestroy()
        {
            if (_bus != null)
            {
                _bus.Unsubscribe<RpgMonsterSpawnedEvent>(OnSpawned);
                _bus.Unsubscribe<RpgMonsterDiedEvent>(OnDied);
                _bus.Unsubscribe<RpgMonsterHurtEvent>(OnHurt);
                _bus.Unsubscribe<RpgRunStartedEvent>(OnRunStarted);
                _bus.Unsubscribe<RpgSkillCastEvent>(OnSkillCast);
            }

            if (_monsterPool != null)
            {
                _monsterPool.Clear();
            }

            if (_fireballPool != null)
            {
                _fireballPool.Clear();
            }
        }

        private void LateUpdate()
        {
            // 玩家与怪的姿态每帧同步；事件只负责"生与死"，位置交给状态。
            _playerBody.transform.position = new Vector3(_arena.PlayerX, 0.75f, _arena.PlayerZ);
            Vector3 face = new Vector3(_arena.FaceX, 0f, _arena.FaceZ);
            if (face.sqrMagnitude > 0.0001f)
            {
                _playerBody.transform.rotation = Quaternion.LookRotation(face, Vector3.up);
            }

            UpdateInteraction();
            _attackFlash -= Time.deltaTime;
            float pulse = _attackFlash > 0f ? 1f + _attackFlash * 0.6f : 1f;
            _playerBody.transform.localScale = new Vector3(pulse, 1f, pulse);

            // 第三人称跟随相机：焦点锁玩家胸口，机位 = 焦点 - 面朝 * 距离 + 俯仰抬升
            float yawRad = _control.Yaw * Mathf.Deg2Rad;
            float pitchRad = _control.CamPitch * Mathf.Deg2Rad;
            float cosP = Mathf.Cos(pitchRad);
            Vector3 focus = new Vector3(_arena.PlayerX, 1.5f, _arena.PlayerZ);
            Vector3 camPos = focus + new Vector3(-Mathf.Sin(yawRad) * cosP, Mathf.Sin(pitchRad), -Mathf.Cos(yawRad) * cosP) * _control.CamDist;
            _cam.transform.position = camPos;
            _cam.transform.rotation = Quaternion.LookRotation(focus - camPos, Vector3.up);

            AdvanceSkillFx();
            foreach (KeyValuePair<int, GameObject> pair in _active)
            {
                float x;
                float z;
                float hpRatio;
                string name;
                if (!_arena.TryGetMonster(pair.Key, out x, out z, out hpRatio, out name))
                {
                    continue;
                }

                GameObject body = pair.Value;
                body.transform.position = new Vector3(x, 0.02f, z);
                // 满血本色、残血变暗（只染主零件，五官配饰保持原色）；
                // 刚出生从 0 长大，一眼看出是新来的。
                Renderer mainRend;
                Color baseColor;
                if (_mainRends.TryGetValue(pair.Key, out mainRend) && _mainColors.TryGetValue(pair.Key, out baseColor))
                {
                    mainRend.material.color = baseColor * (0.55f + 0.45f * hpRatio);
                }
                Vector3 scale = body.transform.localScale;
                if (scale.x < 1f)
                {
                    scale = scale + Vector3.one * Time.deltaTime * 3f;
                    body.transform.localScale = Vector3.Min(scale, Vector3.one);
                }
            }
        }

        /// <summary>开局清场：旧怪没机会发死亡事件（ResetRun 直接清列表），
        /// 这里统一归还池，账本从零开始。</summary>
        private void OnRunStarted(RpgRunStartedEvent evt)
        {
            foreach (KeyValuePair<int, GameObject> pair in _active)
            {
                _monsterPool.Release(pair.Value);
            }
            _active.Clear();
            _mainRends.Clear();
            _mainColors.Clear();
        }

        private void OnSpawned(RpgMonsterSpawnedEvent evt)
        {
            GameObject stale;
            if (_active.TryGetValue(evt.InstanceId, out stale))
            {
                _monsterPool.Release(stale);    // 防御：id 理论上唯一，真撞了也别炸
            }
            GameObject body = _monsterPool.Get();
            BuildMonsterBody(evt.InstanceId, body, evt.DefId);
            body.transform.localScale = Vector3.zero;       // 出生长大动画的起点
            _active[evt.InstanceId] = body;
        }

        private void OnDied(RpgMonsterDiedEvent evt)
        {
            GameObject body;
            if (_active.TryGetValue(evt.InstanceId, out body))
            {
                _active.Remove(evt.InstanceId);
                _mainRends.Remove(evt.InstanceId);
                _mainColors.Remove(evt.InstanceId);
                _monsterPool.Release(body);
            }
        }

        private void OnHurt(RpgMonsterHurtEvent evt)
        {
            _attackFlash = 0.12f;                           // 玩家出手脉冲
        }

        // ------------------------------------------------------------------ 技能特效

        private void OnSkillCast(RpgSkillCastEvent evt)
        {
            if (evt.Failed || evt.Completed) return;        // 生效总账：特效已在起手放过

            if (evt.SkillId == "fireball")
            {
                // 直飞弹：朝面朝方向飞，撞到怪才结算（回调宿主 → 技能模块），10 秒没中自毁
                LaunchFireball();
                return;
            }
            if (evt.SkillId == "whirlwind")
            {
                SpawnWhirlwind();
                return;
            }
            if (evt.SkillId == "mend")
            {
                // 治疗在吟唱结束时生效：倒计时到点再放绿光
                _delayedHeals.Add(evt.CastTime <= 0f ? 0.1f : evt.CastTime);
            }
        }

        /// <summary>火球：橙色小球从玩家胸口射出，沿面朝方向直线飞。
        /// 弹体从框架池借（<see cref="GameObjectPool"/>），每帧查碰撞，
        /// 撞到怪回调宿主结算伤害并还池；10 秒没中也还池。</summary>
        private void LaunchFireball()
        {
            GameObject ball = _fireballPool.Get();
            ball.transform.position = new Vector3(_arena.PlayerX, 0.9f, _arena.PlayerZ);

            FlyFx fx;
            fx.Go = ball;
            fx.DirX = _arena.FaceX;
            fx.DirZ = _arena.FaceZ;
            fx.T = 0f;
            _flies.Add(fx);
        }

        /// <summary>旋风斩：四道白色风刃绕玩家转一圈，0.4 秒散去。</summary>
        private void SpawnWhirlwind()
        {
            for (int i = 0; i < 4; i++)
            {
                GameObject blade = GameObject.CreatePrimitive(PrimitiveType.Cube);
                blade.name = "FxBlade";
                blade.transform.SetParent(_root, false);
                blade.transform.localScale = new Vector3(0.12f, 0.08f, 2.4f);
                blade.GetComponent<Renderer>().material.color = new Color(0.9f, 0.97f, 1f);
                Object.Destroy(blade.GetComponent<Collider>());

                SpinFx fx;
                fx.Go = blade;
                fx.Phase = i * (Mathf.PI * 2f / 4f);
                fx.T = 0f;
                _blades.Add(fx);
            }
        }

        /// <summary>治疗术：绿色光柱罩着玩家呼吸两下，0.8 秒收掉。</summary>
        private void SpawnHealPillar()
        {
            GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            pillar.name = "FxHeal";
            pillar.transform.SetParent(_root, false);
            pillar.transform.position = new Vector3(_arena.PlayerX, 1.2f, _arena.PlayerZ);
            pillar.transform.localScale = new Vector3(1.3f, 1.6f, 1.3f);
            pillar.GetComponent<Renderer>().material.color = new Color(0.35f, 1f, 0.5f, 1f);
            Object.Destroy(pillar.GetComponent<Collider>());

            PulseFx fx;
            fx.Go = pillar;
            fx.T = 0f;
            _pillars.Add(fx);
        }

        /// <summary>特效每帧推进（视图层自己的表现账，跟竞技场状态无关）。</summary>
        private void AdvanceSkillFx()
        {
            float dt = Time.deltaTime;

            // 治疗读条：到点放绿光（与伤害/回血落地同一时刻）
            for (int i = _delayedHeals.Count - 1; i >= 0; i--)
            {
                _delayedHeals[i] -= dt;
                if (_delayedHeals[i] <= 0f)
                {
                    _delayedHeals.RemoveAt(i);
                    SpawnHealPillar();
                }
            }

            for (int i = _flies.Count - 1; i >= 0; i--)
            {
                FlyFx fx = _flies[i];
                fx.T += dt;
                if (fx.T >= FireballLifetime)
                {
                    _fireballPool.Release(fx.Go);       // 没打中：飞够 10 秒还池
                    _flies.RemoveAt(i);
                    continue;
                }

                Vector3 pos = fx.Go.transform.position;
                pos += new Vector3(fx.DirX, 0f, fx.DirZ) * FireballSpeed * dt;
                fx.Go.transform.position = pos;

                int hitId;
                if (_onProjectileHit != null && _arena.TryMonsterInRadius(pos.x, pos.z, FireballHitRadius, out hitId))
                {
                    _onProjectileHit("m" + hitId);      // 撞上这一刻才掉血
                    _fireballPool.Release(fx.Go);
                    _flies.RemoveAt(i);
                    continue;
                }
                _flies[i] = fx;
            }

            for (int i = _blades.Count - 1; i >= 0; i--)
            {
                SpinFx fx = _blades[i];
                fx.T += dt;
                const float spinTime = 0.4f;
                if (fx.T >= spinTime)
                {
                    Destroy(fx.Go);
                    _blades.RemoveAt(i);
                    continue;
                }
                float angle = fx.Phase + fx.T / spinTime * Mathf.PI * 2f;
                float radius = 1.5f;
                Vector3 center = new Vector3(_arena.PlayerX, 0.7f, _arena.PlayerZ);
                fx.Go.transform.position = center + new Vector3(Mathf.Sin(angle) * radius, 0f, Mathf.Cos(angle) * radius);
                fx.Go.transform.rotation = Quaternion.Euler(0f, -angle * Mathf.Rad2Deg, 0f);
                _blades[i] = fx;
            }

            for (int i = _pillars.Count - 1; i >= 0; i--)
            {
                PulseFx fx = _pillars[i];
                fx.T += dt;
                const float pulseTime = 0.8f;
                if (fx.T >= pulseTime)
                {
                    Destroy(fx.Go);
                    _pillars.RemoveAt(i);
                    continue;
                }
                float breath = 1f + Mathf.Sin(fx.T / pulseTime * Mathf.PI * 3f) * 0.15f;
                float fade = 1f - fx.T / pulseTime;
                fx.Go.transform.localScale = new Vector3(1.3f * breath, 1.6f * fade + 0.4f, 1.3f * breath);
                fx.Go.transform.position = new Vector3(_arena.PlayerX, 0.8f * fade + 0.4f, _arena.PlayerZ);
                _pillars[i] = fx;
            }
        }

        /// <summary>地形按区域走：野外=草地树林大地图，副本=黑石洞窟。
        /// 全是基本体拼的，摆放用固定规律的散布（黄金角），不用随机数，重开一模一样。</summary>
        private void MakeTerrain()
        {
            bool cave = _arena.BossOnly;
            float span = RpgArena.Half * 2f + 8f;

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.SetParent(_root, false);
            ground.transform.position = new Vector3(0f, -0.15f, 0f);
            ground.transform.localScale = new Vector3(span, 0.3f, span);
            ground.GetComponent<Renderer>().material.color = cave
                ? new Color(0.22f, 0.2f, 0.19f)             // 洞窟黑石地
                : new Color(0.38f, 0.56f, 0.28f);           // 野外的草

            if (cave)
            {
                MakeCaveDressing();
            }
            else
            {
                MakeWildsDressing();
            }

            ApplyAtmosphere(cave);
        }

        /// <summary>野外布置：树林、石头、草丛，外加东南角的 Boss 营地（石头阵 + 黑祭坛）。</summary>
        private void MakeWildsDressing()
        {
            float campX;
            float campZ;
            RpgArena.BossCamp(out campX, out campZ);

            for (int i = 0; i < 34; i++)
            {
                // 黄金角散布：均匀又不呆板，重开草-tree 长在同一处
                float ang = i * 137.5f * Mathf.Deg2Rad;
                float r = 12f + (i % 7) * 2.6f;
                float x = Mathf.Sin(ang) * r;
                float z = Mathf.Cos(ang) * r;
                if ((x - campX) * (x - campX) + (z - campZ) * (z - campZ) < 30f)
                {
                    continue;                               // 营地附近留空
                }

                MakeTree(x, z, 1f + (i % 4) * 0.25f);
            }

            for (int i = 0; i < 12; i++)
            {
                float ang = i * 95f * Mathf.Deg2Rad;
                float r = 7f + (i % 5) * 3.7f;
                MakePart(_root, "Rock", PrimitiveType.Sphere,
                    new Vector3(Mathf.Sin(ang) * r, 0.25f, Mathf.Cos(ang) * r),
                    new Vector3(1.1f, 0.6f, 0.9f), new Color(0.5f, 0.49f, 0.47f));
            }

            for (int i = 0; i < 10; i++)
            {
                float ang = (i + 0.5f) * 71f * Mathf.Deg2Rad;
                float r = 9f + (i % 6) * 3.1f;
                MakePart(_root, "Bush", PrimitiveType.Sphere,
                    new Vector3(Mathf.Sin(ang) * r, 0.3f, Mathf.Cos(ang) * r),
                    new Vector3(0.9f, 0.6f, 0.9f), new Color(0.26f, 0.45f, 0.2f));
            }

            // Boss 营地：一圈立石 + 黑祭坛，远远就能认出"这儿有事发生"
            for (int i = 0; i < 8; i++)
            {
                float ang = i * Mathf.PI * 2f / 8f;
                MakePart(_root, "CampStone", PrimitiveType.Cube,
                    new Vector3(campX + Mathf.Sin(ang) * 3.6f, 0.7f, campZ + Mathf.Cos(ang) * 3.6f),
                    new Vector3(0.6f, 1.4f, 0.6f), new Color(0.42f, 0.4f, 0.38f));
            }

            MakePart(_root, "CampAltar", PrimitiveType.Cylinder,
                new Vector3(campX, 0.2f, campZ), new Vector3(2.6f, 0.2f, 2.6f), new Color(0.3f, 0.24f, 0.22f));

            // 远景山丘：一圈压扁的绿球贴在世界边缘外，走路撞不到但看得见"世界有尽头"
            for (int i = 0; i < 14; i++)
            {
                float ang = i * Mathf.PI * 2f / 14f;
                float r = RpgArena.Half + 6f;
                MakePart(_root, "Hill", PrimitiveType.Sphere,
                    new Vector3(Mathf.Sin(ang) * r, -1.5f, Mathf.Cos(ang) * r),
                    new Vector3(13f, 6f, 13f), new Color(0.3f, 0.42f, 0.26f));
            }
        }

        /// <summary>洞窟布置：四面高墙 + 两圈石柱 + 一圈火把，光照氛围靠雾压暗。</summary>
        private void MakeCaveDressing()
        {
            Color wallColor = new Color(0.3f, 0.27f, 0.26f);
            float span = RpgArena.Half * 2f + 3f;
            MakeWall("WallN", new Vector3(0f, 1.75f, RpgArena.Half + 0.5f), new Vector3(span, 3.5f, 1f), wallColor);
            MakeWall("WallS", new Vector3(0f, 1.75f, -RpgArena.Half - 0.5f), new Vector3(span, 3.5f, 1f), wallColor);
            MakeWall("WallW", new Vector3(-RpgArena.Half - 0.5f, 1.75f, 0f), new Vector3(1f, 3.5f, span), wallColor);
            MakeWall("WallE", new Vector3(RpgArena.Half + 0.5f, 1.75f, 0f), new Vector3(1f, 3.5f, span), wallColor);

            for (int ring = 0; ring < 2; ring++)
            {
                int count = ring == 0 ? 8 : 13;
                float r = ring == 0 ? 9f : 17f;
                for (int i = 0; i < count; i++)
                {
                    float ang = i * Mathf.PI * 2f / count + ring * 0.35f;
                    MakePart(_root, "Pillar", PrimitiveType.Cylinder,
                        new Vector3(Mathf.Sin(ang) * r, 1.6f, Mathf.Cos(ang) * r),
                        new Vector3(0.8f, 1.6f, 0.8f), new Color(0.36f, 0.33f, 0.31f));
                }
            }

            for (int i = 0; i < 12; i++)
            {
                float ang = i * Mathf.PI * 2f / 12f;
                float r = RpgArena.Half - 2.5f;
                float x = Mathf.Sin(ang) * r;
                float z = Mathf.Cos(ang) * r;
                MakePart(_root, "TorchPole", PrimitiveType.Cylinder,
                    new Vector3(x, 0.55f, z), new Vector3(0.08f, 1.1f, 0.08f), new Color(0.35f, 0.26f, 0.18f));
                MakePart(_root, "TorchFlame", PrimitiveType.Sphere,
                    new Vector3(x, 1.25f, z), Vector3.one * 0.26f, new Color(1f, 0.55f, 0.12f));
            }
        }

        private void MakeTree(float x, float z, float scale)
        {
            MakePart(_root, "TreeTrunk", PrimitiveType.Cylinder,
                new Vector3(x, 0.7f * scale, z), new Vector3(0.34f * scale, 1.4f * scale, 0.34f * scale),
                new Color(0.4f, 0.29f, 0.18f));
            MakePart(_root, "TreeCrown", PrimitiveType.Sphere,
                new Vector3(x, 1.9f * scale, z), new Vector3(1.9f * scale, 1.7f * scale, 1.9f * scale),
                new Color(0.22f + 0.03f * (scale - 1f), 0.48f, 0.2f));
        }

        /// <summary>光照氛围：野外亮而开阔，洞窟浓雾压暗到"只见火把那一片"。</summary>
        private void ApplyAtmosphere(bool cave)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            if (cave)
            {
                RenderSettings.fogColor = new Color(0.05f, 0.05f, 0.07f);
                RenderSettings.fogStartDistance = 5f;
                RenderSettings.fogEndDistance = 32f;
                RenderSettings.ambientLight = new Color(0.3f, 0.27f, 0.27f);
                _cam.backgroundColor = new Color(0.04f, 0.04f, 0.06f);
            }
            else
            {
                RenderSettings.fogColor = new Color(0.72f, 0.82f, 0.9f);
                RenderSettings.fogStartDistance = 35f;
                RenderSettings.fogEndDistance = 95f;
                RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.55f);
                _cam.backgroundColor = new Color(0.72f, 0.82f, 0.9f);
            }
        }

        private void MakeWall(string name, Vector3 position, Vector3 scale, Color color)
        {
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.SetParent(_root, false);
            wall.transform.position = position;
            wall.transform.localScale = scale;
            wall.GetComponent<Renderer>().material.color = color;
        }

        private void MakePlayer()
        {
            _playerBody = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _playerBody.name = "Hero";
            _playerBody.transform.SetParent(_root, false);
            _playerBody.transform.position = new Vector3(0f, 0.75f, 0f);
            _playerBody.GetComponent<Renderer>().material.color = new Color(0.25f, 0.55f, 0.95f);

            // 鼻尖：Capsule 是对称的，不添这个看不出角色面朝哪（相机在身后跟，方向感全靠它）
            MakePart(_playerBody, "Nose", PrimitiveType.Cube,
                new Vector3(0f, 0.18f, 0.42f), new Vector3(0.16f, 0.16f, 0.3f), new Color(0.15f, 0.35f, 0.7f));
        }

        /// <summary>按怪物种类拼装模型：史莱姆是绿团子、野狼是灰狼身、石魔是堆
        /// 石头、魔王更高大还戴金冠红眼。全是基本体拼的，不依赖美术资产；
        /// 拼之前先清掉上一只的零件（池子复用同一个壳）。返回主零件供按血量染色。</summary>
        private void BuildMonsterBody(int instanceId, GameObject container, string defId)
        {
            for (int i = container.transform.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(container.transform.GetChild(i).gameObject);
            }

            Renderer mainRend;
            Color baseColor;
            switch (defId)
            {
                case "slime":
                    // 绿团子：压扁的球 + 两只黑豆眼
                    baseColor = new Color(0.35f, 0.8f, 0.4f);
                    mainRend = MakePart(container, "SlimeBody", PrimitiveType.Sphere,
                        new Vector3(0f, 0.42f, 0f), new Vector3(0.95f, 0.75f, 0.95f), baseColor).GetComponent<Renderer>();
                    MakePart(container, "SlimeEyeL", PrimitiveType.Sphere,
                        new Vector3(-0.18f, 0.55f, 0.36f), Vector3.one * 0.09f, new Color(0.1f, 0.15f, 0.1f));
                    MakePart(container, "SlimeEyeR", PrimitiveType.Sphere,
                        new Vector3(0.18f, 0.55f, 0.36f), Vector3.one * 0.09f, new Color(0.1f, 0.15f, 0.1f));
                    break;

                case "wolf":
                    // 野狼：拉长的躯干 + 头 + 双耳 + 尾 + 四条腿，灰棕色
                    baseColor = new Color(0.5f, 0.46f, 0.42f);
                    mainRend = MakePart(container, "WolfBody", PrimitiveType.Cube,
                        new Vector3(0f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 1.05f), baseColor).GetComponent<Renderer>();
                    MakePart(container, "WolfHead", PrimitiveType.Cube,
                        new Vector3(0f, 0.72f, 0.62f), Vector3.one * 0.42f, new Color(0.55f, 0.5f, 0.45f));
                    MakePart(container, "WolfEarL", PrimitiveType.Cube,
                        new Vector3(-0.13f, 0.98f, 0.58f), new Vector3(0.1f, 0.18f, 0.08f), new Color(0.4f, 0.36f, 0.33f));
                    MakePart(container, "WolfEarR", PrimitiveType.Cube,
                        new Vector3(0.13f, 0.98f, 0.58f), new Vector3(0.1f, 0.18f, 0.08f), new Color(0.4f, 0.36f, 0.33f));
                    MakePart(container, "WolfTail", PrimitiveType.Cube,
                        new Vector3(0f, 0.62f, -0.62f), new Vector3(0.08f, 0.08f, 0.45f), new Color(0.45f, 0.4f, 0.36f));
                    MakePart(container, "WolfLegFL", PrimitiveType.Cube,
                        new Vector3(-0.16f, 0.2f, 0.35f), new Vector3(0.12f, 0.4f, 0.12f), new Color(0.42f, 0.38f, 0.35f));
                    MakePart(container, "WolfLegFR", PrimitiveType.Cube,
                        new Vector3(0.16f, 0.2f, 0.35f), new Vector3(0.12f, 0.4f, 0.12f), new Color(0.42f, 0.38f, 0.35f));
                    MakePart(container, "WolfLegBL", PrimitiveType.Cube,
                        new Vector3(-0.16f, 0.2f, -0.35f), new Vector3(0.12f, 0.4f, 0.12f), new Color(0.42f, 0.38f, 0.35f));
                    MakePart(container, "WolfLegBR", PrimitiveType.Cube,
                        new Vector3(0.16f, 0.2f, -0.35f), new Vector3(0.12f, 0.4f, 0.12f), new Color(0.42f, 0.38f, 0.35f));
                    break;

                case "spider":
                    // 毒蛛：紫黑肚子 + 小头 + 八条细腿 + 红眼，腿多跑得快
                    baseColor = new Color(0.32f, 0.2f, 0.4f);
                    mainRend = MakePart(container, "SpiderBelly", PrimitiveType.Sphere,
                        new Vector3(0f, 0.45f, -0.1f), new Vector3(0.8f, 0.6f, 0.95f), baseColor).GetComponent<Renderer>();
                    MakePart(container, "SpiderHead", PrimitiveType.Sphere,
                        new Vector3(0f, 0.42f, 0.5f), Vector3.one * 0.45f, new Color(0.26f, 0.16f, 0.32f));
                    MakePart(container, "SpiderEyeL", PrimitiveType.Sphere,
                        new Vector3(-0.12f, 0.5f, 0.72f), Vector3.one * 0.09f, new Color(1f, 0.2f, 0.15f));
                    MakePart(container, "SpiderEyeR", PrimitiveType.Sphere,
                        new Vector3(0.12f, 0.5f, 0.72f), Vector3.one * 0.09f, new Color(1f, 0.2f, 0.15f));
                    for (int side = -1; side <= 1; side += 2)
                    {
                        for (int leg = 0; leg < 4; leg++)
                        {
                            float lx = side * 0.5f;
                            float lz = 0.3f - leg * 0.26f;
                            MakePart(container, "SpiderLeg" + side + "_" + leg, PrimitiveType.Cube,
                                new Vector3(lx, 0.25f, lz), new Vector3(0.55f, 0.07f, 0.07f),
                                new Color(0.22f, 0.14f, 0.28f));
                        }
                    }
                    break;

                case "boar":
                    // 野猪：棕壮身子 + 猪头 + 白獠牙，横冲直撞
                    baseColor = new Color(0.45f, 0.3f, 0.2f);
                    mainRend = MakePart(container, "BoarBody", PrimitiveType.Cube,
                        new Vector3(0f, 0.55f, 0f), new Vector3(0.7f, 0.6f, 1.1f), baseColor).GetComponent<Renderer>();
                    MakePart(container, "BoarHead", PrimitiveType.Cube,
                        new Vector3(0f, 0.5f, 0.7f), new Vector3(0.45f, 0.42f, 0.4f), new Color(0.42f, 0.28f, 0.19f));
                    MakePart(container, "BoarSnout", PrimitiveType.Cube,
                        new Vector3(0f, 0.42f, 0.95f), new Vector3(0.22f, 0.2f, 0.18f), new Color(0.6f, 0.45f, 0.4f));
                    MakePart(container, "BoarTuskL", PrimitiveType.Cube,
                        new Vector3(-0.14f, 0.38f, 0.92f), new Vector3(0.06f, 0.2f, 0.06f), new Color(0.95f, 0.93f, 0.85f));
                    MakePart(container, "BoarTuskR", PrimitiveType.Cube,
                        new Vector3(0.14f, 0.38f, 0.92f), new Vector3(0.06f, 0.2f, 0.06f), new Color(0.95f, 0.93f, 0.85f));
                    MakePart(container, "BoarEarL", PrimitiveType.Cube,
                        new Vector3(-0.16f, 0.75f, 0.6f), new Vector3(0.1f, 0.14f, 0.06f), new Color(0.38f, 0.25f, 0.17f));
                    MakePart(container, "BoarEarR", PrimitiveType.Cube,
                        new Vector3(0.16f, 0.75f, 0.6f), new Vector3(0.1f, 0.14f, 0.06f), new Color(0.38f, 0.25f, 0.17f));
                    for (int i = 0; i < 4; i++)
                    {
                        float lx = (i % 2) == 0 ? -0.22f : 0.22f;
                        float lz = i < 2 ? 0.35f : -0.35f;
                        MakePart(container, "BoarLeg" + i, PrimitiveType.Cube,
                            new Vector3(lx, 0.18f, lz), new Vector3(0.14f, 0.36f, 0.14f), new Color(0.38f, 0.25f, 0.17f));
                    }
                    break;

                case "treant-lord":
                    // 树妖之王（野外 Boss）：老树成精 —— 粗树干 + 三层绿冠 + 树枝手臂 + 红眼
                    baseColor = new Color(0.36f, 0.26f, 0.15f);
                    mainRend = MakePart(container, "TreantTrunk", PrimitiveType.Cylinder,
                        new Vector3(0f, 1.3f, 0f), new Vector3(1.1f, 1.3f, 1.1f), baseColor).GetComponent<Renderer>();
                    MakePart(container, "TreantCrownLow", PrimitiveType.Sphere,
                        new Vector3(0f, 2.6f, 0f), new Vector3(2.6f, 1.8f, 2.6f), new Color(0.2f, 0.45f, 0.18f));
                    MakePart(container, "TreantCrownTop", PrimitiveType.Sphere,
                        new Vector3(0f, 3.4f, 0f), new Vector3(1.7f, 1.4f, 1.7f), new Color(0.24f, 0.5f, 0.2f));
                    MakePart(container, "TreantArmL", PrimitiveType.Cube,
                        new Vector3(-1.05f, 1.6f, 0f), new Vector3(0.9f, 0.22f, 0.22f), new Color(0.33f, 0.24f, 0.14f));
                    MakePart(container, "TreantArmR", PrimitiveType.Cube,
                        new Vector3(1.05f, 1.6f, 0f), new Vector3(0.9f, 0.22f, 0.22f), new Color(0.33f, 0.24f, 0.14f));
                    MakePart(container, "TreantEyeL", PrimitiveType.Sphere,
                        new Vector3(-0.2f, 1.7f, 0.5f), Vector3.one * 0.12f, new Color(1f, 0.2f, 0.1f));
                    MakePart(container, "TreantEyeR", PrimitiveType.Sphere,
                        new Vector3(0.2f, 1.7f, 0.5f), Vector3.one * 0.12f, new Color(1f, 0.2f, 0.1f));
                    MakePart(container, "TreantRootL", PrimitiveType.Cube,
                        new Vector3(-0.4f, 0.2f, 0.1f), new Vector3(0.3f, 0.4f, 0.3f), new Color(0.3f, 0.22f, 0.13f));
                    MakePart(container, "TreantRootR", PrimitiveType.Cube,
                        new Vector3(0.4f, 0.2f, 0.1f), new Vector3(0.3f, 0.4f, 0.3f), new Color(0.3f, 0.22f, 0.13f));
                    break;

                case "golem":
                    // 石魔：堆起来的大石块 + 红眼，土石色
                    baseColor = new Color(0.55f, 0.52f, 0.48f);
                    mainRend = MakePart(container, "GolemTorso", PrimitiveType.Cube,
                        new Vector3(0f, 0.8f, 0f), new Vector3(1.0f, 0.9f, 0.7f), baseColor).GetComponent<Renderer>();
                    MakePart(container, "GolemHead", PrimitiveType.Cube,
                        new Vector3(0f, 1.45f, 0f), Vector3.one * 0.45f, new Color(0.5f, 0.47f, 0.44f));
                    MakePart(container, "GolemEyeL", PrimitiveType.Cube,
                        new Vector3(-0.1f, 1.45f, 0.24f), Vector3.one * 0.07f, new Color(0.9f, 0.2f, 0.15f));
                    MakePart(container, "GolemEyeR", PrimitiveType.Cube,
                        new Vector3(0.1f, 1.45f, 0.24f), Vector3.one * 0.07f, new Color(0.9f, 0.2f, 0.15f));
                    MakePart(container, "GolemArmL", PrimitiveType.Cube,
                        new Vector3(-0.68f, 0.75f, 0f), new Vector3(0.28f, 0.85f, 0.28f), new Color(0.48f, 0.45f, 0.42f));
                    MakePart(container, "GolemArmR", PrimitiveType.Cube,
                        new Vector3(0.68f, 0.75f, 0f), new Vector3(0.28f, 0.85f, 0.28f), new Color(0.48f, 0.45f, 0.42f));
                    MakePart(container, "GolemLegL", PrimitiveType.Cube,
                        new Vector3(-0.26f, 0.25f, 0f), new Vector3(0.3f, 0.5f, 0.3f), new Color(0.45f, 0.42f, 0.39f));
                    MakePart(container, "GolemLegR", PrimitiveType.Cube,
                        new Vector3(0.26f, 0.25f, 0f), new Vector3(0.3f, 0.5f, 0.3f), new Color(0.45f, 0.42f, 0.39f));
                    break;

                case "golem-king":
                    // 岩石魔王：比石魔高大一圈的黑岩身 + 红眼 + 金冠，一看就是 boss
                    baseColor = new Color(0.4f, 0.32f, 0.32f);
                    mainRend = MakePart(container, "KingTorso", PrimitiveType.Cube,
                        new Vector3(0f, 1.05f, 0f), new Vector3(1.4f, 1.25f, 0.9f), baseColor).GetComponent<Renderer>();
                    MakePart(container, "KingHead", PrimitiveType.Cube,
                        new Vector3(0f, 1.95f, 0f), Vector3.one * 0.55f, new Color(0.35f, 0.28f, 0.28f));
                    MakePart(container, "KingEyeL", PrimitiveType.Cube,
                        new Vector3(-0.12f, 2.0f, 0.3f), Vector3.one * 0.09f, new Color(1f, 0.15f, 0.1f));
                    MakePart(container, "KingEyeR", PrimitiveType.Cube,
                        new Vector3(0.12f, 2.0f, 0.3f), Vector3.one * 0.09f, new Color(1f, 0.15f, 0.1f));
                    MakePart(container, "KingCrown", PrimitiveType.Cube,
                        new Vector3(0f, 2.42f, 0f), Vector3.one * 0.14f, new Color(0.92f, 0.78f, 0.25f));
                    MakePart(container, "KingCrownL", PrimitiveType.Cube,
                        new Vector3(-0.2f, 2.36f, 0f), Vector3.one * 0.1f, new Color(0.92f, 0.78f, 0.25f));
                    MakePart(container, "KingCrownR", PrimitiveType.Cube,
                        new Vector3(0.2f, 2.36f, 0f), Vector3.one * 0.1f, new Color(0.92f, 0.78f, 0.25f));
                    MakePart(container, "KingArmL", PrimitiveType.Cube,
                        new Vector3(-0.95f, 1.05f, 0f), new Vector3(0.38f, 1.15f, 0.38f), new Color(0.36f, 0.3f, 0.3f));
                    MakePart(container, "KingArmR", PrimitiveType.Cube,
                        new Vector3(0.95f, 1.05f, 0f), new Vector3(0.38f, 1.15f, 0.38f), new Color(0.36f, 0.3f, 0.3f));
                    MakePart(container, "KingLegL", PrimitiveType.Cube,
                        new Vector3(-0.38f, 0.3f, 0f), new Vector3(0.42f, 0.6f, 0.42f), new Color(0.33f, 0.27f, 0.27f));
                    MakePart(container, "KingLegR", PrimitiveType.Cube,
                        new Vector3(0.38f, 0.3f, 0f), new Vector3(0.42f, 0.6f, 0.42f), new Color(0.33f, 0.27f, 0.27f));
                    break;

                default:
                    // 没登记的种类：退回老样子的红方块，别让怪凭空消失
                    baseColor = new Color(0.85f, 0.4f, 0.3f);
                    mainRend = MakePart(container, "MonsterBody", PrimitiveType.Cube,
                        new Vector3(0f, 0.5f, 0f), Vector3.one, baseColor).GetComponent<Renderer>();
                    break;
            }

            _mainRends[instanceId] = mainRend;
            _mainColors[instanceId] = baseColor;
        }

        /// <summary>拼一个零件：基本体 + 局部位置 + 缩放 + 颜色，挂到怪壳下。</summary>
        private GameObject MakePart(Transform parent, string name, PrimitiveType type,
            Vector3 localPosition, Vector3 scale, Color color)
        {
            return MakePart(parent.gameObject, name, type, localPosition, scale, color);
        }

        /// <summary>拼一个零件：基本体 + 局部位置 + 缩放 + 颜色，挂到怪壳下。</summary>
        private GameObject MakePart(GameObject container, string name, PrimitiveType type,
            Vector3 localPosition, Vector3 scale, Color color)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.transform.SetParent(container.transform, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = scale;
            part.GetComponent<Renderer>().material.color = color;
            return part;
        }

        // ------------------------------------------------------------------ 回城传送石

        /// <summary>回城传送石：石台 + 悬浮蓝水晶 + 漂浮牌子（炉石的等价物）。</summary>
        private void MakeReturnStone(float x, float z)
        {
            MakePart(_root, "ReturnStoneBase", PrimitiveType.Cube,
                new Vector3(x, 0.35f, z), new Vector3(1.2f, 0.7f, 1.2f), new Color(0.35f, 0.36f, 0.4f));
            MakePart(_root, "ReturnCrystal", PrimitiveType.Cube,
                new Vector3(x, 1.35f, z), new Vector3(0.5f, 0.95f, 0.5f), new Color(0.3f, 0.75f, 0.95f));
            MakeSign("回城传送石", x + 1.8f, 2.4f, z, new Color(0.75f, 0.93f, 1f));
        }

        /// <summary>漂浮的 3D 提示牌（动态字体现场造材质，不碰美术资产）。</summary>
        private static void MakeSign(string text, float x, float y, float z, Color color)
        {
            GameObject go = new GameObject("Sign_" + text);
            go.transform.position = new Vector3(x, y, z);
            TextMesh tm = go.AddComponent<TextMesh>();
            tm.font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 24);
            tm.fontSize = 24;
            tm.characterSize = 0.2f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            tm.text = text;
            go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
        }

        /// <summary>每帧找半径内的交互点（玩家位置 = 竞技场的账，怪追到哪玩家都算数）。</summary>
        private void UpdateInteraction()
        {
            _currentId = null;
            _currentLabel = null;
            float best = float.MaxValue;
            for (int i = 0; i < _points.Count; i++)
            {
                float dx = _arena.PlayerX - _points[i].X;
                float dz = _arena.PlayerZ - _points[i].Z;
                float d2 = dx * dx + dz * dz;
                if (d2 <= _points[i].Radius * _points[i].Radius && d2 < best)
                {
                    best = d2;
                    _currentId = _points[i].Id;
                    _currentLabel = _points[i].Label;
                }
            }
        }
    }
}
