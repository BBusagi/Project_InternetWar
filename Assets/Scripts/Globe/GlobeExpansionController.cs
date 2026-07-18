using System.Collections.Generic;
using UnityEngine;

namespace GlobalExpansion.Globe
{
    /// <summary>
    /// 占领 / 侵占控制器。
    /// 规则：占领格(蓝色)会对每一条相邻的非占领边施加侵占，
    /// 每条相邻占领边每秒推进 invasionRatePerEdge 的进度。
    /// 因此非占领格的占领速度与"相邻占领边的数量"成正比：
    /// 1 条边 → 1/rate 秒；2 条边 → 一半时间；以此类推。
    /// 进度以颜色从原色渐变到占领色来呈现，达到 1 时正式占领。
    ///
    /// 用法：把本脚本挂到场景任意物体（如 GameManager）上，指定 GlobeGenerator。
    /// 用 GlobeInputController 的左键点击来手动设置初始占领格（种子）。
    /// </summary>
    public class GlobeExpansionController : MonoBehaviour
    {
        [Tooltip("Grid generator that owns the cells. Auto-found if left empty.")]
        [SerializeField] private GlobeGenerator generator;

        [Header("Occupation")]
        [Tooltip("Color of an occupied cell. The invasion fades from the cell's base color to this.")]
        [SerializeField] private Color occupiedColor = new Color(0.20f, 0.50f, 1f);

        [Tooltip("Invasion progress per second contributed by each adjacent occupied edge. 1/60 makes a single-edge cell take 60s.")]
        [Range(0f, 0.2f)]
        [SerializeField] private float invasionRatePerEdge = 1f / 60f;

        [Header("Debug")]
        [Tooltip("Log occupation toggles and controller binding.")]
        [SerializeField] private bool debugLog = true;

        // 邻居查找：cellId -> view
        private readonly Dictionary<int, GlobeCellView> _lookup = new Dictionary<int, GlobeCellView>();
        private int _lookupVersion = -1;

        /// <summary>占领色（供输入控制器点击时使用，保持颜色一致）。</summary>
        public Color OccupiedColor => occupiedColor;

        private void Awake()
        {
            if (generator == null)
                generator = FindFirstObjectByType<GlobeGenerator>();

            if (debugLog)
            {
                if (generator != null)
                    Debug.Log("[Expansion] 已绑定 GlobeGenerator。");
                else
                    Debug.LogWarning("[Expansion] 未找到 GlobeGenerator，扩张不会运行。");
            }
        }

        /// <summary>左键点击切换某个格子的占领状态（设置/取消种子）。</summary>
        public void ToggleOccupied(GlobeCellView cell)
        {
            if (cell == null)
                return;

            cell.ToggleOccupied(occupiedColor);
            if (debugLog)
                Debug.Log($"[Expansion] 切换占领 id={cell.CellId} 占领={cell.IsOccupied}");
        }

        private void Update()
        {
            if (generator == null)
                return;

            IReadOnlyList<GlobeCellView> views = generator.CellViews;
            if (views == null || views.Count == 0)
                return;

            RefreshLookup(views);

            float dt = Time.deltaTime;
            for (int i = 0; i < views.Count; i++)
            {
                GlobeCellView cell = views[i];
                if (cell == null || cell.IsOccupied)
                    continue;

                // 相邻的占领边数量 → 侵占速度
                int occupiedNeighbors = CountOccupiedNeighbors(cell);
                if (occupiedNeighbors <= 0)
                    continue;

                cell.AddInvasion(occupiedNeighbors * invasionRatePerEdge * dt, occupiedColor);
            }
        }

        private int CountOccupiedNeighbors(GlobeCellView cell)
        {
            int count = 0;
            List<int> neighbors = cell.Data.NeighborIds;
            for (int i = 0; i < neighbors.Count; i++)
            {
                if (_lookup.TryGetValue(neighbors[i], out GlobeCellView n) && n.IsOccupied)
                    count++;
            }
            return count;
        }

        private void RefreshLookup(IReadOnlyList<GlobeCellView> views)
        {
            // 网格重新生成时（版本号变化）才重建查找表，避免持有已销毁的旧格子引用
            if (_lookupVersion == generator.GenerationVersion)
                return;

            _lookup.Clear();
            for (int i = 0; i < views.Count; i++)
            {
                if (views[i] != null)
                    _lookup[views[i].CellId] = views[i];
            }
            _lookupVersion = generator.GenerationVersion;

            if (debugLog)
                Debug.Log($"[Expansion] 重建邻居查找表：{_lookup.Count} 格（版本 {_lookupVersion}）。");
        }
    }
}
