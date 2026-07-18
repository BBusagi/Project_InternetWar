using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GlobalExpansion.Globe
{
    /// <summary>三层可视化模式，对应生成过程的三个阶段。</summary>
    public enum GlobeDisplayMode
    {
        BaseSphere,          // 基底球体（光滑）
        IcosphereTriangles,  // 三角面网（细分后的二十面体）
        DualCells            // 五/六边形对偶网面（默认，可交互）
    }

    /// <summary>
    /// 球面格子生成器：二十面体 → 细分 → 归一化 → Icosphere → 取对偶 →
    /// 每个 Icosphere 顶点变成一个格子（大多六边形 + 正好 12 个五边形）。
    ///
    /// 生成过程的三个阶段都会构建为独立图层，可用 DisplayMode 切换显示。
    ///
    /// 用法：挂到 GlobeRoot（会旋转的那个物体）上，运行即生成。
    /// </summary>
    public class GlobeGenerator : MonoBehaviour
    {
        [Header("Generation")]
        [Tooltip("Subdivision level. 3 -> 642 cells; 2 -> 162 cells (early tech test).")]
        [SerializeField, Range(0, 5)] private int subdivisions = 3;

        [Tooltip("Sphere radius in local space. Should match the scene base sphere radius.")]
        [SerializeField] private float radius = 10f;

        [Tooltip("Cell radius scale above the sphere radius, to avoid Z-fighting with lower layers.")]
        [SerializeField] private float cellRadiusScale = 1.01f;

        [Header("Visualization")]
        [Tooltip("Which layer to show: base sphere / triangle net / dual cells. Switchable at runtime.")]
        [SerializeField] private GlobeDisplayMode displayMode = GlobeDisplayMode.DualCells;

        [Tooltip("Show edge lines (triangle edges / cell borders).")]
        [SerializeField] private bool showEdges = true;

        [Header("Mount / Material")]
        [Tooltip("Parent for the layers. Leave empty to use this transform (should be GlobeRoot).")]
        [SerializeField] private Transform cellParent;

        [Tooltip("Shared surface material. Leave empty to auto-create a URP material.")]
        [SerializeField] private Material surfaceMaterial;

        [Header("Colors")]
        [SerializeField] private Color baseSphereColor = new Color(0.12f, 0.14f, 0.20f);
        [SerializeField] private Color hexColor = new Color(0.32f, 0.36f, 0.42f);
        [SerializeField] private Color pentagonColor = new Color(0.90f, 0.60f, 0.22f);
        [Tooltip("Triangle net edge color (bright, so it reads on the dark sphere).")]
        [SerializeField] private Color triangleEdgeColor = new Color(0.55f, 0.85f, 0.95f);
        [Tooltip("Cell border color (dark, against the light cells).")]
        [SerializeField] private Color cellBorderColor = new Color(0.05f, 0.06f, 0.08f);

        [Header("Debug")]
        [Tooltip("Log topology validation (pentagon count, neighbor counts, bidirectionality) after generation.")]
        [SerializeField] private bool validateTopology = true;

        [Tooltip("Log generation events (params, cache hits, cell counts).")]
        [SerializeField] private bool debugLog = true;

        // --- 生成中间数据 ---
        private readonly List<Vector3> _positions = new List<Vector3>(); // Icosphere 顶点（已归一化到 radius）
        private readonly Dictionary<long, int> _midpointCache = new Dictionary<long, int>();
        private List<int> _icoFaces;

        // --- 图层根节点 ---
        private Transform _baseSphereRoot;
        private Transform _trianglesRoot;
        private Transform _cellsRoot;

        // --- 运行时材质 ---
        private Material _triangleEdgeMaterial;
        private Material _cellBorderMaterial;

        // --- 结果 ---
        private readonly List<GlobeCellData> _cells = new List<GlobeCellData>();
        public IReadOnlyList<GlobeCellData> Cells => _cells;

        private readonly List<GlobeCellView> _cellViews = new List<GlobeCellView>();
        public IReadOnlyList<GlobeCellView> CellViews => _cellViews;

        // --- 缓存：只有生成参数（细分/半径/抬高倍数）变化时才重新生成 ---
        private bool _generated;
        private int _lastSubdivisions = -1;
        private float _lastRadius = float.NaN;
        private float _lastCellScale = float.NaN;

        /// <summary>每次实际生成自增，供其它系统（如扩张控制器）检测网格是否已重建。</summary>
        public int GenerationVersion { get; private set; }

        private void Start()
        {
            EnsureGenerated();
        }

        private void Update()
        {
            // 运行时检测到球型基体大小（半径/细分/抬高倍数）变化才重新生成
            if (_generated && ParamsChanged())
            {
                if (debugLog)
                    Debug.Log("[GlobeGenerator] 生成参数变化，重新生成网格。");
                Generate();
            }
        }

        private void OnValidate()
        {
            // 运行时在 Inspector 改 displayMode / showEdges 时即时切换
            if (_cellsRoot != null || _trianglesRoot != null || _baseSphereRoot != null)
                ApplyDisplayMode();
        }

        /// <summary>已按当前参数生成过则命中缓存直接返回，否则生成。</summary>
        public void EnsureGenerated()
        {
            if (_generated && _cellViews.Count > 0 && !ParamsChanged())
            {
                if (debugLog)
                    Debug.Log($"[GlobeGenerator] 命中缓存，跳过生成（{_cellViews.Count} 格）。");
                return;
            }
            Generate();
        }

        private bool ParamsChanged()
        {
            return _lastSubdivisions != subdivisions
                   || !Mathf.Approximately(_lastRadius, radius)
                   || !Mathf.Approximately(_lastCellScale, cellRadiusScale);
        }

        [ContextMenu("Generate")]
        public void Generate()
        {
            if (cellParent == null)
                cellParent = transform;

            if (debugLog)
                Debug.Log($"[GlobeGenerator] 生成网格 sub={subdivisions} radius={radius} scale={cellRadiusScale}");

            ClearExistingLayers();
            EnsureMaterials();

            BuildIcosphere();

            _baseSphereRoot = CreateLayerRoot("Layer_BaseSphere");
            _trianglesRoot = CreateLayerRoot("Layer_Triangles");
            _cellsRoot = CreateLayerRoot("Layer_Cells");

            BuildBaseSphereLayer();
            BuildTriangleLayer();
            BuildDualCells();

            ApplyDisplayMode();

            // 记录本次生成参数用于缓存判断
            _generated = true;
            _lastSubdivisions = subdivisions;
            _lastRadius = radius;
            _lastCellScale = cellRadiusScale;
            GenerationVersion++;

            if (validateTopology)
                ValidateTopology();
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            if (cellParent == null)
                cellParent = transform;
            ClearExistingLayers();
            _cells.Clear();
            _cellViews.Clear();
            _generated = false;
        }

        // ============================================================
        // 1) 二十面体 + 细分 = Icosphere
        // ============================================================
        private void BuildIcosphere()
        {
            _positions.Clear();
            _midpointCache.Clear();

            float t = (1f + Mathf.Sqrt(5f)) / 2f;

            AddNormalized(new Vector3(-1, t, 0));
            AddNormalized(new Vector3(1, t, 0));
            AddNormalized(new Vector3(-1, -t, 0));
            AddNormalized(new Vector3(1, -t, 0));
            AddNormalized(new Vector3(0, -1, t));
            AddNormalized(new Vector3(0, 1, t));
            AddNormalized(new Vector3(0, -1, -t));
            AddNormalized(new Vector3(0, 1, -t));
            AddNormalized(new Vector3(t, 0, -1));
            AddNormalized(new Vector3(t, 0, 1));
            AddNormalized(new Vector3(-t, 0, -1));
            AddNormalized(new Vector3(-t, 0, 1));

            // 20 个三角面（每 3 个索引一组），朝外缠绕
            List<int> faces = new List<int>
            {
                0, 11, 5,  0, 5, 1,   0, 1, 7,   0, 7, 10,  0, 10, 11,
                1, 5, 9,   5, 11, 4,  11, 10, 2, 10, 7, 6,  7, 1, 8,
                3, 9, 4,   3, 4, 2,   3, 2, 6,   3, 6, 8,   3, 8, 9,
                4, 9, 5,   2, 4, 11,  6, 2, 10,  8, 6, 7,   9, 8, 1
            };

            for (int s = 0; s < subdivisions; s++)
            {
                List<int> next = new List<int>(faces.Count * 4);
                for (int f = 0; f < faces.Count; f += 3)
                {
                    int a = faces[f], b = faces[f + 1], c = faces[f + 2];
                    int ab = GetMidpoint(a, b);
                    int bc = GetMidpoint(b, c);
                    int ca = GetMidpoint(c, a);

                    next.Add(a); next.Add(ab); next.Add(ca);
                    next.Add(b); next.Add(bc); next.Add(ab);
                    next.Add(c); next.Add(ca); next.Add(bc);
                    next.Add(ab); next.Add(bc); next.Add(ca);
                }
                faces = next;
            }

            _icoFaces = faces;
        }

        private void AddNormalized(Vector3 v)
        {
            _positions.Add(v.normalized * radius);
        }

        private int GetMidpoint(int a, int b)
        {
            int lo = Mathf.Min(a, b);
            int hi = Mathf.Max(a, b);
            long key = ((long)lo << 32) | (uint)hi;

            if (_midpointCache.TryGetValue(key, out int idx))
                return idx;

            Vector3 mid = ((_positions[a] + _positions[b]) * 0.5f).normalized * radius;
            idx = _positions.Count;
            _positions.Add(mid);
            _midpointCache[key] = idx;
            return idx;
        }

        // ============================================================
        // 图层 A：基底球体（共享顶点 → 平滑法线）
        // ============================================================
        private void BuildBaseSphereLayer()
        {
            Mesh mesh = new Mesh { name = "BaseSphere", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(_positions);
            mesh.SetTriangles(_icoFaces, 0);
            mesh.RecalculateNormals(); // 顶点共享 → 平滑
            mesh.RecalculateBounds();

            CreateMeshObject("BaseSphere", _baseSphereRoot, mesh, baseSphereColor);
        }

        // ============================================================
        // 图层 B：三角面网的边线（衬在基底球体表面上）
        // ============================================================
        private void BuildTriangleLayer()
        {
            // 三角边线：从所有三角形的边收集去重，紧贴基底球面之上
            HashSet<long> edgeSet = new HashSet<long>();
            List<int> lineIndices = new List<int>(_icoFaces.Count * 2);
            for (int f = 0; f < _icoFaces.Count; f += 3)
            {
                AddUniqueEdge(edgeSet, lineIndices, _icoFaces[f], _icoFaces[f + 1]);
                AddUniqueEdge(edgeSet, lineIndices, _icoFaces[f + 1], _icoFaces[f + 2]);
                AddUniqueEdge(edgeSet, lineIndices, _icoFaces[f + 2], _icoFaces[f]);
            }
            BuildEdgeLines("TriangleEdges", _trianglesRoot, _positions, lineIndices, 1.002f, _triangleEdgeMaterial);
        }

        private static void AddUniqueEdge(HashSet<long> set, List<int> indices, int a, int b)
        {
            int lo = Mathf.Min(a, b);
            int hi = Mathf.Max(a, b);
            long key = ((long)lo << 32) | (uint)hi;
            if (set.Add(key))
            {
                indices.Add(a);
                indices.Add(b);
            }
        }

        // ============================================================
        // 图层 C：对偶网面（五/六边形格子）
        // ============================================================
        private void BuildDualCells()
        {
            _cells.Clear();
            _cellViews.Clear();

            int vertexCount = _positions.Count;
            int faceCount = _icoFaces.Count / 3;
            float cellRadius = radius * cellRadiusScale;

            // 每个三角面的中心点（= 对偶格子的角点），抬高到 cellRadius
            Vector3[] centroids = new Vector3[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                int a = _icoFaces[f * 3];
                int b = _icoFaces[f * 3 + 1];
                int c = _icoFaces[f * 3 + 2];
                Vector3 sum = _positions[a] + _positions[b] + _positions[c];
                centroids[f] = sum.normalized * cellRadius;
            }

            // 顶点 → 相邻三角面
            List<int>[] vertexFaces = new List<int>[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                vertexFaces[i] = new List<int>(6);
            for (int f = 0; f < faceCount; f++)
            {
                vertexFaces[_icoFaces[f * 3]].Add(f);
                vertexFaces[_icoFaces[f * 3 + 1]].Add(f);
                vertexFaces[_icoFaces[f * 3 + 2]].Add(f);
            }

            // 顶点 → 相邻顶点（邻居格子），来自三角形的边
            HashSet<int>[] vertexNeighbors = new HashSet<int>[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                vertexNeighbors[i] = new HashSet<int>();
            for (int f = 0; f < faceCount; f++)
            {
                int a = _icoFaces[f * 3];
                int b = _icoFaces[f * 3 + 1];
                int c = _icoFaces[f * 3 + 2];
                AddNeighbor(vertexNeighbors, a, b);
                AddNeighbor(vertexNeighbors, b, c);
                AddNeighbor(vertexNeighbors, c, a);
            }

            List<int> borderLineIndices = new List<int>();
            List<Vector3> borderVerts = new List<Vector3>();

            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 center = _positions[v];
                Vector3 normal = center.normalized;

                Vector3[] corners = SortCornersAroundNormal(vertexFaces[v], centroids, center, normal);

                GlobeCellData data = new GlobeCellData
                {
                    Id = v,
                    CellType = corners.Length == 5 ? CellType.Pentagon : CellType.Hexagon,
                    CenterDirection = normal,
                    PolygonVertices = corners
                };
                data.NeighborIds.AddRange(vertexNeighbors[v]);
                _cells.Add(data);

                CreateCellObject(data, normal * cellRadius);
                AppendCellBorder(borderVerts, borderLineIndices, corners, radius * (cellRadiusScale + 0.001f) / cellRadius);
            }

            BuildEdgeLines("CellBorders", _cellsRoot, borderVerts, borderLineIndices, 1f, _cellBorderMaterial);
        }

        private static void AddNeighbor(HashSet<int>[] neighbors, int a, int b)
        {
            neighbors[a].Add(b);
            neighbors[b].Add(a);
        }

        /// <summary>
        /// 把格子角点在中心点切平面内按角度排好序。
        /// 基向量 (tangent, bitangent, normal) 为右手系（cross(tangent,bitangent)=normal），
        /// 因此按 atan2 升序排序即为绕 +normal 的逆时针（朝外）顺序，无需再做缠绕反转。
        /// </summary>
        private static Vector3[] SortCornersAroundNormal(List<int> adjFaces, Vector3[] centroids, Vector3 center, Vector3 normal)
        {
            Vector3 tangent = Vector3.Cross(normal, Vector3.up);
            if (tangent.sqrMagnitude < 1e-6f)
                tangent = Vector3.Cross(normal, Vector3.right);
            tangent.Normalize();
            Vector3 bitangent = Vector3.Cross(normal, tangent);

            int k = adjFaces.Count;
            var entries = new List<(float angle, Vector3 pos)>(k);
            for (int i = 0; i < k; i++)
            {
                Vector3 p = centroids[adjFaces[i]];
                Vector3 dir = p - center;
                float angle = Mathf.Atan2(Vector3.Dot(dir, bitangent), Vector3.Dot(dir, tangent));
                entries.Add((angle, p));
            }
            entries.Sort((x, y) => x.angle.CompareTo(y.angle));

            Vector3[] corners = new Vector3[k];
            for (int i = 0; i < k; i++)
                corners[i] = entries[i].pos;
            return corners;
        }

        private static void AppendCellBorder(List<Vector3> verts, List<int> indices, Vector3[] corners, float lift)
        {
            int baseIndex = verts.Count;
            int k = corners.Length;
            for (int i = 0; i < k; i++)
                verts.Add(corners[i] * lift);
            for (int i = 0; i < k; i++)
            {
                indices.Add(baseIndex + i);
                indices.Add(baseIndex + (i + 1) % k);
            }
        }

        // ============================================================
        // GameObject / 网格构建工具
        // ============================================================
        private void CreateCellObject(GlobeCellData data, Vector3 centerPos)
        {
            Mesh mesh = BuildFanMesh(data.PolygonVertices, centerPos);

            GameObject go = new GameObject($"Cell_{data.Id}_{data.CellType}");
            go.transform.SetParent(_cellsRoot, false);

            GlobeCellView view = go.AddComponent<GlobeCellView>();
            view.Initialize(data, mesh, surfaceMaterial);
            view.SetColor(data.CellType == CellType.Pentagon ? pentagonColor : hexColor);
            _cellViews.Add(view);
        }

        /// <summary>用中心 + 角点构建三角扇网格（本地空间）。</summary>
        private static Mesh BuildFanMesh(Vector3[] corners, Vector3 centerPos)
        {
            int k = corners.Length;
            Vector3[] verts = new Vector3[k + 1];
            verts[0] = centerPos;
            for (int i = 0; i < k; i++)
                verts[i + 1] = corners[i];

            int[] tris = new int[k * 3];
            for (int i = 0; i < k; i++)
            {
                tris[i * 3] = 0;
                tris[i * 3 + 1] = 1 + i;
                tris[i * 3 + 2] = 1 + ((i + 1) % k);
            }

            Mesh mesh = new Mesh { name = "CellMesh" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void CreateMeshObject(string name, Transform parent, Mesh mesh, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = surfaceMaterial;
            SetRendererColor(mr, color);
        }

        private void BuildEdgeLines(string name, Transform parent, IList<Vector3> verts, List<int> indices, float lift, Material material)
        {
            if (indices.Count == 0)
                return;

            Vector3[] v = new Vector3[verts.Count];
            for (int i = 0; i < verts.Count; i++)
                v[i] = verts[i] * lift;

            Mesh mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(v);
            mesh.SetIndices(indices.ToArray(), MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
        }

        private Transform CreateLayerRoot(string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(cellParent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        // ============================================================
        // 显示模式 / 材质 / 清理
        // ============================================================
        public void ApplyDisplayMode()
        {
            // 三角面网模式也显示基底球体，作为边线贴合的实心衬底
            bool showBaseSphere = displayMode == GlobeDisplayMode.BaseSphere
                                  || displayMode == GlobeDisplayMode.IcosphereTriangles;

            if (_baseSphereRoot != null)
                _baseSphereRoot.gameObject.SetActive(showBaseSphere);
            if (_trianglesRoot != null)
                _trianglesRoot.gameObject.SetActive(displayMode == GlobeDisplayMode.IcosphereTriangles);
            if (_cellsRoot != null)
                _cellsRoot.gameObject.SetActive(displayMode == GlobeDisplayMode.DualCells);

            SetEdgesVisible(_trianglesRoot, "TriangleEdges");
            SetEdgesVisible(_cellsRoot, "CellBorders");
        }

        private void SetEdgesVisible(Transform layer, string edgeName)
        {
            if (layer == null)
                return;
            Transform edges = layer.Find(edgeName);
            if (edges != null)
                edges.gameObject.SetActive(showEdges);
        }

        private void EnsureMaterials()
        {
            if (surfaceMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Standard");
                surfaceMaterial = new Material(shader) { name = "GlobeSurface (auto)" };
            }

            if (_triangleEdgeMaterial == null)
                _triangleEdgeMaterial = CreateUnlitMaterial("TriangleEdges (auto)", triangleEdgeColor);
            if (_cellBorderMaterial == null)
                _cellBorderMaterial = CreateUnlitMaterial("CellBorders (auto)", cellBorderColor);
        }

        private static Material CreateUnlitMaterial(string name, Color color)
        {
            Shader lineShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (lineShader == null) lineShader = Shader.Find("Unlit/Color");
            if (lineShader == null) lineShader = Shader.Find("Sprites/Default");
            Material mat = new Material(lineShader) { name = name };
            mat.SetColor("_BaseColor", color);
            mat.SetColor("_Color", color);
            return mat;
        }

        private static void SetRendererColor(Renderer r, Color color)
        {
            MaterialPropertyBlock mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", color);
            mpb.SetColor("_Color", color);
            r.SetPropertyBlock(mpb);
        }

        private void ClearExistingLayers()
        {
            if (cellParent == null)
                return;

            for (int i = cellParent.childCount - 1; i >= 0; i--)
            {
                Transform child = cellParent.GetChild(i);
                // 清理旧图层根，也兼容清理旧版本直接生成的 Cell_ 子物体
                if (!child.name.StartsWith("Layer_") && !child.name.StartsWith("Cell_"))
                    continue;

                if (Application.isPlaying)
                    Destroy(child.gameObject);
                else
                    DestroyImmediate(child.gameObject);
            }

            _baseSphereRoot = null;
            _trianglesRoot = null;
            _cellsRoot = null;
        }

        private void ValidateTopology()
        {
            int pentagons = 0;
            int badNeighborCount = 0;
            int nonBidirectional = 0;
            int badCornerCount = 0; // 角点数不是 5 或 6
            int degenerate = 0;     // 面积近 0 或有重合角点

            var byId = new Dictionary<int, GlobeCellData>(_cells.Count);
            foreach (var c in _cells)
                byId[c.Id] = c;

            var pentagonIds = new List<int>();

            foreach (var c in _cells)
            {
                if (c.CellType == CellType.Pentagon)
                {
                    pentagons++;
                    pentagonIds.Add(c.Id);
                }

                // 角点数应为 5（五边形）或 6（六边形）
                int corners = c.PolygonVertices != null ? c.PolygonVertices.Length : 0;
                if (corners != 5 && corners != 6)
                {
                    badCornerCount++;
                    Debug.LogWarning($"[GlobeGenerator] 格子 {c.Id} 角点数异常={corners}（应为5或6）——可能是奇怪的黄色点来源。");
                }

                // 退化多边形检测（面积近 0 / 重合角点）——这类会渲染成奇怪的点/尖刺
                if (IsDegenerate(c.PolygonVertices))
                {
                    degenerate++;
                    Debug.LogWarning($"[GlobeGenerator] 格子 {c.Id}({c.CellType}) 多边形退化（面积近 0 或角点重合）。");
                }

                int expected = c.CellType == CellType.Pentagon ? 5 : 6;
                if (c.NeighborIds.Count != expected)
                    badNeighborCount++;

                foreach (int n in c.NeighborIds)
                {
                    if (!byId.TryGetValue(n, out var other) || !other.NeighborIds.Contains(c.Id))
                        nonBidirectional++;
                }
            }

            string msg =
                $"[GlobeGenerator] 细分级={subdivisions} 格子总数={_cells.Count} " +
                $"五边形={pentagons}(应为12) 角点数异常={badCornerCount} 退化格子={degenerate} " +
                $"邻居数异常={badNeighborCount} 非双向邻接={nonBidirectional}";

            bool ok = pentagons == 12 && badNeighborCount == 0 && nonBidirectional == 0
                      && badCornerCount == 0 && degenerate == 0;
            if (ok)
                Debug.Log(msg + "  ✅ 拓扑正确");
            else
                Debug.LogError(msg + "  ❌ 拓扑异常");

            if (debugLog)
                Debug.Log($"[GlobeGenerator] 五边形格子 Id: {string.Join(",", pentagonIds)}");
        }

        /// <summary>多边形是否退化：任意相邻角点重合，或总面积近 0。</summary>
        private static bool IsDegenerate(Vector3[] corners)
        {
            if (corners == null || corners.Length < 3)
                return true;

            // 相邻角点重合
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 a = corners[i];
                Vector3 b = corners[(i + 1) % corners.Length];
                if ((a - b).sqrMagnitude < 1e-8f)
                    return true;
            }

            // 三角扇总面积
            float area2 = 0f;
            Vector3 c0 = corners[0];
            for (int i = 1; i < corners.Length - 1; i++)
                area2 += Vector3.Cross(corners[i] - c0, corners[i + 1] - c0).magnitude;
            return area2 < 1e-6f;
        }
    }
}
