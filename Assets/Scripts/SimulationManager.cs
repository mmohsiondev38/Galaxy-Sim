using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns the simulation lifecycle (stopped / running / paused / failed) and every body's starting conditions.
/// The heaviest body is the central body: fixed at the origin, it pulls on everything else.
/// Every other planet starts at a distance from it with a speed perpendicular to the radius.
/// While stopped, planets sit at their start positions as kinematic bodies so they can be edited.
/// Any collision fails the run: the bodies freeze, the colliding planets burst, and the
/// simulation resets to its start. Starting conditions are saved to PlayerPrefs.
/// </summary>
public class SimulationManager : MonoBehaviour
{
    public enum SimulationState { Stopped, Running, Paused, Failed }

    [Serializable]
    public class PlanetSetup
    {
        [NonSerialized] public Planet planet; // Runtime only; never written to the save
        public string name;
        public Vector3 direction;   // Unit direction from the central body to the start position
        public float distance;      // Start distance from the central body
        public float speed;         // Initial speed, perpendicular to the radius
        public float tilt;          // Degrees the initial velocity is tilted out of the horizontal plane
        public float size;          // Sphere diameter, drives mass
        public bool tinted;         // Recoloured on creation; the colour is saved with the setup
        public Color color = Color.white;
    }

    [Serializable]
    private class SaveData
    {
        public int version = SaveVersion;
        public float centralSize;
        public int addedPlanetCount;
        public List<PlanetSetup> orbiters = new List<PlanetSetup>();
    }

    private const string SaveKey = "OrbitSimulator.Setup";
    private const int SaveVersion = 1;
    private const float SaveDelay = 0.5f;

    [Header("Size To Mass")]
    [Tooltip("Mass = density * size^massExponent. 3 makes mass follow volume, 1 makes it linear in size.")]
    [SerializeField] private float density = 1f;
    [SerializeField] private float massExponent = 3f;

    [Header("New Planets")]
    [SerializeField] private float newPlanetSize = 1f;
    [SerializeField] private float newPlanetSpacing = 4f;

    [Header("Collisions")]
    [SerializeField] private CollisionBurst collisionBurst;
    [Tooltip("Seconds the failure stays on screen before resetting to the start")]
    [SerializeField] private float failResetDelay = 2.5f;

    [Header("Mobile")]
    [Tooltip("Android defaults to 30 fps; orbits look much smoother at 60")]
    [SerializeField] private int targetFrameRate = 60;

    public SimulationState State { get; private set; } = SimulationState.Stopped;
    public float SimulationTime { get; private set; }
    public PlanetSetup Central { get; private set; }
    public IReadOnlyList<PlanetSetup> Orbiters => orbiters;
    public int BodyCount => (Central != null ? 1 : 0) + orbiters.Count;
    public float FailResetDelay => failResetDelay;

    public event Action StateChanged;
    public event Action PlanetsChanged;
    public event Action<string> SimulationFailed;

    private readonly List<PlanetSetup> orbiters = new List<PlanetSetup>();
    private readonly Dictionary<PlanetSetup, (Vector3 linear, Vector3 angular)> pausedVelocities =
        new Dictionary<PlanetSetup, (Vector3 linear, Vector3 angular)>();
    private Planet planetTemplate;
    private int addedPlanetCount;
    private string defaultSetupJson;
    private bool saveDirty;
    private float lastEditTime;
    private Coroutine failReset;

    private void Awake()
    {
        Application.targetFrameRate = targetFrameRate;

        Planet[] planets = FindObjectsByType<Planet>(FindObjectsSortMode.None);
        if (planets.Length == 0)
        {
            Debug.LogWarning("SimulationManager: no Planet in the scene.");
            enabled = false;
            return;
        }

        Planet central = planets[0];
        foreach (Planet planet in planets)
        {
            if (planet.Body.mass > central.Body.mass) central = planet;
        }

        Vector3 center = central.transform.position;
        central.launchOnStart = false;
        Central = new PlanetSetup { planet = central, direction = Vector3.right, size = SizeForMass(central.Body.mass) };

        foreach (Planet planet in planets)
        {
            if (planet == central) continue;
            planet.launchOnStart = false;
            orbiters.Add(SetupFromScene(planet, center));
        }
        orbiters.Sort((a, b) => a.distance.CompareTo(b.distance));

        // Inactive copy used for new and restored planets, so adding still works after every planet is removed
        if (orbiters.Count > 0)
        {
            planetTemplate = Instantiate(orbiters[0].planet, transform);
            planetTemplate.gameObject.SetActive(false);
            planetTemplate.name = "Planet Template";
        }

        if (collisionBurst == null) collisionBurst = FindFirstObjectByType<CollisionBurst>();

        // The scene's setup is the default; a saved setup replaces it
        defaultSetupJson = JsonUtility.ToJson(Capture());
        string saved = PlayerPrefs.GetString(SaveKey, null);
        if (!string.IsNullOrEmpty(saved) && !LoadSetup(saved))
        {
            Debug.LogWarning("SimulationManager: saved setup could not be read, using the scene defaults.");
        }

        ResetSimulation();
    }

