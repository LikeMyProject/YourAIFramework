using System.Collections.Generic;
using UnityEngine;
using YourFramework.Pool;

namespace YourAIFramework.SnakeGame
{
    /// <summary>
    /// 视图：每帧把棋盘状态同步到池化的 Cube 上，食物是一颗 Sphere。
    /// 全部程序化生成，不依赖任何美术资产。
    ///
    /// 对象池教学的落点：身体长度一直变，借还配平交给 <see cref="ObjectPool{T}"/> ——
    /// 多借（同一对象借两次）、少还（重复归还）都会当场抛错，而不是默默漏对象。
    /// 每帧稳态运行零分配：_used 列表只复用不重建，池取还也不 new。
    /// </summary>
    public sealed class SnakeView : MonoBehaviour
    {
        private SnakeBoard _board;
        private ObjectPool<GameObject> _pool;
        private readonly List<GameObject> _used = new List<GameObject>(64);
        private GameObject _food;
        private Transform _root;

        public void Init(SnakeBoard board)
        {
            _board = board;
            _root = new GameObject("[SnakeView]").transform;
            MakeWalls();

            _pool = new ObjectPool<GameObject>(
                MakeSegment,
                delegate(GameObject go) { go.SetActive(true); },
                delegate(GameObject go) { go.SetActive(false); },
                128);

            _food = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _food.name = "SnakeFood";
            _food.transform.SetParent(_root, false);
            _food.transform.localScale = new Vector3(0.7f, 0.7f, 0.7f);
            _food.GetComponent<Renderer>().material.color = new Color(0.90f, 0.30f, 0.25f);
        }

        private void LateUpdate()
        {
            Sync();
        }

        public void Sync()
        {
            if (_board == null || _pool == null)
            {
                return;
            }

            while (_used.Count < _board.BodyCount)
            {
                _used.Add(_pool.Get());
            }

            while (_used.Count > _board.BodyCount)
            {
                _pool.Release(_used[_used.Count - 1]);
                _used.RemoveAt(_used.Count - 1);
            }

            for (int i = 0; i < _board.BodyCount; i++)
            {
                GameObject segment = _used[i];
                segment.transform.position = ToWorld(_board.GetBody(i));
                // 头亮一点，身子暗一点，方向一眼可辨。
                segment.GetComponent<Renderer>().material.color = i == 0
                    ? new Color(0.35f, 0.85f, 0.40f)
                    : new Color(0.22f, 0.58f, 0.27f);
            }

            _food.transform.position = ToWorld(_board.Food);
            _food.SetActive(_board.IsAlive);
        }

        /// <summary>格坐标 → 世界坐标。棋盘以原点为中心摆。</summary>
        public static Vector3 ToWorld(SnakeCell cell)
        {
            return new Vector3(
                (cell.X - (SnakeBoard.Grid / 2) + 0.5f) * SnakeBoard.CellSize,
                (cell.Y - (SnakeBoard.Grid / 2) + 0.5f) * SnakeBoard.CellSize,
                0f);
        }

        /// <summary>棋盘边界：四条细墙贴在格子区外沿，玩家一眼知道撞哪里会死。</summary>
        private void MakeWalls()
        {
            float half = SnakeBoard.Grid / 2f;
            float thickness = 0.4f;
            Color wallColor = new Color(0.45f, 0.42f, 0.38f);
            MakeWall("WallTop", new Vector3(0f, half + (thickness / 2f), 0f),
                new Vector3(SnakeBoard.Grid + 1f, thickness, 1f), wallColor);
            MakeWall("WallBottom", new Vector3(0f, -half - (thickness / 2f), 0f),
                new Vector3(SnakeBoard.Grid + 1f, thickness, 1f), wallColor);
            MakeWall("WallLeft", new Vector3(-half - (thickness / 2f), 0f, 0f),
                new Vector3(thickness, SnakeBoard.Grid + 1f, 1f), wallColor);
            MakeWall("WallRight", new Vector3(half + (thickness / 2f), 0f, 0f),
                new Vector3(thickness, SnakeBoard.Grid + 1f, 1f), wallColor);
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

        private GameObject MakeSegment()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "SnakeSegment";
            go.transform.SetParent(_root, false);
            go.transform.localScale = new Vector3(0.9f, 0.9f, 0.9f);
            return go;
        }

        private void OnDestroy()
        {
            if (_pool != null)
            {
                // 池里囤着的对象带引擎资源，销毁要显式交给宿主 —— Clear 的 onDestroy 钩子就是干这个的。
                _pool.Clear(delegate(GameObject go) { Destroy(go); });
            }
        }
    }
}
