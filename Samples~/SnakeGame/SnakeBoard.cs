using YourFramework.Event;
using YourFramework.Stats;

namespace YourAIFramework.SnakeGame
{
    /// <summary>棋盘格子（纯数据）。X/Y 是格坐标，[0, Grid)。</summary>
    public struct SnakeCell
    {
        public int X;
        public int Y;
    }

    /// <summary>每拍移动事件：AI 控制器、视图、HUD 都靠它对齐。高频事件，走 Publish（零装箱）。</summary>
    public struct SnakeMovedEvent
    {
        public SnakeCell Head;
        public int DirX;
        public int DirY;
        public SnakeCell Food;
        public int Score;
    }

    /// <summary>吃到食物。</summary>
    public struct SnakeFoodEatenEvent
    {
        public int Score;
    }

    /// <summary>撞墙或咬到自己。</summary>
    public struct SnakeGameOverEvent
    {
        public int Score;
    }

    /// <summary>新一局开始（Reset 之后）。</summary>
    public struct SnakeStartedEvent
    {
    }

    /// <summary>
    /// 贪吃蛇棋盘：纯 C#，不碰引擎 —— 整个玩法逻辑可以在无头环境逐拍验证。
    ///
    /// 两条框架规矩的落点：
    /// 1. 每拍路径零分配：身体是预分配数组，事件是 struct 走 Publish，一拍不 new 任何东西；
    /// 2. 随机用框架的确定性随机（PCG32）：同样的种子同样的食物序列，方便复现一个 bug。
    ///
    /// 驱动方式：宿主每帧累计真实时间，攒够一拍调一次 <see cref="Tick"/>。
    /// 时间在宿主手里，棋盘只管"一拍走一格"，这样才能脱离引擎做测试。
    /// </summary>
    public sealed class SnakeBoard
    {
        public const int Grid = 28;
        public const float CellSize = 1f;

        private readonly EventBus _bus;
        private readonly IRandomSource _random;
        private readonly int[] _bx;
        private readonly int[] _by;
        private readonly bool[] _occupied;

        private int _count;
        private int _dirX = 1;
        private int _dirY;
        private int _pendingX;
        private int _pendingY;
        private bool _pending;
        private SnakeCell _food;
        private bool _alive;
        private int _score;

        public SnakeBoard(EventBus bus, IRandomSource random)
        {
            _bus = bus;
            _random = random;
            _bx = new int[Grid * Grid];
            _by = new int[Grid * Grid];
            _occupied = new bool[Grid * Grid];
            Reset();
        }

        public bool IsAlive { get { return _alive; } }
        public int Score { get { return _score; } }
        public int BodyCount { get { return _count; } }
        public SnakeCell Food { get { return _food; } }
        public int DirX { get { return _dirX; } }
        public int DirY { get { return _dirY; } }

        /// <summary>第 index 节身体，0 是头。</summary>
        public SnakeCell GetBody(int index)
        {
            return new SnakeCell { X = _bx[index], Y = _by[index] };
        }

        /// <summary>AI 启发式要用：这一格是否可以进（在界内且没有身体）。</summary>
        public bool IsFree(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Grid && y < Grid && !_occupied[(y * Grid) + x];
        }

        /// <summary>
        /// 请求转向。掉头（相对当前方向 180 度）是新手第一个 bug，这里直接拒收 ——
        /// 规则内纠错，不抛异常；同一拍内的多次请求只保留最后一次。
        /// </summary>
        public void SetDirection(int dx, int dy)
        {
            if (!_alive || (dx == 0 && dy == 0))
            {
                return;
            }

            if (dx == -_dirX && dy == -_dirY)
            {
                return;
            }

            _pendingX = dx;
            _pendingY = dy;
            _pending = true;
        }

        /// <summary>推进一拍：转向、移动、吃食物、判死。死了以后调用是空操作。</summary>
        public void Tick()
        {
            if (!_alive)
            {
                return;
            }

            if (_pending)
            {
                _dirX = _pendingX;
                _dirY = _pendingY;
                _pending = false;
            }

            int nx = _bx[0] + _dirX;
            int ny = _by[0] + _dirY;
            bool grow = nx == _food.X && ny == _food.Y;

            if (nx < 0 || ny < 0 || nx >= Grid || ny >= Grid)
            {
                Die();
                return;
            }

            if (!grow)
            {
                // 尾巴这一拍让位：先清占用，"追着尾巴进格"才算合法。
                SetOccupied(_bx[_count - 1], _by[_count - 1], false);
            }

            if (_occupied[(ny * Grid) + nx])
            {
                Die();
                return;
            }

            if (grow)
            {
                _count++;
            }

            for (int i = _count - 1; i > 0; i--)
            {
                _bx[i] = _bx[i - 1];
                _by[i] = _by[i - 1];
            }

            _bx[0] = nx;
            _by[0] = ny;
            _occupied[(ny * Grid) + nx] = true;

            if (grow)
            {
                _score++;
                SpawnFood();
                _bus.Publish(new SnakeFoodEatenEvent { Score = _score });
            }

            _bus.Publish(new SnakeMovedEvent
            {
                Head = new SnakeCell { X = nx, Y = ny },
                DirX = _dirX,
                DirY = _dirY,
                Food = _food,
                Score = _score,
            });
        }

        /// <summary>重开一局：三节身位居中朝右，重新撒食物。发 <see cref="SnakeStartedEvent"/>。</summary>
        public void Reset()
        {
            for (int i = 0; i < _occupied.Length; i++)
            {
                _occupied[i] = false;
            }

            _count = 3;
            int cy = Grid / 2;
            for (int i = 0; i < _count; i++)
            {
                _bx[i] = (Grid / 2 - 1) - i;
                _by[i] = cy;
                _occupied[(cy * Grid) + _bx[i]] = true;
            }

            _dirX = 1;
            _dirY = 0;
            _pending = false;
            _score = 0;
            _alive = true;
            SpawnFood();
            _bus.Publish(new SnakeStartedEvent());
        }

        // ------------------------------------------------------------------ core

        private void Die()
        {
            _alive = false;
            _bus.Publish(new SnakeGameOverEvent { Score = _score });
        }

        /// <summary>随机撒食物：拒绝采样，撒不中 256 次就线性扫一遍。棋盘满了按终局处理。</summary>
        private void SpawnFood()
        {
            for (int attempt = 0; attempt < 256; attempt++)
            {
                int v = _random.Range(0, Grid * Grid);
                int x = v % Grid;
                int y = v / Grid;
                if (!_occupied[(y * Grid) + x])
                {
                    SetFood(x, y);
                    return;
                }
            }

            for (int y = 0; y < Grid; y++)
            {
                for (int x = 0; x < Grid; x++)
                {
                    if (!_occupied[(y * Grid) + x])
                    {
                        SetFood(x, y);
                        return;
                    }
                }
            }

            Die();
        }

        private void SetFood(int x, int y)
        {
            _food = new SnakeCell { X = x, Y = y };
        }

        private void SetOccupied(int x, int y, bool value)
        {
            _occupied[(y * Grid) + x] = value;
        }
    }
}