    private void OnEnable()
    {
        Planet.Collided += HandleCollision;
    }

    private void OnDisable()
    {
        Planet.Collided -= HandleCollision;
        SaveIfDirty();
    }

    private void OnDestroy()
    {
        Screen.sleepTimeout = SleepTimeout.SystemSetting;
    }

    // Android may kill a paused app without calling OnApplicationQuit, so save on pause too
    private void OnApplicationPause(bool paused)
    {
        if (paused) SaveIfDirty();
    }

    private void OnApplicationQuit()
    {
        SaveIfDirty();
    }

    private void OnValidate()
    {
        density = Mathf.Max(0.0001f, density);
        massExponent = Mathf.Max(0.1f, massExponent);
    }

    private void Update()
    {
        // Sliders change values every frame while dragged, so save once they settle
        if (saveDirty && Time.unscaledTime - lastEditTime > SaveDelay) SaveIfDirty();
    }

    private void FixedUpdate()
    {
        if (State == SimulationState.Running) SimulationTime += Time.fixedDeltaTime;
    }

    #region Lifecycle

    /// <summary>
    /// Launch from the start positions, or resume if paused
    /// </summary>
    public void StartSimulation()
    {
        if (Central == null) return;
        if (State == SimulationState.Running) return;
        if (State == SimulationState.Failed) ResetSimulation();

        bool resuming = State == SimulationState.Paused;
        if (!resuming) ClearTrail(Central.planet, true);

        foreach (PlanetSetup setup in orbiters)
        {
            if (!resuming) ClearTrail(setup.planet, true);

            Rigidbody body = setup.planet.Body;
            body.isKinematic = false;
            if (resuming && pausedVelocities.TryGetValue(setup, out var velocity))
            {
                body.linearVelocity = velocity.linear;
                body.angularVelocity = velocity.angular;
            }
            else
            {
                body.linearVelocity = InitialVelocity(setup);
                body.angularVelocity = Vector3.zero;
            }
        }

        pausedVelocities.Clear();
        SetState(SimulationState.Running);
    }

    /// <summary>
    /// Freeze every body where it is, remembering its velocity for resume
    /// </summary>
    public void PauseSimulation()
    {
        if (State != SimulationState.Running) return;

        foreach (PlanetSetup setup in orbiters)
        {
            Rigidbody body = setup.planet.Body;
            pausedVelocities[setup] = (body.linearVelocity, body.angularVelocity);
            body.isKinematic = true;
        }

        SetState(SimulationState.Paused);
    }

    /// <summary>
    /// Stop and put every body back at its start position
    /// </summary>
    public void ResetSimulation()
    {
        if (Central == null) return;

        if (failReset != null)
        {
            StopCoroutine(failReset);
            failReset = null;
        }

        pausedVelocities.Clear();
        ResetBody(Central);
        foreach (PlanetSetup setup in orbiters) ResetBody(setup);

        SimulationTime = 0f;
        SetState(SimulationState.Stopped);
    }

    private void HandleCollision(Planet planet, Planet other, Vector3 point)
    {
        // Both bodies report the contact; only the first one in a run counts
        if (State != SimulationState.Running) return;

        foreach (PlanetSetup setup in orbiters) setup.planet.Body.isKinematic = true;

        // The central body survives the impact; orbiting planets burst
        if (planet != Central.planet) SetVisible(planet, false);
        if (other != Central.planet) SetVisible(other, false);

        if (collisionBurst != null)
        {
            float scale = (planet.transform.localScale.x + other.transform.localScale.x) * 0.5f;
            collisionBurst.Play(point, RendererColor(planet), RendererColor(other), scale);
        }

        // Name the orbiting planet first: "Earth crashed into Sun"
        if (planet == Central.planet) (planet, other) = (other, planet);
        SetState(SimulationState.Failed);
        SimulationFailed?.Invoke($"{planet.name} crashed into {other.name}");
        failReset = StartCoroutine(ResetAfterFailure());
    }

    private IEnumerator ResetAfterFailure()
    {
        yield return new WaitForSeconds(failResetDelay);
        failReset = null;
        ResetSimulation();
    }

    #endregion

    #region Editing

    // Every edit changes the starting conditions, so a running, paused or failed simulation is reset first

