using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using GlobalExpansion.Globe;

/// <summary>
/// POC 阶段的球体交互控制器。
/// 遵循设计文档规则：旋转球体（GlobeRoot），而不是相机；
/// 使用相机空间的轴 + 四元数，避免万向锁与极点锁定；相机始终注视球心。
///
/// 使用方法：把本脚本挂到 GlobeRoot（会旋转的根节点）上。
/// - 按住鼠标右键拖动：旋转球体
/// - 松开右键后：惯性继续旋转并逐渐停下
/// - 鼠标左键点击格子：切换选中（变色 / 恢复）
/// - 鼠标滚轮：拉近 / 拉远相机（改变相机距离，不缩放球体）
/// </summary>
public class test : MonoBehaviour
{
    // ============================================================
    // Inspector 字段
    // ============================================================
    [Header("Target")]
    [Tooltip("Globe root to rotate. Leave empty to use this transform.")]
    [SerializeField] private Transform globeRoot;

    [Tooltip("Camera used for interaction. Leave empty to use Camera.main.")]
    [SerializeField] private Camera targetCamera;

    [Header("Rotation")]
    [Tooltip("Drag rotation sensitivity in degrees per pixel.")]
    [SerializeField] private float rotationSpeed = 0.1f;

    [Header("Inertia")]
    [Tooltip("Higher value stops the spin sooner after release.")]
    [SerializeField] private float inertiaDamping = 6f;

    [Tooltip("Stop spinning below this angular speed (deg/sec) to avoid jitter.")]
    [SerializeField] private float minInertiaSpeed = 1f;

    [Header("Zoom")]
    [Tooltip("Mouse wheel zoom sensitivity.")]
    [SerializeField] private float zoomSpeed = 1f;

    [Tooltip("Zoom range as a multiple of the initial camera distance.")]
    [SerializeField] private float minDistanceFactor = 0.4f;
    [SerializeField] private float maxDistanceFactor = 2.5f;

    [Tooltip("Camera distance smoothing speed.")]
    [SerializeField] private float zoomSmooth = 10f;

    [Tooltip("Extra look-tilt added per zoom step, in degrees. 0 keeps the constant initial offset.")]
    [Range(-10.0f, 10.0f)]
    [SerializeField] private float zoomTiltDelta = 1f;

    [Header("Selection")]
    [Tooltip("Color applied to a cell while it is selected. Click again to restore.")]
    [SerializeField] private Color selectedColor = new Color(0.20f, 0.50f, 1f);

    [Tooltip("Pixel movement above which a press becomes a drag instead of a click.")]
    [SerializeField] private float dragThreshold = 8f;

    [Tooltip("Log click / raycast details to the Console for debugging selection.")]
    [SerializeField] private bool debugClicks = true;

    // ============================================================
    // 运行时状态
    // ============================================================
    // 缩放俯仰偏移的上限（度），避免相机在缩放极限处翻转
    private const float MaxExtraPitch = 45f;

    // 指针 / 拖动状态
    private bool _pointerDown;
    private bool _isDragging;
    private Vector2 _pointerDownPos;

    // 左键按下时是否命中球体（格子），以及命中的格子（用于点击选择）
    private bool _pressedOnCell;
    private GlobeCellView _pressedCell;

    // 惯性：绕世界空间某轴的角速度，方向为轴、大小为角速度（度/秒）
    private Vector3 _angularVelocity;

    // 缩放：相对球心的目标距离，以及由初始距离推算出的范围
    private float _initialDistance;
    private float _targetDistance;
    private float _minDistance;
    private float _maxDistance;

    // 相机初始朝向相对"正对球心"的偏移（保留你摆好的约 20° 俯仰）
    private Quaternion _lookRotationOffset = Quaternion.identity;
    private bool _aimInitialized;

    // ============================================================
    // Unity 生命周期
    // ============================================================
    private void Awake()
    {
        if (globeRoot == null)
            globeRoot = transform;
        if (targetCamera == null)
            targetCamera = Camera.main;
    }

    private void Start()
    {
        InitZoomRange();
        InitCameraAim();
    }

    private void Update()
    {
        if (!EnsureCamera())
            return;

        Mouse mouse = Mouse.current;
        if (mouse == null)
            return; // 没有鼠标设备

        HandlePointer(mouse);
        HandleZoom(mouse);
    }

