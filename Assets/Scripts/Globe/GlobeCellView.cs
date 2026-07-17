using UnityEngine;

namespace GlobalExpansion.Globe
{
    /// <summary>
    /// 单个格子的视图组件：持有网格、碰撞体，暴露 CellId，负责占领状态与颜色。
    /// 通过 AddComponent 由 GlobeGenerator 创建，所需组件自动挂上。
    /// 颜色用 MaterialPropertyBlock 实现，避免为 642 个格子各建一份材质。
    ///
    /// 占领(Occupied)：蓝色格子。侵占过程通过颜色从原色渐变到占领色来呈现。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class GlobeCellView : MonoBehaviour
    {
        public int CellId { get; private set; }
        public GlobeCellData Data { get; private set; }

        /// <summary>是否已被占领（完全变成占领色）。</summary>
        public bool IsOccupied { get; private set; }

        /// <summary>侵占进度 0~1，用于原色到占领色的渐变。</summary>
        public float InvasionProgress { get; private set; }

        private MeshRenderer _renderer;
        private Color _baseColor = Color.gray; // 未占领时的原始颜色

        private static MaterialPropertyBlock _mpb;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public void Initialize(GlobeCellData data, Mesh mesh, Material sharedMaterial)
        {
            Data = data;
            CellId = data.Id;

            GetComponent<MeshFilter>().sharedMesh = mesh;
            GetComponent<MeshCollider>().sharedMesh = mesh;

            _renderer = GetComponent<MeshRenderer>();
            _renderer.sharedMaterial = sharedMaterial;
        }

        /// <summary>设置格子的原始颜色。未被侵占时立即生效；否则只记录，用于取消占领后恢复。</summary>
        public void SetColor(Color color)
        {
            _baseColor = color;
            if (!IsOccupied && InvasionProgress <= 0f)
                ApplyColor(color);
        }

        /// <summary>直接设置占领/取消占领。占领→满进度显示占领色；取消→清零并恢复原色。</summary>
        public void SetOccupied(bool occupied, Color occupiedColor)
        {
            IsOccupied = occupied;
            InvasionProgress = occupied ? 1f : 0f;
            ApplyColor(occupied ? occupiedColor : _baseColor);
        }

        /// <summary>切换占领状态：未占领→占领，已占领→恢复原色。</summary>
        public void ToggleOccupied(Color occupiedColor)
        {
            SetOccupied(!IsOccupied, occupiedColor);
        }

        /// <summary>
        /// 累加侵占进度并按进度把颜色从原色渐变到占领色。
        /// 进度达到 1 时变为已占领。返回本次是否刚好完成占领。
        /// </summary>
        public bool AddInvasion(float amount, Color occupiedColor)
        {
            if (IsOccupied)
                return false;

            InvasionProgress = Mathf.Clamp01(InvasionProgress + amount);
            if (InvasionProgress >= 1f)
            {
                SetOccupied(true, occupiedColor);
                return true;
            }

            // 视觉：原色 → 占领色 的线性渐变
            ApplyColor(Color.Lerp(_baseColor, occupiedColor, InvasionProgress));
            return false;
        }

        private void ApplyColor(Color color)
        {
            if (_renderer == null)
                _renderer = GetComponent<MeshRenderer>();
            if (_mpb == null)
                _mpb = new MaterialPropertyBlock();

            _renderer.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, color); // URP Lit / Unlit
            _mpb.SetColor(ColorId, color);     // 兼容内置 Standard 兜底
            _renderer.SetPropertyBlock(_mpb);
        }
    }
}