    public void SetSize(PlanetSetup setup, float size)
    {
        PrepareEdit();
        setup.size = Mathf.Max(0.01f, size);
        ApplySetup(setup);
    }

    public void SetDistance(PlanetSetup setup, float distance)
    {
        PrepareEdit();
        setup.distance = Mathf.Max(0f, distance);
        ApplySetup(setup);
    }

    public void SetSpeed(PlanetSetup setup, float speed)
    {
        PrepareEdit();
        setup.speed = Mathf.Max(0f, speed);
        ApplySetup(setup);
    }

    public void SetTilt(PlanetSetup setup, float tilt)
    {
        PrepareEdit();
        setup.tilt = tilt;
        ApplySetup(setup);
    }

    public float GetMass(PlanetSetup setup) => MassForSize(setup.size);

    /// <summary>
    /// Speed for a circular orbit around the central body at the planet's start distance
    /// </summary>
    public float CircularSpeed(PlanetSetup setup)
    {
        if (Central == null || setup.distance <= 0f) return 0f;
        return Mathf.Sqrt(Central.planet.gravityConstant * MassForSize(Central.size) / setup.distance);
    }

    /// <summary>
    /// Reset, then add a planet one spacing beyond the outermost one, at circular-orbit speed
    /// </summary>
    public PlanetSetup AddPlanet()
    {
        ResetSimulation();

        float outermost = Central.size * 0.5f;
        foreach (PlanetSetup s in orbiters) outermost = Mathf.Max(outermost, s.distance);

        addedPlanetCount++;
        var setup = new PlanetSetup
        {
            name = $"Planet {orbiters.Count + 1}",
            // Golden-angle spread so successive planets don't line up
            direction = Quaternion.Euler(0f, 137.5f * addedPlanetCount, 0f) * Vector3.forward,
            distance = outermost + newPlanetSpacing,
            size = newPlanetSize,
            tinted = true,
            color = Color.HSVToRGB(UnityEngine.Random.value, 0.6f, 1f),
        };
        setup.speed = CircularSpeed(setup);

        if (!SpawnOrbiter(setup)) return null;

        MarkDirty();
        PlanetsChanged?.Invoke();
        return setup;
    }

    public void RemovePlanet(PlanetSetup setup)
    {
        if (!orbiters.Contains(setup)) return;

        ResetSimulation();
        orbiters.Remove(setup);
        DestroyPlanet(setup.planet);

        MarkDirty();
        PlanetsChanged?.Invoke();
    }

    /// <summary>
    /// Throw away the saved setup and go back to the scene's starting system
    /// </summary>
    public void RestoreDefaults()
    {
        PlayerPrefs.DeleteKey(SaveKey);
        PlayerPrefs.Save();
        saveDirty = false;

        LoadSetup(defaultSetupJson);
        ResetSimulation();
    }

    #endregion

    #region Saving

    private SaveData Capture()
    {
        var data = new SaveData { centralSize = Central.size, addedPlanetCount = addedPlanetCount };
        foreach (PlanetSetup setup in orbiters)
        {
            data.orbiters.Add(new PlanetSetup
            {
                name = setup.name,
                direction = setup.direction,
                distance = setup.distance,
                speed = setup.speed,
                tilt = setup.tilt,
                size = setup.size,
                tinted = setup.tinted,
                color = setup.color,
            });
        }
        return data;
    }

