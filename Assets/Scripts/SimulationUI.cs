using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Builds the runtime control panel for SimulationManager: start / pause / reset, add / remove planets,
/// a card for the central body (size, which drives mass) and one per orbiting planet
/// (size, start distance, orbit speed and tilt).
/// Built in code because the planet cards change whenever planets are added or removed.
/// Sized for landscape phones; the Android back button toggles the panel.
/// </summary>
[RequireComponent(typeof(SimulationManager))]
public class SimulationUI : MonoBehaviour
{
    [Header("Layout")]
    [SerializeField] private Vector2 referenceResolution = new Vector2(1280f, 720f);
    [SerializeField] private float panelWidth = 420f;

    [Header("Slider Ranges")]
    [SerializeField] private Vector2 sizeRange = new Vector2(0.2f, 8f);
    [SerializeField] private Vector2 centralSizeRange = new Vector2(1f, 10f);
    [SerializeField] private Vector2 distanceRange = new Vector2(1f, 60f);
    [SerializeField] private float maxSpeed = 30f;
    [SerializeField] private float maxTilt = 90f;

    private const float RowHeight = 44f;
    private const float ButtonHeight = 56f;

    private static readonly Color PanelColor = new Color(0.07f, 0.08f, 0.12f, 0.9f);
    private static readonly Color CardColor = new Color(1f, 1f, 1f, 0.06f);
    private static readonly Color ButtonColor = new Color(0.24f, 0.4f, 0.7f, 1f);
    private static readonly Color AccentColor = new Color(0.22f, 0.55f, 0.38f, 1f);
    private static readonly Color DangerColor = new Color(0.66f, 0.24f, 0.26f, 1f);
    private static readonly Color TrackColor = new Color(1f, 1f, 1f, 0.15f);
    private static readonly Color TextColor = new Color(0.93f, 0.95f, 0.98f, 1f);
    private static readonly Color MutedTextColor = new Color(0.66f, 0.7f, 0.78f, 1f);

    private class SliderRow
    {
        public Slider slider;
        public Text valueText;
        public string format;

        public void SetValueWithoutNotify(float value)
        {
            slider.SetValueWithoutNotify(value);
            valueText.text = value.ToString(format);
        }
    }

    private class PlanetCard
    {
        public SimulationManager.PlanetSetup setup;
        public Text infoText;
        public SliderRow speedRow;
    }

    private SimulationManager manager;
    private Font font;
    private RectTransform safeArea;
    private GameObject panel;
    private GameObject showButton;
    private RectTransform cardContainer;
    private Text statusText;
    private Button startButton;
    private Text startLabel;
    private Button pauseButton;
    private Button resetButton;
    private Rect appliedSafeArea;
    private Vector2Int appliedScreenSize;
    private PlanetCard centralCard;
    private readonly List<PlanetCard> orbiterCards = new List<PlanetCard>();
    private RectTransform failBanner;
    private Text failDetailText;
    private float failShownAt;

    private void Awake()
    {
        manager = GetComponent<SimulationManager>();
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        BuildCanvas();
    }

    private void OnEnable()
    {
        manager.StateChanged += RefreshState;
        manager.PlanetsChanged += RebuildCards;
        manager.SimulationFailed += ShowFailure;
    }

    private void OnDisable()
    {
        manager.StateChanged -= RefreshState;
        manager.PlanetsChanged -= RebuildCards;
        manager.SimulationFailed -= ShowFailure;
    }

    private void Start()
    {
        RebuildCards();
        RefreshState();
    }

    private void Update()
    {
        ApplySafeArea();

        // Android back button arrives as Escape
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            SetPanelVisible(!panel.activeSelf);
        }