    private void LateUpdate()
    {
        // 相机保持初始设定的注视角度（正对球心的方向 + 初始约 20° 偏移）
        if (!_aimInitialized || targetCamera == null || globeRoot == null)
            return;

        Vector3 toCenter = globeRoot.position - targetCamera.transform.position;
        if (toCenter.sqrMagnitude < 1e-6f)
            return;

        // 随缩放阶段性调整俯仰：以初始距离为基准，每偏离一个缩放步长(zoomSpeed)加 zoomTiltDelta 度
        float extraPitch = 0f;
        if (zoomSpeed > 0.0001f && zoomTiltDelta > 0f)
        {
            float currentDistance = toCenter.magnitude;
            float steps = (_initialDistance - currentDistance) / zoomSpeed;
            extraPitch = Mathf.Clamp(steps * zoomTiltDelta, -MaxExtraPitch, MaxExtraPitch);
        }

        Quaternion lookAtCenter = Quaternion.LookRotation(toCenter, Vector3.up);
        Quaternion tiltedOffset = _lookRotationOffset * Quaternion.Euler(extraPitch, 0f, 0f);
        targetCamera.transform.rotation = lookAtCenter * tiltedOffset;
    }

    // ============================================================
    // 指针：区分点击与拖动
    // ============================================================
    private void HandlePointer(Mouse mouse)
    {
        Vector2 mousePos = mouse.position.ReadValue();

        HandleRotateButton(mouse);       // 右键：旋转球体
        HandleSelectButton(mouse, mousePos); // 左键：点击选中/取消
    }

    /// <summary>右键按住拖动旋转球体，松开后交给惯性。</summary>
    private void HandleRotateButton(Mouse mouse)
    {
        if (mouse.rightButton.wasPressedThisFrame)
        {
            _isDragging = true;
            _angularVelocity = Vector3.zero; // 抓住球体时清除惯性
        }

        if (mouse.rightButton.wasReleasedThisFrame)
            _isDragging = false; // 松开 → 惯性接管并逐渐停下

        if (_isDragging)
            RotateFromDrag(mouse);
        else
            ApplyInertia();
    }

