using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Orbital camera controller that rotates around a target and supports zoom
/// Uses Unity's new Input System
/// </summary>
public class OrbitCamera : MonoBehaviour
{
    [Header("Target Settings")]
    [SerializeField] private Transform target;
    [SerializeField] private Vector3 targetOffset = Vector3.zero;

    [Header("Rotation Settings")]
    [SerializeField] private float rotationSpeed = 100f;
    [SerializeField] private bool invertY = false;
    [SerializeField] private float minVerticalAngle = -80f;
    [SerializeField] private float maxVerticalAngle = 80f;

    [Header("Zoom Settings")]
    [SerializeField] private float zoomSpeed = 10f;
    [SerializeField] private float minDistance = 2f;
    [SerializeField] private float maxDistance = 20f;
    [SerializeField] private float currentDistance = 10f;

    [Header("Smoothing")]
    [SerializeField] private float rotationSmoothTime = 0.12f;
    [SerializeField] private float zoomSmoothTime = 0.2f;

    // Input values
    private Vector2 rotationInput;
    private float zoomInput;

    // Current angles
    private float currentX = 0f;
    private float currentY = 0f;

    // Smoothing velocities
    private Vector2 rotationVelocity;
    private float zoomVelocity;

    // Target values for smoothing
    private float targetDistance;

    private void Start()
    {
        // Initialize with current rotation
        Vector3 angles = transform.eulerAngles;
        currentX = angles.y;
        currentY = angles.x;
        
        targetDistance = currentDistance;

        // If no target is assigned, create a default one at origin
        if (target == null)
        {
            GameObject targetObj = new GameObject("Camera Target");
            target = targetObj.transform;
            target.position = Vector3.zero;
            Debug.LogWarning("No target assigned to OrbitCamera. Created default target at origin.");
        }
    }

    private void LateUpdate()
    {
        if (target == null) return;

        // Smooth rotation input
        Vector2 smoothRotation = Vector2.SmoothDamp(
            Vector2.zero, 
            rotationInput * rotationSpeed, 
            ref rotationVelocity, 
            rotationSmoothTime
        );

        // Update rotation angles
        currentX += smoothRotation.x * Time.deltaTime;
        currentY -= smoothRotation.y * Time.deltaTime * (invertY ? -1 : 1);

        // Clamp vertical rotation
        currentY = Mathf.Clamp(currentY, minVerticalAngle, maxVerticalAngle);

        // Update zoom distance
        targetDistance = Mathf.Clamp(targetDistance - zoomInput, minDistance, maxDistance);
        currentDistance = Mathf.SmoothDamp(currentDistance, targetDistance, ref zoomVelocity, zoomSmoothTime);

        // Calculate position
        Quaternion rotation = Quaternion.Euler(currentY, currentX, 0);
        Vector3 direction = rotation * Vector3.back;
        Vector3 targetPosition = target.position + targetOffset;

        transform.position = targetPosition + direction * currentDistance;
        transform.LookAt(targetPosition);
    }

    #region Input Callbacks
    
    /// <summary>
    /// Called when rotation input is received (e.g., right mouse drag or gamepad stick)
    /// </summary>
    public void OnRotate(InputAction.CallbackContext context)
    {
        rotationInput = context.ReadValue<Vector2>();
    }

    /// <summary>
    /// Called when zoom input is received (e.g., mouse scroll wheel)
    /// </summary>
    public void OnZoom(InputAction.CallbackContext context)
    {
        zoomInput = context.ReadValue<float>();
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Set a new target for the camera to orbit around
    /// </summary>
    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
    }

    /// <summary>
    /// Reset camera to default position
    /// </summary>
    public void ResetCamera()
    {
        currentX = 0f;
        currentY = 20f;
        currentDistance = 10f;
        targetDistance = currentDistance;
    }

    /// <summary>
    /// Instantly snap to a specific angle
    /// </summary>
    public void SetRotation(float horizontal, float vertical)
    {
        currentX = horizontal;
        currentY = Mathf.Clamp(vertical, minVerticalAngle, maxVerticalAngle);
    }

    /// <summary>
    /// Instantly set zoom distance
    /// </summary>
    public void SetDistance(float distance)
    {
        currentDistance = Mathf.Clamp(distance, minDistance, maxDistance);
        targetDistance = currentDistance;
    }

    #endregion

    #region Debug Gizmos

    private void OnDrawGizmosSelected()
    {
        if (target == null) return;

        // Draw orbit radius
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(target.position + targetOffset, currentDistance);

        // Draw target position
        Gizmos.color = Color.red;
        Gizmos.DrawSphere(target.position + targetOffset, 0.2f);

        // Draw line from camera to target
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, target.position + targetOffset);
    }

    #endregion
}