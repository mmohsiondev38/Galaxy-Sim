using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// Orbital camera for touch and mouse, reading Input System devices directly.
/// One-finger drag (or left/right mouse drag) rotates, two-finger pinch (or the scroll wheel) zooms.
/// Hovering never moves the camera, and any gesture that starts on UI is left to the UI.
/// </summary>
public class OrbitCameraMobile : MonoBehaviour
{
    [Header("Target Settings")]
    [SerializeField] private Transform target;
    [SerializeField] private Vector3 targetOffset = Vector3.zero;

    [Header("Rotation Settings")]
    [Tooltip("Degrees of rotation for a drag across the full screen height, x180")]
    [SerializeField] private float rotationSensitivity = 1f;
    [SerializeField] private bool invertY = false;
    [SerializeField] private float minVerticalAngle = -80f;
    [SerializeField] private float maxVerticalAngle = 80f;

    [Header("Zoom Settings")]
    [SerializeField] private bool enablePinchZoom = true;
    [Tooltip("Zoom per scroll-wheel unit; one notch is usually 120 units on Windows, less on macOS trackpads")]
    [SerializeField] private float scrollZoomSpeed = 0.001f;
    [SerializeField] private float minDistance = 2f;
    [SerializeField] private float maxDistance = 20f;
    [SerializeField] private float currentDistance = 10f;

    [Header("Momentum")]
    [SerializeField] private bool enableMomentum = true;
    [Tooltip("How quickly a flick slows down, per second")]
    [SerializeField] private float momentumDecay = 4f;

    [Header("Smoothing")]
    [SerializeField] private float rotationSmoothTime = 0.08f;
    [SerializeField] private float zoomSmoothTime = 0.15f;

    // Current and target angles; input moves the target, the camera eases towards it
    private float currentX;
    private float currentY;
    private float targetX;
    private float targetY;
    private float targetDistance;
    private float xVelocity;
    private float yVelocity;
    private float zoomVelocity;
    private Vector2 dragVelocity;
    private Vector2 momentum;

    // Touch gesture state
    private bool touchGestureActive;
    private bool touchGestureBlocked;
    private bool isPinching;
    private int pinchId0;
    private int pinchId1;
    private float previousPinchDistance;

    // Mouse drag state
    private bool mouseWasPressed;
    private bool mouseDragBlocked;

    // Reused for UI hit tests at the start of a gesture
    private PointerEventData pointerData;
    private EventSystem pointerDataOwner;
    private readonly List<RaycastResult> raycastResults = new List<RaycastResult>();