    /// <summary>左键点击选中/取消格子（不旋转）。</summary>
    private void HandleSelectButton(Mouse mouse, Vector2 mousePos)
    {
        if (mouse.leftButton.wasPressedThisFrame)
        {
            _pointerDown = true;
            _pointerDownPos = mousePos;
            bool overUI = IsPointerOverUI();
            // 记录按下时命中的格子，供松手时判定点击
            _pressedOnCell = !overUI && RaycastCell(mousePos, out _pressedCell);
            if (debugClicks)
                Debug.Log($"[Select] 左键按下 pos={mousePos} overUI={overUI} 命中格子={_pressedOnCell}");
        }

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            float moved = (mousePos - _pointerDownPos).magnitude;
            // 命中格子且未大幅移动 → 视为一次点击 → 切换该格选中
            if (_pointerDown && _pressedOnCell && _pressedCell != null && moved <= dragThreshold)
            {
                _pressedCell.ToggleSelected(selectedColor);
                if (debugClicks)
                    Debug.Log($"[Select] 切换格子 id={_pressedCell.CellId} 选中={_pressedCell.IsSelected}");
            }
            else if (debugClicks && _pointerDown)
            {
                Debug.Log($"[Select] 未选中（命中格子={_pressedOnCell} 位移={moved:F1} 阈值={dragThreshold}）");
            }

            _pointerDown = false;
            _pressedOnCell = false;
            _pressedCell = null;
        }
    }

    private void RotateFromDrag(Mouse mouse)
    {
        Vector2 delta = mouse.delta.ReadValue();
        if (delta.sqrMagnitude <= 0f)
        {
            _angularVelocity = Vector3.zero; // 停住不动
            return;
        }

        // 旋转轴来自相机，而不是固定世界轴——保证拖动方向在任意旋转后仍然直观
        Vector3 camUp = targetCamera.transform.up;
        Vector3 camRight = targetCamera.transform.right;

        // 水平移动 → 绕相机 up 轴；垂直移动 → 绕相机 right 轴
        float yaw = -delta.x * rotationSpeed;
        float pitch = delta.y * rotationSpeed;

        Quaternion deltaRot =
            Quaternion.AngleAxis(yaw, camUp) *
            Quaternion.AngleAxis(pitch, camRight);

        // 新的相机空间旋转叠加在当前旋转之前，保持拖动方向一致
        globeRoot.rotation = deltaRot * globeRoot.rotation;

        // 记录角速度供松手后的惯性使用
        deltaRot.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f; // 归一化到 [-180,180]
        if (Time.deltaTime > 0f && !float.IsInfinity(axis.x))
            _angularVelocity = axis * (angle / Time.deltaTime);
    }

    // ============================================================
    // 选择：射线命中格子并切换选中
    // ============================================================
    /// <summary>
    /// 从屏幕点发射线，返回最近的、带 GlobeCellView 的格子。
    /// 用 RaycastAll 过滤：即使底球等其它碰撞体挡在前面，也能命中最近的格子；
    /// 背面格子距离更远，因此仍然选不到（正面格子总是更近）。
    /// </summary>
    private bool RaycastCell(Vector2 screenPos, out GlobeCellView view)
    {
        view = null;

        if (targetCamera == null)
        {
            if (debugClicks) Debug.LogWarning("[Select] targetCamera 为空，无法发射线");
            return false;
        }

        Ray ray = targetCamera.ScreenPointToRay(screenPos);
        if (debugClicks) Debug.DrawRay(ray.origin, ray.direction * 1000f, Color.yellow, 1f);

        RaycastHit[] hits = Physics.RaycastAll(ray, Mathf.Infinity);
        if (hits.Length == 0)
        {
            if (debugClicks)
                Debug.Log("[Select] 射线没有命中任何碰撞体。确认：格子已生成且带 MeshCollider，且当前是 DualCells 显示模式（格子层激活）。");
            return false;
        }

        // 取最近的、带 GlobeCellView 的碰撞体（忽略底球等非格子碰撞体）
        float nearest = float.MaxValue;
        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].distance < nearest && hits[i].collider.TryGetComponent(out GlobeCellView v))
            {
                nearest = hits[i].distance;
                view = v;
            }
        }

        if (debugClicks)
        {
            if (view != null)
                Debug.Log($"[Select] 命中格子 '{view.name}' id={view.CellId}（射线共命中 {hits.Length} 个碰撞体）");
            else
                Debug.Log($"[Select] 射线命中 {hits.Length} 个碰撞体，但都不是格子（如底球）。第一个='{hits[0].collider.name}'");
        }

        return view != null;
    }

    private static bool IsPointerOverUI()
    {
        // 指针在 UI 上时不进行选择（遵守 EventSystem 检查）
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    private void ApplyInertia()
    {
        float speed = _angularVelocity.magnitude;
        if (speed <= minInertiaSpeed)
        {
            _angularVelocity = Vector3.zero;
            return;
        }

        // 继续按当前角速度旋转
        Vector3 axis = _angularVelocity / speed;
        globeRoot.rotation = Quaternion.AngleAxis(speed * Time.deltaTime, axis) * globeRoot.rotation;

        // 帧率无关的指数衰减
        _angularVelocity *= Mathf.Exp(-inertiaDamping * Time.deltaTime);
    }

    // ============================================================
    // 缩放（改变相机距离，不缩放球体）
    // ============================================================
    private void InitZoomRange()
    {
        if (targetCamera == null)
            return;

        // 直接采用你在场景里摆好的初始相机距离，不做强制钳制，避免相机第一帧被拉走
        _initialDistance = Vector3.Distance(targetCamera.transform.position, globeRoot.position);
        _targetDistance = _initialDistance;
        _minDistance = _initialDistance * minDistanceFactor;
        _maxDistance = _initialDistance * maxDistanceFactor;
    }

    private void InitCameraAim()
    {
        if (targetCamera == null || globeRoot == null)
            return;

        Vector3 toCenter = globeRoot.position - targetCamera.transform.position;
        if (toCenter.sqrMagnitude < 1e-6f)
            return;

        // 记录初始朝向相对"正对球心"的偏移，之后一直保持这个偏移
        Quaternion lookAtCenter = Quaternion.LookRotation(toCenter, Vector3.up);
        _lookRotationOffset = Quaternion.Inverse(lookAtCenter) * targetCamera.transform.rotation;
        _aimInitialized = true;
    }

    private void HandleZoom(Mouse mouse)
    {
        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            // scroll 一般是 ±120 的步进，除以 120 归一化
            _targetDistance -= (scroll / 120f) * zoomSpeed;
            _targetDistance = Mathf.Clamp(_targetDistance, _minDistance, _maxDistance);
        }

        // 沿"球心→相机"方向平滑移动相机到目标距离
        Transform camT = targetCamera.transform;
        Vector3 dir = (camT.position - globeRoot.position).normalized;
        if (dir.sqrMagnitude < 0.0001f)
            dir = -camT.forward; // 兜底方向

        Vector3 desiredPos = globeRoot.position + dir * _targetDistance;
        camT.position = Vector3.Lerp(camT.position, desiredPos, 1f - Mathf.Exp(-zoomSmooth * Time.deltaTime));
    }

    // ============================================================
    // 工具
    // ============================================================
    private bool EnsureCamera()
    {
        if (targetCamera != null)
            return true;

        // 相机可能在运行时才创建，尝试补获取一次
        targetCamera = Camera.main;
        if (targetCamera == null)
            return false;

        InitZoomRange();
        InitCameraAim();
        return true;
    }
}
