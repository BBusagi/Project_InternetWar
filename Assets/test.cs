using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// POC 第一步：用鼠标控制球型地图。
/// 遵循设计文档规则：旋转球体（GlobeRoot），而不是相机；
/// 使用相机空间的轴 + 四元数，避免万向锁与极点锁定。
///
/// 使用方法：把本脚本挂到球体（或 GlobeRoot）上即可。
/// - 按住鼠标左键拖动：旋转球体
/// - 松开后：惯性继续旋转并逐渐停下
/// - 鼠标滚轮：拉近 / 拉远相机（不缩放球体）
/// </summary>
public class test : MonoBehaviour
{
    [Header("目标")]
    [Tooltip("要旋转的球体根节点。留空则默认使用本物体 (this.transform)。")]
    [SerializeField] private Transform globeRoot;

    [Tooltip("使用的相机。留空则默认使用 Camera.main。")]
    [SerializeField] private Camera targetCamera;

    [Header("旋转")]
    [Tooltip("鼠标拖动的旋转灵敏度（度 / 像素）。")]
    [SerializeField] private float rotationSpeed = 0.2f;

    [Header("惯性")]
    [Tooltip("松手后惯性衰减速度，越大停得越快。")]
    [SerializeField] private float inertiaDamping = 5f;

    [Tooltip("角速度低于该值（度/秒）时直接停止，避免长时间微小抖动。")]
    [SerializeField] private float minInertiaSpeed = 1f;

    [Header("缩放")]
    [Tooltip("滚轮缩放灵敏度。")]
    [SerializeField] private float zoomSpeed = 2f;

    [Tooltip("缩放范围相对初始距离的倍数。例如初始距离 20，则可缩放到 20*0.4 ~ 20*2.5。")]
    [SerializeField] private float minDistanceFactor = 0.4f;
    [SerializeField] private float maxDistanceFactor = 2.5f;

    // 运行时根据初始相机距离推算出的实际缩放范围
    private float minDistance;
    private float maxDistance;

    [Tooltip("相机距离平滑插值速度。")]
    [SerializeField] private float zoomSmooth = 10f;

    // 拖动状态
    private bool isDragging;

    // 惯性：绕世界空间某轴的角速度（度/秒），方向为轴、大小为角速度。
    private Vector3 angularVelocity;

    // 缩放：以球心为参考的目标距离
    private float targetDistance;

    private void Awake()
    {
        if (globeRoot == null)
            globeRoot = transform;

        if (targetCamera == null)
            targetCamera = Camera.main;
    }

    private void Start()
    {
        if (targetCamera != null)
        {
            // 直接采用你在场景里摆好的初始相机距离，不做强制钳制，避免相机第一帧被拉走
            Vector3 toCam = targetCamera.transform.position - globeRoot.position;
            float initialDistance = toCam.magnitude;
            targetDistance = initialDistance;

            // 缩放范围围绕初始距离自动推算，尊重你的场景设置
            minDistance = initialDistance * minDistanceFactor;
            maxDistance = initialDistance * maxDistanceFactor;
        }
    }

    private void Update()
    {
        if (targetCamera == null)
        {
            // 相机可能在运行时才创建，尝试补获取一次
            targetCamera = Camera.main;
            if (targetCamera == null)
                return;
        }

        Mouse mouse = Mouse.current;
        if (mouse == null)
            return; // 没有鼠标设备

        HandleRotation(mouse);
        HandleZoom(mouse);
    }

    private void HandleRotation(Mouse mouse)
    {
        if (mouse.leftButton.wasPressedThisFrame)
        {
            isDragging = true;
            angularVelocity = Vector3.zero; // 抓住球体时清除惯性
        }

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            isDragging = false;
        }

        if (isDragging)
        {
            Vector2 delta = mouse.delta.ReadValue();

            if (delta.sqrMagnitude > 0f)
            {
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
                {
                    angularVelocity = axis * (angle / Time.deltaTime);
                }
            }
            else
            {
                angularVelocity = Vector3.zero; // 停住不动
            }
        }
        else
        {
            ApplyInertia();
        }
    }

    private void ApplyInertia()
    {
        float speed = angularVelocity.magnitude;
        if (speed <= minInertiaSpeed)
        {
            angularVelocity = Vector3.zero;
            return;
        }

        // 继续按当前角速度旋转
        Vector3 axis = angularVelocity / speed;
        globeRoot.rotation = Quaternion.AngleAxis(speed * Time.deltaTime, axis) * globeRoot.rotation;

        // 帧率无关的指数衰减
        angularVelocity *= Mathf.Exp(-inertiaDamping * Time.deltaTime);
    }

    private void HandleZoom(Mouse mouse)
    {
        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            // scroll 一般是 ±120 的步进，除以 120 归一化
            targetDistance -= (scroll / 120f) * zoomSpeed;
            targetDistance = Mathf.Clamp(targetDistance, minDistance, maxDistance);
        }

        // 平滑地把相机移动到目标距离（改变相机距离，而不是缩放球体）
        Transform camT = targetCamera.transform;
        Vector3 dir = (camT.position - globeRoot.position).normalized;
        if (dir.sqrMagnitude < 0.0001f)
            dir = -camT.forward; // 兜底方向

        Vector3 desiredPos = globeRoot.position + dir * targetDistance;
        camT.position = Vector3.Lerp(camT.position, desiredPos, 1f - Mathf.Exp(-zoomSmooth * Time.deltaTime));
    }
}
