using System.Collections.Generic;
using UnityEngine;

namespace GlobalExpansion.Globe
{
    /// <summary>格子类型。五边形是总部候选点，永远只有 12 个。</summary>
    public enum CellType
    {
        Hexagon,
        Pentagon
    }

    /// <summary>
    /// 纯数据类：只存格子数据，不含任何渲染 / 输入 / MonoBehaviour 逻辑。
    /// 对应设计文档的 GlobeCellData。
    /// </summary>
    public class GlobeCellData
    {
        /// <summary>唯一格子 Id。</summary>
        public int Id;

        /// <summary>六边形 / 五边形。</summary>
        public CellType CellType;

        /// <summary>从球心指向格子中心的单位向量（球体本地空间）。</summary>
        public Vector3 CenterDirection;

        /// <summary>多边形角点，球体本地空间坐标（未排序前不要使用）。</summary>
        public Vector3[] PolygonVertices;

        /// <summary>相邻格子的 Id 列表（五边形 5 个，六边形 6 个）。</summary>
        public readonly List<int> NeighborIds = new List<int>(6);
    }
}