    private void Start()
    {
        Vector3 angles = transform.eulerAngles;
        currentX = targetX = angles.y;
        currentY = targetY = NormalizeAngle(angles.x);
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

    private void Update()
    {
        bool dragging = false;

        Touchscreen touchscreen = Touchscreen.current;
        if (touchscreen != null && HandleTouch(touchscreen, ref dragging))
        {
            // Touch owns the frame; skip the mouse so simulated or paired mice don't double up
        }
        else if (Mouse.current != null)
        {
            HandleMouse(Mouse.current, ref dragging);
        }

        if (!dragging) ApplyMomentum();
    }

    private void LateUpdate()
    {
        if (target == null) return;

        currentX = Mathf.SmoothDamp(currentX, targetX, ref xVelocity, rotationSmoothTime);
        currentY = Mathf.SmoothDamp(currentY, targetY, ref yVelocity, rotationSmoothTime);
        currentDistance = Mathf.SmoothDamp(currentDistance, targetDistance, ref zoomVelocity, zoomSmoothTime);

        Quaternion rotation = Quaternion.Euler(currentY, currentX, 0);
        Vector3 targetPosition = target.position + targetOffset;

        transform.position = targetPosition + rotation * Vector3.back * currentDistance;
        transform.LookAt(targetPosition);
    }

    #region Input

    /// <summary>
    /// Returns true while at least one finger is down
    /// </summary>
    private bool HandleTouch(Touchscreen touchscreen, ref bool dragging)
    {
        TouchControl first = null;
        TouchControl second = null;
        int count = 0;
        var touches = touchscreen.touches;
        for (int i = 0; i < touches.Count; i++)
        {
            if (!touches[i].press.isPressed) continue;
            if (count == 0) first = touches[i];
            else if (count == 1) second = touches[i];
            count++;
        }

        if (count == 0)
        {
            touchGestureActive = false;
            touchGestureBlocked = false;
            isPinching = false;
            return false;
        }

        // The first finger decides who owns the whole gesture: the UI or the camera
        if (!touchGestureActive)
        {
            touchGestureActive = true;
            touchGestureBlocked = IsOverUI(first.position.ReadValue());
            momentum = Vector2.zero;
            dragVelocity = Vector2.zero;
        }
        if (touchGestureBlocked) return true;

        if (count >= 2)
        {
            if (enablePinchZoom) HandlePinch(first, second);
            dragVelocity = Vector2.zero; // No flick after a pinch
            dragging = true;
            return true;
        }

        if (isPinching)
        {
            // Lifting one finger of a pinch shouldn't jump the camera
            isPinching = false;
            return true;
        }

        Rotate(first.delta.ReadValue());
        dragging = true;
        return true;
    }

    private void HandlePinch(TouchControl first, TouchControl second)
    {
        int id0 = first.touchId.ReadValue();
        int id1 = second.touchId.ReadValue();
        float distance = Vector2.Distance(first.position.ReadValue(), second.position.ReadValue());

        // Start a new pinch when it begins or the finger pair changes
        if (!isPinching || id0 != pinchId0 || id1 != pinchId1)
        {
            isPinching = true;
            pinchId0 = id0;
            pinchId1 = id1;
            previousPinchDistance = distance;
            return;
        }

        // Spreading fingers zooms in, pinching zooms out, proportional to the change
        if (distance > 1f && previousPinchDistance > 1f)
        {
            targetDistance = Mathf.Clamp(targetDistance * previousPinchDistance / distance, minDistance, maxDistance);
        }
        previousPinchDistance = distance;
    }

    private void HandleMouse(Mouse mouse, ref bool dragging)
    {
        Vector2 position = mouse.position.ReadValue();
        bool pressed = mouse.leftButton.isPressed || mouse.rightButton.isPressed;

        if (pressed && !mouseWasPressed)
        {
            mouseDragBlocked = IsOverUI(position);
            momentum = Vector2.zero;
            dragVelocity = Vector2.zero;
        }
        mouseWasPressed = pressed;

        if (pressed && !mouseDragBlocked)
        {
            Rotate(mouse.delta.ReadValue());
            dragging = true;
        }

        float scroll = mouse.scroll.ReadValue().y;
        if (scroll != 0f && !pressed && !IsOverUI(position))
        {
            targetDistance = Mathf.Clamp(targetDistance * Mathf.Exp(-scroll * scrollZoomSpeed), minDistance, maxDistance);
        }
    }

    private void Rotate(Vector2 pixelDelta)
    {
        // Resolution independent: a full screen-height drag turns 180 degrees at sensitivity 1
        Vector2 delta = pixelDelta * (180f * rotationSensitivity / Mathf.Max(1, Screen.height));
        AddRotation(delta);

        float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        dragVelocity = Vector2.Lerp(dragVelocity, delta / dt, 0.5f);
        momentum = enableMomentum ? dragVelocity : Vector2.zero;
    }

    private void ApplyMomentum()
    {
        if (!enableMomentum || momentum.sqrMagnitude < 0.01f)
        {
            momentum = Vector2.zero;
            return;
        }

        float dt = Time.unscaledDeltaTime;
        AddRotation(momentum * dt);
        momentum *= Mathf.Exp(-momentumDecay * dt);
    }

    private void AddRotation(Vector2 degrees)
    {
        targetX += degrees.x;
        targetY -= degrees.y * (invertY ? -1f : 1f);
        targetY = Mathf.Clamp(targetY, minVerticalAngle, maxVerticalAngle);
    }

    private bool IsOverUI(Vector2 screenPosition)
    {
        EventSystem eventSystem = EventSystem.current;
        if (eventSystem == null) return false;

        // Raycast directly: the UI module may not have processed a brand-new touch yet this frame
        if (pointerData == null || pointerDataOwner != eventSystem)
        {
            pointerData = new PointerEventData(eventSystem);
            pointerDataOwner = eventSystem;
        }
        pointerData.position = screenPosition;
        raycastResults.Clear();
        eventSystem.RaycastAll(pointerData, raycastResults);
        return raycastResults.Count > 0;
    }

    private static float NormalizeAngle(float angle) => angle > 180f ? angle - 360f : angle;

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
        targetX = 0f;
        targetY = 20f;
        targetDistance = 10f;
        momentum = Vector2.zero;
    }

    /// <summary>
    /// Instantly snap to a specific angle
    /// </summary>
    public void SetRotation(float horizontal, float vertical)
    {
        currentX = targetX = horizontal;
        currentY = targetY = Mathf.Clamp(vertical, minVerticalAngle, maxVerticalAngle);
        momentum = Vector2.zero;
    }

    /// <summary>
    /// Instantly set zoom distance
    /// </summary>
    public void SetDistance(float distance)
    {
        currentDistance = targetDistance = Mathf.Clamp(distance, minDistance, maxDistance);
    }

    /// <summary>
    /// Enable or disable pinch to zoom at runtime
    /// </summary>
    public void SetPinchZoomEnabled(bool enabled)
    {
        enablePinchZoom = enabled;
    }

    /// <summary>
    /// Enable or disable momentum/inertia at runtime
    /// </summary>
    public void SetMomentumEnabled(bool enabled)
    {
        enableMomentum = enabled;
        if (!enabled) momentum = Vector2.zero;
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

        // Draw min/max zoom ranges
        Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
        Gizmos.DrawWireSphere(target.position + targetOffset, minDistance);
        Gizmos.DrawWireSphere(target.position + targetOffset, maxDistance);
    }

    #endregion
}
