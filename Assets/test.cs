using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// POC 阶段的球体交互控制器。
/// 遵循设计文档规则：旋转球体（GlobeRoot），而不是相机；
/// 使用相机空间的轴 + 四元数，避免万向锁与极点锁定；相机始终注视球心。
///
/// 使用方法：把本脚本挂到 GlobeRoot（会旋转的根节点）上。
/// - 按住鼠标左键拖动：旋转球体
/// - 松开后：惯性继续旋转并逐渐停下
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

    // ============================================================
    // 运行时状态
    // ============================================================
    // 拖动状态
    private bool _isDragging;

    // 惯性：绕世界空间某轴的角速度，方向为轴、大小为角速度（度/秒）
    private Vector3 _angularVelocity;

    // 缩放：相对球心的目标距离，以及由初始距离推算出的范围
    private float _targetDistance;
    private float _minDistance;
    private float _maxDistance;

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
    }

    private void Update()
    {
        if (!EnsureCamera())
            return;

        Mouse mouse = Mouse.current;
        if (mouse == null)
            return; // 没有鼠标设备

        HandleRotation(mouse);
        HandleZoom(mouse);
    }

    private void LateUpdate()
    {
        // 相机始终注视球心（相机只做径向缩放，不独立环绕）
        if (targetCamera != null && globeRoot != null)
            targetCamera.transform.LookAt(globeRoot.position);
    }

    // ============================================================
    // 旋转 + 惯性
    // ============================================================
    private void HandleRotation(Mouse mouse)
    {
        if (mouse.leftButton.wasPressedThisFrame)
        {
            _isDragging = true;
            _angularVelocity = Vector3.zero; // 抓住球体时清除惯性
        }

        if (mouse.leftButton.wasReleasedThisFrame)
            _isDragging = false;

        if (!_isDragging)
        {
            ApplyInertia();
            return;
        }

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
        float initialDistance = Vector3.Distance(targetCamera.transform.position, globeRoot.position);
        _targetDistance = initialDistance;
        _minDistance = initialDistance * minDistanceFactor;
        _maxDistance = initialDistance * maxDistanceFactor;
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
        return true;
    }
}
