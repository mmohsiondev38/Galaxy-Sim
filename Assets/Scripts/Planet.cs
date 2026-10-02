using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class Planet : MonoBehaviour
{
    public static readonly List<Planet> Planets = new List<Planet>();

    // Raised by each body involved in a contact: (this planet, the other planet, contact point)
    public static event Action<Planet, Planet, Vector3> Collided;

    [Header("Settings")]
    public Vector3 initialVelocity;
    public float gravityConstant = 6.674f; // Adjusted for Unity scale

    // SimulationManager clears this so it decides when the planet is launched
    [NonSerialized] public bool launchOnStart = true;

    private Rigidbody rb;

    public Rigidbody Body
    {
        get
        {
            if (rb == null) rb = GetComponent<Rigidbody>();
            return rb;
        }
    }

    void Awake()
    {
        // Physics steps at 50 Hz but Android renders at 60, so interpolate to avoid judder
        Body.interpolation = RigidbodyInterpolation.Interpolate;

        // Cheap protection against fast, small planets tunnelling through each other
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    void OnEnable()
    {
        Planets.Add(this);
    }

    void OnDisable()
    {
        Planets.Remove(this);
    }

    void Start()
    {
        Body.useGravity = false; // Disable standard Earth-down gravity

        // Apply the starting force/velocity
        if (launchOnStart) Body.linearVelocity = initialVelocity;
    }

    void FixedUpdate()
    {
        foreach (Planet planet in Planets)
        {
            if (planet == this) continue;
            ApplyGravity(planet);
        }
    }

    void ApplyGravity(Planet other)
    {
        Rigidbody rbOther = other.Body;

        Vector3 direction = Body.position - rbOther.position;
        float sqrDistance = direction.sqrMagnitude;

        if (sqrDistance == 0f) return; // Avoid division by zero

        // Calculate force magnitude: F = G * (m1 * m2) / r^2
        float forceMagnitude = gravityConstant * (Body.mass * rbOther.mass) / sqrDistance;
        Vector3 force = direction * (forceMagnitude / Mathf.Sqrt(sqrDistance));

        rbOther.AddForce(force);
    }

    void OnCollisionEnter(Collision collision)
    {
        Planet other = collision.rigidbody != null ? collision.rigidbody.GetComponent<Planet>() : null;
        if (other == null) return;

        Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : (Body.position + other.Body.position) * 0.5f;
        Collided?.Invoke(this, other, point);
    }
}
