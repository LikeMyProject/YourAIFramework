using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using YourFramework.Core;
using YourFramework.Event;
using YourFramework.Flow;
using YourFramework.Save;
using YourFramework.Scenes;
using YourFramework.Skills;
using YourFramework.Stats;
using YourFramework.UnityRuntime;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// 战斗场景宿主（野外 RpgWilds / 副本 RpgDungeon，多场景教学）：
    /// 场景名决定刷怪账（RpgZones 预设），回城 = SceneFlow.Request 切场景。
    /// 不用 AI 层 —— 框架的"游戏底盘"单独拿出来就能拼出完整 RPG。
    ///
    /// 刻意不用 GameEntry：自己 new ModuleCenter、自己泵（与贪吃蛇同理）。
    ///
    /// 依赖图（InitOrder 小的先初始化）：
    ///   RpgProfile 25（角色 + SaveSystem，事件一来就存档）
    ///   → RpgFlow 30（Menu → Playing → Dead）
    ///   → RpgRules 35（玩家倒下 → 切 Dead）
    /// 竞技场（纯 C#）由宿主每帧喂 Time.deltaTime 推进，无头环境可逐帧验证。
    /// </summary>
    public sealed class RpgFieldHost : MonoBehaviour
    {
        private EventBus _bus;
        private RpgPlayer _player;
        private RpgArena _arena;
        private ModuleCenter _modules;
        private ProcedureFlow _flow;
        private RpgSkillModule _skills;
        private SceneFlow _scenes;
        private RpgUi _ui;
        private RpgView _view;
        private RpgControl _input;
        private float _hudTimer;

        private void Awake()
        {
            _bus = new EventBus();
            DeterministicRandom random = new DeterministicRandom(20260920UL, "rpg/arena");
            _player = new RpgPlayer();
            // 场景名决定刷怪账：RpgWilds 野外（营地 Boss）、RpgDungeon 副本（守关 Boss）。
            // 单场景教学（没拆场景）时找不到预设，按野外玩。
            RpgZonePreset zone;
            RpgZones.TryFind(SceneManager.GetActiveScene().name, out zone);
            bool bossOnly = zone.BossOnly;

            _arena = new RpgArena(_bus, random, _player, bossOnly, zone.BossId);

            SaveSystem saves = new SaveSystem(Path.Combine(Application.persistentDataPath, "RpgSaves"));

            _flow = new ProcedureFlow("Rpg", 30);
            _flow.Add(new RpgPlayingProcedure())
                .Add(new RpgDeadProcedure())
                .Start<RpgPlayingProcedure>();

            _modules = new ModuleCenter();
            _modules.Register(new RpgProfileModule(_bus, saves, _player, Debug.Log));
            _skills = new RpgSkillModule(_bus, _player, _arena, Debug.Log);
            _modules.Register(_skills);
            // 1.2f = 最短展示时长：空场景毫秒级加载完，进度条停满格 1.2 秒再切过去
            _scenes = new SceneFlow(_bus, new UnitySceneBackend(2f), Debug.Log);
            _modules.Register(_scenes);
            _modules.Register(_flow);
            _modules.Register(new RpgRulesModule(_bus, _flow));
            _modules.Initialize();

            BuildCompanions();
            BuildSkillBar();
            WireEvents();
            SetupCamera();

            // 野外才有回城传送石（炉石的等价物）；副本进出只认死亡与通关 —— 出口是结算界面
            if (!bossOnly)
            {
                _view.AddInteractPoint(2.2f, 2.2f, 2.6f, "home", "回城传送石（返回城镇复活点）");
            }

            Debug.Log("[RPG] 就绪：WASD 移动，空格攻击，I 背包。角色档案走 SaveSystem，跨局保留。");
        }

        private void Update()
        {
            _modules.Pump();            // 流程机在这里推进（切换延迟到泵首是它的契约）

            if (!IsPlaying())
            {
                _ui.SetPrompt(null);    // 阵亡/结算界面开着时收掉交互提示
                return;
            }

            // 交互：走近传送石出提示，按 F 回城（结算归宿主，视图只管"站在哪"）
            _ui.SetPrompt(_view.CurrentInteractionLabel);
            if (_input.ConsumeInteract() && _view.CurrentInteractionId == "home")
            {
                GoHome();
                return;
            }

            _arena.SetFacing(_input.FacingX, _input.FacingZ);   // 瞄准方向 = 视角朝向
            _arena.Tick(Time.deltaTime, _input.DirX, _input.DirZ, _input.AttackHeld);
            _skills.Advance(Time.deltaTime);

            if (_input.SkillRequest >= 0 && _input.SkillRequest < _skills.Count)
            {
                _skills.TryCast(_skills.IdAt(_input.SkillRequest));
            }

            _ui.SetHpBar(_player.Hp, _player.MaxHp);
            _ui.SetExpBar(_player.Exp, _player.ExpToNext);
            _ui.SetMpBar(_player.Mp, _player.MaxMp);

            // 文字面板随事件走会漏（掉血/回蓝/加经验不发 StatsChanged），
            // 这里节流到 4 次/秒重刷，肉眼看着是实时的，又不至于每帧拼字符串
            _hudTimer += Time.deltaTime;
            if (_hudTimer >= 0.25f)
            {
                _hudTimer = 0f;
                _ui.SetHud(_player);
            }
            for (int i = 0; i < _skills.Count; i++)
            {
                SkillSlot slot = _skills.Slot(_skills.IdAt(i));
                if (slot != null)
                {
                    _ui.SetSkillCooldown(i, slot.Cooldown.IsFull ? 0f : 1f - slot.Cooldown.Progress,
                        slot.Cooldown.IsFull ? 0f : slot.Cooldown.Remaining);
                }
            }
        }

        private void OnDestroy()
        {
            if (_modules != null)
            {
                _modules.Shutdown();
            }
        }

        // ------------------------------------------------------------------ 接线

        // 技能栏按技能表摆格 —— 表里加技能，界面自动多一格（配置驱动落到 UI）。
        // 注意时机：必须在 BuildCompanions 之后（_ui 那时才存在）。
        private void BuildSkillBar()
        {
            for (int i = 0; i < _skills.Count; i++)
            {
                SkillDef def = _skills.Def(_skills.IdAt(i));
                float mpCost = 0f;
                for (int c = 0; c < def.Costs.Count; c++)
                {
                    if (def.Costs[c].Resource == "mp")
                    {
                        mpCost = def.Costs[c].Amount;
                    }
                }

                _ui.AddSkillSlot(i, def.Name + (mpCost > 0f ? " " + mpCost.ToString("0") + "蓝" : ""),
                    (i + 1).ToString());
            }
            _ui.LayoutSkillSlots();     // 格子总数齐了再对称摆开（逐格摆会挤成一团）
        }

        private void BuildCompanions()
        {
            _ui = gameObject.AddComponent<RpgUi>();
            _ui.Build();

            // 输入先建好再交给视图 —— 顺序反了，视图存下的就是空引用
            //（踩过：LateUpdate 里 _control.Yaw 首帧炸，之前被 MakeTerrain 的炸挡住没露头）
            _input = gameObject.AddComponent<RpgControl>();
            _input.Init(IsPlaying, CanStart, CanRestart, StartGame, ToggleInventory);

            // 火球是直飞弹：撞到怪由视图回调，伤害在技能模块里按表结算
            RpgView view = gameObject.AddComponent<RpgView>();
            _view = view;
            view.Init(_arena, _bus, delegate(string targetId)
            {
                SkillEffectResult hit = _skills.ApplyFireballHit(targetId);
                if (hit.Succeeded)
                {
                    _ui.AddLog("火球命中，造成 " + hit.Amount + " 伤害");
                }
            }, _input);
        }

        private void WireEvents()
        {
            _ui.OnStartClicked = StartGame;
            _ui.OnRestartClicked = StartGame;
            _ui.OnSlotClicked = HandleSlotClick;

            _bus.Subscribe<RpgRunStartedEvent>(delegate
            {
                _ui.ClearLog();
                _ui.AddLog("新的冒险开始，注意场边！");
                RefreshAll();
            });
            _bus.Subscribe<RpgMonsterDiedEvent>(delegate(RpgMonsterDiedEvent evt)
            {
                string drop = evt.DropName == null ? "" : "，掉了 " + evt.DropName;
                _ui.AddLog("击败 " + evt.Name + " +" + evt.Exp + " 经验 +" + evt.Gold + " 金币" + drop);
                if (evt.DefId == "golem-king")
                {
                    _ui.ShowVictory(_arena.KillCountThisRun);
                    _ui.AddLog("岩石魔王倒下了！按 I 收好战利品，回城休整吧。");
                }
            });
            _bus.Subscribe<RpgItemGainedEvent>(delegate(RpgItemGainedEvent evt)
            {
                _ui.AddLog(evt.Rejected ? "背包已满，" + evt.Name + " 没捡走" : "捡到 " + evt.Name);
            });
            _bus.Subscribe<RpgPlayerLeveledUpEvent>(delegate(RpgPlayerLeveledUpEvent evt)
            {
                _ui.AddLog("升级！Lv." + evt.Level + "，血量回满，攻击成长");
            });
            _bus.Subscribe<RpgPlayerHurtEvent>(delegate(RpgPlayerHurtEvent evt)
            {
                _ui.SetHpBar(evt.Hp, evt.MaxHp);
            });
            _bus.Subscribe<RpgPlayerDiedEvent>(delegate
            {
                _ui.AddLog("你倒下了……");
                _ui.ShowDead(_arena.KillCountThisRun);
            });
            _bus.Subscribe<RpgItemEquippedEvent>(delegate(RpgItemEquippedEvent evt)
            {
                string replaced = evt.ReplacedName == null ? "" : "（换下 " + evt.ReplacedName + "）";
                _ui.AddLog("装备了 " + evt.Name + replaced);
            });
            _bus.Subscribe<RpgPotionUsedEvent>(delegate(RpgPotionUsedEvent evt)
            {
                _ui.AddLog(evt.Failed ? "没有药水可喝" : "喝下 " + evt.Name + "，回血 " + evt.Healed);
            });
            _bus.Subscribe<RpgStatsChangedEvent>(delegate { RefreshAll(); });
            _bus.Subscribe<RpgSkillCastEvent>(delegate(RpgSkillCastEvent evt)
            {
                _ui.AddLog("【技能】" + evt.SkillName + "：" + evt.Detail);
            });
            _ui.OnSkillClicked = delegate(int index)
            {
                if (IsPlaying() && index < _skills.Count)
                {
                    _skills.TryCast(_skills.IdAt(index));
                }
            };
            _ui.OnHomeClicked = GoHome;
        }

        /// <summary>回城：一次场景切换就完事，档案在城镇场景自己读回来。落复活点（泉水旁）。</summary>
        private void GoHome()
        {
            RpgSpawnBook.BindTownWell();    // 野外传送石、副本阵亡/通关回城都落复活点
            SceneFlow.SceneRequestOutcome o = _scenes.Request(RpgScenes.Town);
            if (!o.Accepted)
            {
                _ui.AddLog("回不了城：" + o.Error);
            }
        }

        /// <summary>背包格点击：药水直接喝，装备直接穿。操作者在这里，事件也从这里发出。</summary>
        private void HandleSlotClick(int index)
        {
            if (!IsPlaying() || index < 0 || index >= _player.Slots.Count)
            {
                return;
            }

            RpgItemDef def = RpgItems.Find(_player.Slots[index].DefId);
            if (def.Kind == RpgItemKind.Potion)
            {
                int healed = _player.UsePotion(def.Id);
                _bus.Publish(new RpgPotionUsedEvent { Name = def.Name, Healed = healed, Failed = healed < 0 });
            }
            else
            {
                string replacedId;
                if (_player.TryEquip(def.Id, out replacedId))
                {
                    _bus.Publish(new RpgItemEquippedEvent
                    {
                        DefId = def.Id,
                        Name = def.Name,
                        ReplacedName = replacedId == null ? null : RpgItems.Find(replacedId).Name,
                    });
                }
            }

            RefreshAll();
        }

        private void RefreshAll()
        {
            _ui.SetHud(_player);
            _ui.SetHpBar(_player.Hp, _player.MaxHp);
            _ui.SetExpBar(_player.Exp, _player.ExpToNext);
            if (_ui.InventoryVisible)
            {
                _ui.RefreshInventory(_player);
            }
        }

        private void ToggleInventory()
        {
            _ui.ToggleInventory(!_ui.InventoryVisible);
            if (_ui.InventoryVisible)
            {
                _ui.RefreshInventory(_player);
            }
        }

        // ------------------------------------------------------------------ 流程

        /// <summary>开一局（开始 / 再战都汇到这里）。角色成长跨局保留，血量回满。</summary>
        private void StartGame()
        {
            _arena.ResetRun();
            _flow.ChangeProcedure<RpgPlayingProcedure>();
            _ui.ShowPlaying();
            RefreshAll();
        }

        private bool IsPlaying()
        {
            return _flow.State == ProcedureFlowState.Running && _flow.CurrentProcedureName == "Playing";
        }

        private bool CanRestart()
        {
            return _flow.State == ProcedureFlowState.Running && _flow.CurrentProcedureName == "Dead";
        }

        private bool CanStart()
        {
            return _flow.State == ProcedureFlowState.Running && _flow.CurrentProcedureName == "Menu";
        }

        private void SetupCamera()
        {
            // 相机的位置/朝向每帧由 RpgView 按跟随算，这里只保证透视初值
            Camera mainCamera = Camera.main;
            mainCamera.orthographic = false;
            mainCamera.fieldOfView = 60f;
        }
    }
}