        if (manager.State == SimulationManager.SimulationState.Running) UpdateStatus();
        if (failBanner.gameObject.activeSelf) AnimateFailBanner();
    }

    #region State

    private void RefreshState()
    {
        if (startButton == null) return;

        SimulationManager.SimulationState state = manager.State;
        startLabel.text = state == SimulationManager.SimulationState.Paused ? "Resume" : "Start";
        startButton.interactable = state != SimulationManager.SimulationState.Running;
        pauseButton.interactable = state == SimulationManager.SimulationState.Running;
        resetButton.interactable = state != SimulationManager.SimulationState.Stopped;
        if (state != SimulationManager.SimulationState.Failed) failBanner.gameObject.SetActive(false);
        UpdateStatus();
    }

    private void ShowFailure(string detail)
    {
        failDetailText.text = detail;
        failShownAt = Time.unscaledTime;
        failBanner.gameObject.SetActive(true);
        AnimateFailBanner();
    }

    private void AnimateFailBanner()
    {
        // Quick overshoot pop as the banner appears
        float t = Mathf.Clamp01((Time.unscaledTime - failShownAt) / 0.25f);
        float scale = t < 1f ? Mathf.LerpUnclamped(0.7f, 1f, 1f + 2.2f * Mathf.Pow(t - 1f, 3f) + 1.2f * Mathf.Pow(t - 1f, 2f)) : 1f;
        failBanner.localScale = Vector3.one * scale;
    }

    private void UpdateStatus()
    {
        // Only touch the text when it changes; it sits on its own canvas so this doesn't rebuild the panel
        string status = $"{manager.State}   |   t = {manager.SimulationTime:0.0} s   |   {manager.BodyCount} bodies";
        if (statusText.text != status) statusText.text = status;
    }

    private void SetPanelVisible(bool visible)
    {
        panel.SetActive(visible);
        showButton.SetActive(!visible);
    }

    private void ApplySafeArea()
    {
        Rect area = Screen.safeArea;
        var screenSize = new Vector2Int(Screen.width, Screen.height);
        if (screenSize.x <= 0 || screenSize.y <= 0) return;
        if (area == appliedSafeArea && screenSize == appliedScreenSize) return;

        appliedSafeArea = area;
        appliedScreenSize = screenSize;
        safeArea.anchorMin = new Vector2(area.xMin / screenSize.x, area.yMin / screenSize.y);
        safeArea.anchorMax = new Vector2(area.xMax / screenSize.x, area.yMax / screenSize.y);

        // Legacy Text generated at the old canvas scale stays blurry until regenerated
        foreach (Text text in safeArea.GetComponentsInChildren<Text>(true)) text.SetAllDirty();
    }

    #endregion

    #region Panel

    private void BuildCanvas()
    {
        var canvasObject = new GameObject("Simulation Canvas", typeof(RectTransform), typeof(Canvas),
            typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);

        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;

        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = referenceResolution;
        scaler.matchWidthOrHeight = 0.5f;

        safeArea = CreateRect("Safe Area", canvasObject.transform);
        Stretch(safeArea);

        // Left-docked panel, full safe-area height
        RectTransform panelRect = CreateRect("Panel", safeArea);
        panelRect.anchorMin = new Vector2(0f, 0f);
        panelRect.anchorMax = new Vector2(0f, 1f);
        panelRect.pivot = new Vector2(0f, 1f);
        panelRect.anchoredPosition = new Vector2(12f, -12f);
        panelRect.sizeDelta = new Vector2(panelWidth, -24f);
        panelRect.gameObject.AddComponent<Image>().color = PanelColor;
        AddVerticalLayout(panelRect.gameObject, new RectOffset(14, 14, 12, 14), 8f);
        panel = panelRect.gameObject;

        RectTransform header = CreateRow(panelRect, ButtonHeight);
        Text title = CreateText(header, "Orbit Simulator", 26, TextColor, FontStyle.Bold);
        AddLayoutElement(title.gameObject, flexibleWidth: 1f);
        CreateButton(header, "Hide", ButtonColor, () => SetPanelVisible(false), ButtonHeight, 96f);

        statusText = CreateText(panelRect, "", 18, MutedTextColor);
        statusText.gameObject.AddComponent<Canvas>(); // Updated while running, so isolate its rebuilds

        RectTransform controls = CreateRow(panelRect, ButtonHeight);
        startButton = CreateButton(controls, "Start", AccentColor, manager.StartSimulation, ButtonHeight);
        startLabel = startButton.GetComponentInChildren<Text>();
        pauseButton = CreateButton(controls, "Pause", ButtonColor, manager.PauseSimulation, ButtonHeight);
        resetButton = CreateButton(controls, "Reset", DangerColor, manager.ResetSimulation, ButtonHeight);

        RectTransform planetRow = CreateRow(panelRect, ButtonHeight);
        CreateButton(planetRow, "+  Add Planet", ButtonColor, () => manager.AddPlanet(), ButtonHeight);
        CreateButton(planetRow, "Defaults", DangerColor, manager.RestoreDefaults, ButtonHeight, 120f);
        CreateText(panelRect, "Edits reset the simulation. Settings are saved automatically.", 16, MutedTextColor,
            FontStyle.Italic);

        cardContainer = CreateScrollList(panelRect);
        BuildFailBanner();

        // Shown in place of the panel while it is hidden
        Button show = CreateButton(safeArea, "Controls", ButtonColor, () => SetPanelVisible(true), ButtonHeight);
        var showRect = (RectTransform)show.transform;
        showRect.anchorMin = showRect.anchorMax = showRect.pivot = new Vector2(0f, 1f);
        showRect.anchoredPosition = new Vector2(12f, -12f);
        showRect.sizeDelta = new Vector2(160f, ButtonHeight);
        showButton = show.gameObject;
        showButton.SetActive(false);
    }

    private void BuildFailBanner()
    {
        // Centred over the free space to the right of the panel, on its own canvas so showing it is cheap
        failBanner = CreateRect("Fail Banner", safeArea);
        failBanner.anchorMin = new Vector2(0f, 1f);
        failBanner.anchorMax = new Vector2(1f, 1f);
        failBanner.pivot = new Vector2(0.5f, 1f);
        failBanner.offsetMin = new Vector2(panelWidth + 36f, -170f);
        failBanner.offsetMax = new Vector2(-24f, -24f);
        failBanner.gameObject.AddComponent<Canvas>();

        RectTransform box = CreateRect("Box", failBanner);
        box.anchorMin = box.anchorMax = box.pivot = new Vector2(0.5f, 1f);
        box.sizeDelta = new Vector2(520f, 146f);
        var background = box.gameObject.AddComponent<Image>();
        background.color = new Color(0.55f, 0.1f, 0.14f, 0.92f);
        background.raycastTarget = false; // Never blocks the camera or the planets
        AddVerticalLayout(box.gameObject, new RectOffset(20, 20, 16, 16), 4f);
        box.GetComponent<VerticalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;

        Text title = CreateText(box, "Simulation Failed", 40, TextColor, FontStyle.Bold);
        title.alignment = TextAnchor.MiddleCenter;
        failDetailText = CreateText(box, "", 22, TextColor);
        failDetailText.alignment = TextAnchor.MiddleCenter;
        Text hint = CreateText(box, "Resetting to the start so you can adjust and try again", 17,
            new Color(1f, 0.85f, 0.85f, 1f), FontStyle.Italic);
        hint.alignment = TextAnchor.MiddleCenter;

        failBanner.gameObject.SetActive(false);
    }

    private RectTransform CreateScrollList(RectTransform parent)
    {
        RectTransform scrollRect = CreateRect("Planet List", parent);
        AddLayoutElement(scrollRect.gameObject, flexibleHeight: 1f, minHeight: 120f);
        var scroll = scrollRect.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;

        RectTransform viewport = CreateRect("Viewport", scrollRect);
        Stretch(viewport);
        viewport.gameObject.AddComponent<Image>().color = Color.clear; // Catches drags between cards
        viewport.gameObject.AddComponent<RectMask2D>();

        RectTransform content = CreateRect("Content", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = Vector2.zero;
        AddVerticalLayout(content.gameObject, new RectOffset(0, 0, 0, 0), 8f);
        content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.viewport = viewport;
        scroll.content = content;
        return content;
    }

    #endregion

    #region Planet Cards

    private void RebuildCards()
    {
        for (int i = cardContainer.childCount - 1; i >= 0; i--)
        {
            Destroy(cardContainer.GetChild(i).gameObject);
        }
        orbiterCards.Clear();
        centralCard = null;

        if (manager.Central != null) centralCard = BuildCentralCard(manager.Central);
        foreach (SimulationManager.PlanetSetup setup in manager.Orbiters)
        {
            orbiterCards.Add(BuildOrbiterCard(setup));
        }

        RefreshInfo();
        UpdateStatus();
    }

    private PlanetCard BuildCentralCard(SimulationManager.PlanetSetup setup)
    {
        var card = new PlanetCard { setup = setup };
        RectTransform root = CreateCardRoot(setup, "Center", out _);

        card.infoText = CreateText(root, "", 17, MutedTextColor);
        CreateSliderRow(root, "Size", centralSizeRange.x, centralSizeRange.y, setup.size, "0.00", v =>
        {
            manager.SetSize(setup, v);
            RefreshInfo(); // Central mass changes every planet's circular speed
        });
        return card;
    }

    private PlanetCard BuildOrbiterCard(SimulationManager.PlanetSetup setup)
    {
        var card = new PlanetCard { setup = setup };
        RectTransform root = CreateCardRoot(setup, null, out RectTransform header);
        CreateButton(header, "Remove", DangerColor, () => manager.RemovePlanet(setup), RowHeight, 104f);

        card.infoText = CreateText(root, "", 17, MutedTextColor);
        CreateSliderRow(root, "Size", sizeRange.x, sizeRange.y, setup.size, "0.00", v =>
        {
            manager.SetSize(setup, v);
            UpdateInfo(card);
        });
        CreateSliderRow(root, "Distance", distanceRange.x, distanceRange.y, setup.distance, "0.0", v =>
        {
            manager.SetDistance(setup, v);
            UpdateInfo(card);
        });
        card.speedRow = CreateSliderRow(root, "Speed", 0f, maxSpeed, setup.speed, "0.00",
            v => manager.SetSpeed(setup, v));
        CreateSliderRow(root, "Tilt", -maxTilt, maxTilt, setup.tilt, "0°", v => manager.SetTilt(setup, v));

        CreateButton(root, "Use Circular Orbit Speed", ButtonColor, () =>
        {
            float speed = manager.CircularSpeed(setup);
            manager.SetSpeed(setup, speed);
            card.speedRow.SetValueWithoutNotify(speed);
        }, RowHeight);
        return card;
    }

    private RectTransform CreateCardRoot(SimulationManager.PlanetSetup setup, string tag, out RectTransform header)
    {
        RectTransform root = CreateRect(setup.planet.name + " Card", cardContainer);
        root.gameObject.AddComponent<Image>().color = CardColor;
        AddVerticalLayout(root.gameObject, new RectOffset(12, 12, 8, 12), 4f);

        header = CreateRow(root, RowHeight);
        RectTransform swatch = CreateRect("Swatch", header);
        swatch.gameObject.AddComponent<Image>().color = PlanetColor(setup.planet);
        AddLayoutElement(swatch.gameObject, preferredWidth: 20f, preferredHeight: 20f);

        string title = tag != null ? $"{setup.planet.name}  <size=16><color=#A8B3C7>{tag}</color></size>" : setup.planet.name;
        Text nameText = CreateText(header, title, 22, TextColor, FontStyle.Bold);
        nameText.supportRichText = true;
        AddLayoutElement(nameText.gameObject, flexibleWidth: 1f);
        return root;
    }

    private void RefreshInfo()
    {
        if (centralCard != null) UpdateInfo(centralCard);
        foreach (PlanetCard card in orbiterCards) UpdateInfo(card);
    }

    private void UpdateInfo(PlanetCard card)
    {
        float mass = manager.GetMass(card.setup);
        card.infoText.text = card.setup == manager.Central
            ? $"Mass {mass:0.##}   |   fixed at the origin"
            : $"Mass {mass:0.##}   |   circular speed {manager.CircularSpeed(card.setup):0.00}";
    }

    private static Color PlanetColor(Planet planet)
    {
        Renderer renderer = planet.GetComponent<Renderer>();
        return renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.color : Color.white;
    }

    #endregion

    #region Widget Helpers

    private SliderRow CreateSliderRow(RectTransform parent, string label, float min, float max, float value,
        string format, UnityAction<float> onChanged)
    {
        RectTransform row = CreateRow(parent, RowHeight);

        Text labelText = CreateText(row, label, 19, TextColor);
        AddLayoutElement(labelText.gameObject, preferredWidth: 84f);

        GameObject sliderObject = DefaultControls.CreateSlider(new DefaultControls.Resources());
        sliderObject.transform.SetParent(row, false);
        AddLayoutElement(sliderObject, flexibleWidth: 1f, preferredHeight: RowHeight);

        var slider = sliderObject.GetComponent<Slider>();
        slider.minValue = Mathf.Min(min, value);
        slider.maxValue = Mathf.Max(max, value);
        StyleSlider(slider);

        Text valueText = CreateText(row, "", 19, TextColor);
        valueText.alignment = TextAnchor.MiddleRight;
        AddLayoutElement(valueText.gameObject, preferredWidth: 60f);

        var sliderRow = new SliderRow { slider = slider, valueText = valueText, format = format };
        sliderRow.SetValueWithoutNotify(value);
        slider.onValueChanged.AddListener(v =>
        {
            valueText.text = v.ToString(format);
            onChanged(v);
        });
        return sliderRow;
    }

    private static void StyleSlider(Slider slider)
    {
        // The whole row height stays touchable; only the visuals are slimmed down
        Transform background = slider.transform.Find("Background");
        if (background != null)
        {
            var backgroundRect = (RectTransform)background;
            backgroundRect.anchorMin = new Vector2(0f, 0.4f);
            backgroundRect.anchorMax = new Vector2(1f, 0.6f);
            background.GetComponent<Image>().color = TrackColor;
        }
        if (slider.fillRect != null)
        {
            var fillArea = (RectTransform)slider.fillRect.parent;
            fillArea.anchorMin = new Vector2(0f, 0.4f);
            fillArea.anchorMax = new Vector2(1f, 0.6f);
            slider.fillRect.GetComponent<Image>().color = ButtonColor;
        }
        if (slider.handleRect != null)
        {
            var handleArea = (RectTransform)slider.handleRect.parent;
            handleArea.anchorMin = new Vector2(0f, 0.2f);
            handleArea.anchorMax = new Vector2(1f, 0.8f);
            slider.handleRect.sizeDelta = new Vector2(26f, 0f);
            slider.handleRect.GetComponent<Image>().color = TextColor;
        }
    }

    private Button CreateButton(Transform parent, string label, Color color, UnityAction onClick,
        float height, float width = -1f)
    {
        RectTransform rect = CreateRect(label + " Button", parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);

        if (width > 0f) AddLayoutElement(rect.gameObject, preferredWidth: width, preferredHeight: height);
        else AddLayoutElement(rect.gameObject, flexibleWidth: 1f, preferredHeight: height);

        Text text = CreateText(rect, label, 20, TextColor, FontStyle.Bold);
        text.alignment = TextAnchor.MiddleCenter;
        Stretch(text.rectTransform);
        return button;
    }

    private Text CreateText(Transform parent, string content, int fontSize, Color color,
        FontStyle style = FontStyle.Normal)
    {
        RectTransform rect = CreateRect("Text", parent);
        var text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.text = content;
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.color = color;
        text.alignment = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.supportRichText = false;
        text.raycastTarget = false;
        return text;
    }

    private static RectTransform CreateRow(Transform parent, float height)
    {
        RectTransform row = CreateRect("Row", parent);
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        AddLayoutElement(row.gameObject, minHeight: height);
        return row;
    }

    private static void AddVerticalLayout(GameObject target, RectOffset padding, float spacing)
    {
        var layout = target.AddComponent<VerticalLayoutGroup>();
        layout.padding = padding;
        layout.spacing = spacing;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
    }

    private static void AddLayoutElement(GameObject target, float flexibleWidth = -1f, float flexibleHeight = -1f,
        float preferredWidth = -1f, float preferredHeight = -1f, float minHeight = -1f)
    {
        var element = target.GetComponent<LayoutElement>();
        if (element == null) element = target.AddComponent<LayoutElement>();
        element.flexibleWidth = flexibleWidth;
        element.flexibleHeight = flexibleHeight;
        element.preferredWidth = preferredWidth;
        element.preferredHeight = preferredHeight;
        element.minHeight = minHeight;
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    #endregion
}
