using UnityEngine;
using YourAI.Agent;
using YourAI.UnityRuntime;
using YourFramework.Event;
using YourFramework.Save;

namespace YourAIFramework.SnakeGame
{
    /// <summary>键盘输入：方向键 / WASD 喂方向，空格重开，回车开局。</summary>
    public sealed class SnakeInput : MonoBehaviour
    {
        private SnakeBoard _board;
        private System.Func<bool> _isPlaying;
        private System.Func<bool> _canRestart;
        private System.Func<bool> _canStart;
        private System.Action _restart;

        public void Init(SnakeBoard board, System.Func<bool> isPlaying,
            System.Func<bool> canRestart, System.Func<bool> canStart, System.Action restart)
        {
            _board = board;
            _isPlaying = isPlaying;
            _canRestart = canRestart;
            _canStart = canStart;
            _restart = restart;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Space) && _canRestart())
            {
                _restart();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Return) && _canStart())
            {
                _restart();
                return;
            }

            if (_isPlaying == null || !_isPlaying())
            {
                return;
            }

            int dx = 0;
            int dy = 0;
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) { dy = 1; }
            else if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) { dy = -1; }
            else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) { dx = -1; }
            else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) { dx = 1; }

            if (dx != 0 || dy != 0)
            {
                _board.SetDirection(dx, dy);
            }
        }
    }

    /// <summary>
    /// AI 操控（低延迟版）：一次问模型要**三步**方向，存进小队列，蛇每走一步消耗一个。
    /// 网络来回约 1 秒，而蛇一步 0.55 秒 —— 一次问三步，就能让大部分时刻队列里
    /// 有现成方向，蛇不再干等网络。
    ///
    /// 队列见底、回复还没到的那几拍，用本地启发式垫场（面板上如实写"AI 途中"），
    /// 不让蛇停下来，也不让玩家误以为是 AI 在操控。
    ///
    /// "失败也是值"在这里的实际形态：
    /// - 没配 Key：AI 缺席，本地启发式照样玩，不报错不崩；
    /// - 回复超时 / 看不懂：清空旧计划，那一拍落到启发式，下一拍继续问；
    /// - 上下文控制：每 10 次请求重建一次 agent，聊天记录不会无限涨。
    /// </summary>
    public sealed class SnakeAiController : MonoBehaviour
    {
        private const float ReplyTimeoutSeconds = 2.5f;
        private const int PlanSteps = 3;            // 一次问几步
        private const int AgentRefreshEvery = 10;   // 十次请求重建 agent（每次请求 = 三步）

        private SnakeBoard _board;
        private EventBus _bus;
        private SettingStore _settings;
        private System.Func<bool> _isPlaying;
        private System.Action<string> _setStatus;

        private AiAgent _agent;
        private IAgentTurn _pending;
        private float _pendingAge;
        private int _requestsSinceCreate;
        private bool _aiMode;
        private bool _keyMissingLogged;
        private SnakeMovedEvent _last;
        private bool _hasLast;

        // 预取的方向队列：最多 PlanSteps 个，存 dx/dy 两个小数组 + 游标。
        private readonly int[] _queueX = new int[PlanSteps];
        private readonly int[] _queueY = new int[PlanSteps];
        private int _queueCount;
        private int _queueHead;
        private int _lastElapsedMs = -1;

        // 提示词不写死：下面只是**默认值**（兜底用）。上线后想调提示词，
        // 在 game-settings.json 里加 "snake.ai.systemPrompt": "..." 即可覆盖，
        // 不用重新编译 —— 提示词是运营期常改的东西，别焊在代码里。
        private static readonly string DefaultSystemPrompt =
            "你在玩贪吃蛇。坐标 x 向右增大，y 向上增大：up 使 y+1，down 使 y-1，left 使 x-1，right 使 x+1。"
            + "目标：选一个标记为安全的方向，并让头靠近食物。掉头不允许。蛇每走一步身体也前移一格。"
            + "每次回答" + PlanSteps + "步的连续方向：只写 " + PlanSteps
            + " 个词，用空格分开，例如 up left up。不要任何其他文字。";

        private string GetSystemPrompt()
        {
            return _settings.GetString("snake.ai.systemPrompt", DefaultSystemPrompt);
        }

        public bool AiMode { get { return _aiMode; } }

        public void Init(SnakeBoard board, EventBus bus, SettingStore settings,
            System.Func<bool> isPlaying, System.Action<string> setStatus)
        {
            _board = board;
            _bus = bus;
            _settings = settings;
            _isPlaying = isPlaying;
            _setStatus = setStatus;
            _bus.Subscribe<SnakeMovedEvent>(OnMoved);
        }

        private void OnDestroy()
        {
            if (_bus != null)
            {
                _bus.Unsubscribe<SnakeMovedEvent>(OnMoved);
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.A))
            {
                ToggleAi();
            }

            if (_pending == null)
            {
                return;
            }

            _pending.Pump();
            _pendingAge += Time.deltaTime;
            if (_pending.IsDone)
            {
                AgentTurnResult result = _pending.Result;
                _pending = null;
                ApplyReply(result);
            }
            else if (_pendingAge > ReplyTimeoutSeconds)
            {
                _pending.Cancel();
                _pending = null;
                _queueCount = 0;        // 计划的前提是旧局面，超时就别再用
                _setStatus("AI 超时 → 启发式垫场");
                Heuristic(_last, "AI 超时");
            }
        }

        /// <summary>AI 模式开关（A 键或面板按钮）。</summary>
        public void ToggleAi()
        {
            _aiMode = !_aiMode;
            _queueCount = 0;
            if (_aiMode)
            {
                EnsureAgent();
                _setStatus(_agent != null ? "AI 操控：开，思考中…" : "AI 操控：开（无 Key）→ 本地启发式");
            }
            else
            {
                _setStatus("AI 操控：关");
            }

            Debug.Log("[Snake] AI 操控 " + (_aiMode ? "开" : "关"));
        }

        // ------------------------------------------------------------------ core

        private void OnMoved(SnakeMovedEvent evt)
        {
            _last = evt;
            _hasLast = true;
            if (!_aiMode || !_isPlaying())
            {
                return;
            }

            // 1) 队列里有现成方向：直接用，不等网络（这就是延迟优化主体）。
            if (_queueCount > 0)
            {
                int dx = _queueX[_queueHead];
                int dy = _queueY[_queueHead];
                _queueHead++;
                _queueCount--;

                if (dx == -evt.DirX && dy == -evt.DirY)
                {
                    // 模型给的这步要掉头（多半是计划过期）：这拍换启发式。
                    Heuristic(evt, "计划掉头");
                    return;
                }

                _board.SetDirection(dx, dy);
                _setStatus("AI 计划 → " + DirName(dx, dy) + "（还剩 " + _queueCount + " 步）");
                return;
            }

            // 2) 队列空了：没在等回复就发起新请求。
            if (_pending == null)
            {
                if (_agent == null)
                {
                    Heuristic(evt, "启发式");
                    return;
                }

                _requestsSinceCreate++;
                if (_requestsSinceCreate >= AgentRefreshEvery)
                {
                    _agent = null;      // 聊天记录别无限涨：重建一个干净的 agent
                    _requestsSinceCreate = 0;
                    EnsureAgent();
                    if (_agent == null)
                    {
                        Heuristic(evt, "启发式");
                        return;
                    }
                }

                _pending = _agent.BeginTurn("snake", Describe(evt), null);
                _pendingAge = 0f;
                _setStatus("AI 思考中…（首次会有一步延迟）");
                return;
            }

            // 3) 队列空 + 回复在路上：启发式垫场，蛇不停。
            Heuristic(evt, "AI 途中");
        }

        private void EnsureAgent()
        {
            if (_agent != null)
            {
                return;
            }

            string apiKey = _settings.GetString("ai.apiKey", "");
            if (string.IsNullOrEmpty(apiKey))
            {
                if (!_keyMissingLogged)
                {
                    _keyMissingLogged = true;
                    Debug.Log("[Snake] 没配 ai.apiKey —— AI 缺席，用本地启发式照样玩（失败也是值）。"
                        + "想看真 AI：在 persistentDataPath 下的 game-settings.json 里加 \"ai.apiKey\": \"sk-...\"");
                }

                return;
            }

            try
            {
                _agent = AiRuntimeFactory.CreateDeepSeekAgent("snake-ai", apiKey, GetSystemPrompt());
                Debug.Log("[Snake] AI 上场（deepseek-chat，一次答 " + PlanSteps + " 步）");
            }
            catch (System.Exception ex)
            {
                Debug.Log("[Snake] AI 创建失败（失败也是值）: " + ex.Message);
            }
        }

        private string Describe(SnakeMovedEvent e)
        {
            int towardX = e.Food.X - e.Head.X;
            int towardY = e.Food.Y - e.Head.Y;
            string up = _board.IsFree(e.Head.X, e.Head.Y + 1) ? "安全" : "撞";
            string down = _board.IsFree(e.Head.X, e.Head.Y - 1) ? "安全" : "撞";
            string left = _board.IsFree(e.Head.X - 1, e.Head.Y) ? "安全" : "撞";
            string right = _board.IsFree(e.Head.X + 1, e.Head.Y) ? "安全" : "撞";
            return "现在 头(" + e.Head.X + "," + e.Head.Y + ") 食物(" + e.Food.X + "," + e.Food.Y
                + ")，靠近食物要让 x" + Delta(towardX) + "、y" + Delta(towardY)
                + "。当前四个方向：up:" + up + " down:" + down + " left:" + left + " right:" + right
                + "。请给出接下来连续 " + PlanSteps
                + " 步（每步之间身体会前移，尽量都安全且朝食物靠近），只答 " + PlanSteps + " 个词。";
        }

        private static string Delta(int value)
        {
            return value >= 0 ? "+" + value : value.ToString();
        }

        private void ApplyReply(AgentTurnResult result)
        {
            if (result == null || !result.Success)
            {
                _setStatus("AI 失败 → 启发式（" + (result == null ? "空回复" : result.Error) + "）");
                Heuristic(_last, "AI 失败");
                return;
            }

            _lastElapsedMs = (int)result.ElapsedMs;
            string text = (result.Text ?? string.Empty).Trim();
            string[] words = text.ToLowerInvariant().Split(
                new[] { ' ', ',', '，', '、', ';', '；', '\n', '\r', '\t' },
                System.StringSplitOptions.RemoveEmptyEntries);

            _queueCount = 0;
            _queueHead = 0;
            foreach (string word in words)
            {
                if (_queueCount >= PlanSteps)
                {
                    break;
                }

                int dx;
                int dy;
                if (!TryWord(word, out dx, out dy))
                {
                    continue;
                }

                _queueX[_queueCount] = dx;
                _queueY[_queueCount] = dy;
                _queueCount++;
            }

            if (_queueCount == 0)
            {
                _setStatus("AI 答看不懂（" + Truncate(result.Text) + "）→ 启发式");
                Heuristic(_last, "看不懂");
                return;
            }

            // 第一步立刻用上（此刻正好在两拍之间），剩下的留给后续拍消化。
            int firstX = _queueX[_queueHead];
            int firstY = _queueY[_queueHead];
            if (firstX == -_last.DirX && firstY == -_last.DirY)
            {
                _queueCount = 0;
                Heuristic(_last, "计划掉头");
                return;
            }

            _queueHead++;
            _queueCount--;
            _board.SetDirection(firstX, firstY);
            Debug.Log("[Snake] AI 计划 '" + (result.Text ?? "").Trim() + "' 耗时"
                + result.ElapsedMs.ToString("0") + "ms @头("
                + _last.Head.X + "," + _last.Head.Y + ") 食物(" + _last.Food.X + "," + _last.Food.Y + ")");
            _setStatus("AI 计划 " + _queueCount + "+1 步 → " + DirName(firstX, firstY)
                + "（" + result.ElapsedMs.ToString("0") + "ms）");
        }

        private static bool TryWord(string word, out int dx, out int dy)
        {
            dx = 0;
            dy = 0;
            if (word.Contains("up")) { dy = 1; return true; }
            if (word.Contains("down")) { dy = -1; return true; }
            if (word.Contains("left")) { dx = -1; return true; }
            if (word.Contains("right")) { dx = 1; return true; }
            return false;
        }

        /// <summary>本地启发式：优先朝食物走，避开身体与墙。AI 缺席或垫场时的替代品。</summary>
        private void Heuristic(SnakeMovedEvent e, string reason)
        {
            if (!_hasLast)
            {
                return;
            }

            int towardX = e.Food.X - e.Head.X;
            int towardY = e.Food.Y - e.Head.Y;
            int sx = towardX > 0 ? 1 : towardX < 0 ? -1 : 0;
            int sy = towardY > 0 ? 1 : towardY < 0 ? -1 : 0;

            bool xFirst = System.Math.Abs(towardX) >= System.Math.Abs(towardY);
            int cx = 0;
            int cy = 0;

            if (xFirst && TryCandidate(sx, 0, e, ref cx, ref cy)) { }
            else if (!xFirst && TryCandidate(0, sy, e, ref cx, ref cy)) { }
            else if (TryCandidate(0, sy, e, ref cx, ref cy)) { }
            else if (TryCandidate(sx, 0, e, ref cx, ref cy)) { }
            else if (TryCandidate(e.DirX, e.DirY, e, ref cx, ref cy)) { }
            else
            {
                return;                 // 四面都堵：不转向，听天由命
            }

            _board.SetDirection(cx, cy);
            string tail = _lastElapsedMs >= 0 ? "，上次 " + _lastElapsedMs.ToString("0") + "ms" : "";
            _setStatus(reason + " → " + DirName(cx, cy) + tail);
        }

        private bool TryCandidate(int dx, int dy, SnakeMovedEvent e, ref int outX, ref int outY)
        {
            if ((dx == 0 && dy == 0)
                || (dx == -e.DirX && dy == -e.DirY)
                || !_board.IsFree(e.Head.X + dx, e.Head.Y + dy))
            {
                return false;
            }

            outX = dx;
            outY = dy;
            return true;
        }

        private static string DirName(int dx, int dy)
        {
            return dy == 1 ? "up" : dy == -1 ? "down" : dx == 1 ? "right" : "left";
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(空)";
            }

            string trimmed = text.Trim();
            return trimmed.Length <= 24 ? trimmed : trimmed.Substring(0, 24) + "…";
        }
    }
}