    /// <summary>
    /// Replace every orbiter with fresh copies of the template built from saved data
    /// </summary>
    private bool LoadSetup(string json)
    {
        SaveData data;
        try
        {
            data = JsonUtility.FromJson<SaveData>(json);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (data == null || data.version != SaveVersion || data.orbiters == null) return false;

        if (State != SimulationState.Stopped) ResetSimulation();

        foreach (PlanetSetup setup in orbiters) DestroyPlanet(setup.planet);
        orbiters.Clear();

        Central.size = Mathf.Max(0.01f, data.centralSize);
        ApplySetup(Central);
        addedPlanetCount = data.addedPlanetCount;

        foreach (PlanetSetup setup in data.orbiters)
        {
            setup.direction = setup.direction.sqrMagnitude > 0f ? setup.direction.normalized : Vector3.forward;
            if (string.IsNullOrEmpty(setup.name)) setup.name = "Planet";
            if (!SpawnOrbiter(setup)) break;
        }

        PlanetsChanged?.Invoke();
        return true;
    }

    private void MarkDirty()
    {
        saveDirty = true;
        lastEditTime = Time.unscaledTime;
    }

    private void SaveIfDirty()
    {
        if (!saveDirty || Central == null) return;

        PlayerPrefs.SetString(SaveKey, JsonUtility.ToJson(Capture()));
        PlayerPrefs.Save();
        saveDirty = false;
    }

    #endregion

    #region Helpers

    private PlanetSetup SetupFromScene(Planet planet, Vector3 center)
    {
        Vector3 offset = planet.transform.position - center;
        Vector3 direction = offset.sqrMagnitude > 0f ? offset.normalized : Vector3.forward;

        // Keep only the velocity perpendicular to the radius, expressed as speed + tilt
        Vector3 velocity = Vector3.ProjectOnPlane(planet.initialVelocity, direction);
        float tilt = velocity.sqrMagnitude > 0f ? Vector3.SignedAngle(Tangent(direction), velocity, direction) : 0f;

        return new PlanetSetup
        {
            planet = planet,
            name = planet.name,
            direction = direction,
            distance = offset.magnitude,
            speed = velocity.magnitude,
            tilt = tilt,
            size = SizeForMass(planet.Body.mass),
            color = RendererColor(planet),
        };
    }

    private bool SpawnOrbiter(PlanetSetup setup)
    {
        if (planetTemplate == null)
        {
            Debug.LogWarning("SimulationManager: the scene needs at least one orbiting Planet to copy for new planets.");
            return false;
        }

        Planet planet = Instantiate(planetTemplate, Central.planet.transform.parent);
        planet.name = setup.name;
        planet.launchOnStart = false;
        if (setup.tinted) TintPlanet(planet, setup.color);
        else setup.color = RendererColor(planet);

        setup.planet = planet;
        orbiters.Add(setup);
        ApplySetup(setup);

        planet.gameObject.SetActive(true);
        ClearTrail(planet, false);
        return true;
    }

    private static void DestroyPlanet(Planet planet)
    {
        if (planet == null) return;

        // Deactivate first so it leaves Planet.Planets immediately, not at end of frame
        planet.gameObject.SetActive(false);
        Destroy(planet.gameObject);
    }

    private void PrepareEdit()
    {
        if (State != SimulationState.Stopped) ResetSimulation();
        MarkDirty();
    }

    private void ResetBody(PlanetSetup setup)
    {
        ApplySetup(setup);
        SetVisible(setup.planet, true);
        ClearTrail(setup.planet, false);
    }

    private void ApplySetup(PlanetSetup setup)
    {
        Rigidbody body = setup.planet.Body;
        Vector3 position = setup == Central ? Vector3.zero : setup.direction * setup.distance;

        body.isKinematic = true;
        setup.planet.transform.position = position;
        body.position = position;
        setup.planet.transform.localScale = Vector3.one * setup.size;
        body.mass = MassForSize(setup.size);
        setup.planet.initialVelocity = setup == Central ? Vector3.zero : InitialVelocity(setup);
    }

    private static Vector3 InitialVelocity(PlanetSetup setup)
    {
        return Quaternion.AngleAxis(setup.tilt, setup.direction) * Tangent(setup.direction) * setup.speed;
    }

    // Horizontal direction of travel for a counter-clockwise (seen from above) orbit
    private static Vector3 Tangent(Vector3 direction)
    {
        Vector3 tangent = Vector3.Cross(Vector3.up, direction);
        return tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector3.right;
    }

    private float MassForSize(float size) => density * Mathf.Pow(size, massExponent);

    private float SizeForMass(float mass) => Mathf.Pow(Mathf.Max(0.0001f, mass) / density, 1f / massExponent);

    private static Color RendererColor(Planet planet)
    {
        Renderer renderer = planet.GetComponent<Renderer>();
        return renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.color : Color.white;
    }

    private static void TintPlanet(Planet planet, Color color)
    {
        Renderer renderer = planet.GetComponent<Renderer>();
        if (renderer != null) renderer.material.color = color;

        TrailRenderer trail = planet.GetComponent<TrailRenderer>();
        if (trail != null)
        {
            trail.startColor = color;
            trail.endColor = new Color(color.r, color.g, color.b, 0f);
        }
    }

    private static void SetVisible(Planet planet, bool visible)
    {
        Renderer renderer = planet.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.enabled = visible;

        // Disable the collider too, so a burst planet can't be hit again
        Collider collider = planet.GetComponent<Collider>();
        if (collider != null) collider.enabled = visible;
    }

    private static void ClearTrail(Planet planet, bool emitting)
    {
        TrailRenderer trail = planet.GetComponent<TrailRenderer>();
        if (trail == null) return;

        trail.Clear();
        trail.emitting = emitting;
    }

    private void SetState(SimulationState state)
    {
        State = state;

        // Keep the screen awake while orbits are running
        Screen.sleepTimeout = state == SimulationState.Running ? SleepTimeout.NeverSleep : SleepTimeout.SystemSetting;
        StateChanged?.Invoke();
    }

    #endregion
}
