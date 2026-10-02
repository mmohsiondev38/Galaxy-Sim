using UnityEngine;

/// <summary>
/// Burst effect played where two planets collide: a bright flash plus a spray of sparks
/// in both planets' colours. Both particle systems are built once and reused, so a
/// collision costs no allocations or instantiation.
/// </summary>
public class CollisionBurst : MonoBehaviour
{
    [Tooltip("Additive particle material; its HDR colour pushes the burst over the bloom threshold")]
    [SerializeField] private Material particleMaterial;
    [SerializeField] private int sparkCount = 120;
    [SerializeField] private int glowCount = 10;
    [SerializeField] private float sparkSpeed = 9f;
    [SerializeField] private float flashSize = 5f;

    private ParticleSystem sparks;
    private ParticleSystem flash;

    private void Awake()
    {
        sparks = CreateSparks();
        flash = CreateFlash();
    }

    /// <summary>
    /// Play the burst at a point; scale grows the effect with the size of the colliding planets
    /// </summary>
    public void Play(Vector3 position, Color colorA, Color colorB, float scale)
    {
        transform.position = position;
        scale = Mathf.Max(0.3f, scale);

        // One big stationary flash, then softer glowing puffs drifting outwards
        var flashParams = new ParticleSystem.EmitParams
        {
            startSize = flashSize * scale,
            startColor = Color.white,
            startLifetime = 0.6f,
            velocity = Vector3.zero,
        };
        flash.Emit(flashParams, 1);

        var glowParams = new ParticleSystem.EmitParams
        {
            startSize = 1.3f * scale,
            startColor = Color.Lerp(Color.Lerp(colorA, colorB, 0.5f), Color.white, 0.4f),
        };
        flash.Emit(glowParams, glowCount);

        // Half the sparks in each planet's colour, faster for bigger impacts
        var sparkParams = new ParticleSystem.EmitParams();
        ParticleSystem.MainModule main = sparks.main;
        main.startSpeedMultiplier = sparkSpeed * Mathf.Sqrt(scale);

        sparkParams.startColor = Color.Lerp(colorA, Color.white, 0.35f);
        sparks.Emit(sparkParams, sparkCount / 2);
        sparkParams.startColor = Color.Lerp(colorB, Color.white, 0.35f);
        sparks.Emit(sparkParams, sparkCount - sparkCount / 2);
    }

    private ParticleSystem CreateSparks()
    {
        ParticleSystem system = CreateSystem("Sparks", 256);

        ParticleSystem.MainModule main = system.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.35f, 1f); // Scaled by startSpeedMultiplier in Play
        main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);

        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.2f;

        // Sparks slow down and burn out
        ParticleSystem.LimitVelocityOverLifetimeModule limit = system.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.drag = 1.5f;

        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        color.color = FadeOut();

        var renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.08f;
        renderer.lengthScale = 2f;
        return system;
    }

    private ParticleSystem CreateFlash()
    {
        ParticleSystem system = CreateSystem("Flash", 32);

        ParticleSystem.MainModule main = system.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.1f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 2.5f);

        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.3f;

        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.4f, 1f, 1.3f));

        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        color.color = FadeOut();
        return system;
    }

    private ParticleSystem CreateSystem(string name, int maxParticles)
    {
        var child = new GameObject(name);
        child.transform.SetParent(transform, false);

        var system = child.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = system.main;
        main.playOnAwake = false;
        main.loop = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = maxParticles;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.enabled = false; // Only emits through Play

        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = false;

        var renderer = child.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = particleMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return system;
    }

    private static ParticleSystem.MinMaxGradient FadeOut()
    {
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) });
        return new ParticleSystem.MinMaxGradient(gradient);
    }
}
