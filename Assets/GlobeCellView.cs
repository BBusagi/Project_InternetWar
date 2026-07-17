using UnityEngine;

namespace GlobalExpansion.Globe
{
    /// <summary>
    /// 单个格子的视图组件：持有网格、碰撞体，暴露 CellId，负责改颜色与选中状态。
    /// 通过 AddComponent 由 GlobeGenerator 创建，所需组件自动挂上。
    /// 颜色用 MaterialPropertyBlock 实现，避免为 642 个格子各建一份材质。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class GlobeCellView : MonoBehaviour
    {
        public int CellId { get; private set; }
        public GlobeCellData Data { get; private set; }

        /// <summary>是否处于选中（高亮）状态。选中是视觉状态，与所有权无关。</summary>
        public bool IsSelected { get; private set; }

        private MeshRenderer _renderer;
        private Color _baseColor = Color.gray; // 未选中时的原始颜色

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

        /// <summary>设置格子的原始颜色。未选中时立即生效；已选中时仅记录，取消选中后恢复到该色。</summary>
        public void SetColor(Color color)
        {
            _baseColor = color;
            if (!IsSelected)
                ApplyColor(color);
        }

        /// <summary>设置选中状态，选中时显示 selectedColor，取消时恢复原始颜色。</summary>
        public void SetSelected(bool selected, Color selectedColor)
        {
            IsSelected = selected;
            ApplyColor(selected ? selectedColor : _baseColor);
        }

        /// <summary>切换选中状态：未选中→选中(selectedColor)，已选中→恢复原色。</summary>
        public void ToggleSelected(Color selectedColor)
        {
            SetSelected(!IsSelected, selectedColor);
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
