using UnityEngine;

namespace GlobalExpansion.Globe
{
    /// <summary>
    /// 单个格子的视图组件：持有网格、碰撞体，暴露 CellId，负责改颜色。
    /// 通过 AddComponent 由 GlobeGenerator 创建，所需组件自动挂上。
    /// 颜色用 MaterialPropertyBlock 实现，避免为 642 个格子各建一份材质。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class GlobeCellView : MonoBehaviour
    {
        public int CellId { get; private set; }
        public GlobeCellData Data { get; private set; }

        private MeshRenderer _renderer;

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

        /// <summary>改变本格子的颜色（不新建材质）。</summary>
        public void SetColor(Color color)
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
