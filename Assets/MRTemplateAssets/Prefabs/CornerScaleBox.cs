using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Puts 8 pinchable corner handles on a box. Grab a corner and drag: the opposite
/// corner stays fixed and the box stretches to follow your hand.
///
/// Setup:
///   Root (this script, scale 1,1,1)
///     +-- Box  (your cube mesh, localPosition 0, localScale = size)
///     +-- Water, Glass, Sand, ...  (optional: drag these into "followers")
///
/// Anything that is a CHILD of Box scales automatically (Unity does that itself).
/// Anything that is a SIBLING of Box (a child of Root) goes into "followers" and
/// is scaled and repositioned in proportion to the box.
///
/// Audio: assign a short, loopable clip to "growSound". It plays while the box is
/// getting bigger, fades out when you stop or shrink it, and rises in pitch as the
/// box grows.
/// </summary>
public class CornerScaleBox : MonoBehaviour
{
    [Tooltip("The visual cube. Must be a child of this object.")]
    public Transform box;

    [Tooltip("Other objects (children of this root, NOT children of Box) that should " +
             "stretch together with the box.")]
    public Transform[] followers;

    public float handleSize = 0.04f;
    public float minSize = 0.05f;

    [Tooltip("Keep the cube's proportions (scales all axes equally).")]
    public bool uniformScale = false;

    public enum HandleVisibility { Always, OnHover, Hidden }

    [Header("Handle appearance")]
    [Tooltip("Always = dots always visible. OnHover = dots only appear when a hand or ray is " +
             "near them. Hidden = never drawn (they can still be pinched).")]
    public HandleVisibility handleVisibility = HandleVisibility.OnHover;

    [Tooltip("Material for the dots. Use a URP material (e.g. Universal Render Pipeline/Unlit). " +
             "Leaving this empty gives pink dots in URP.")]
    public Material handleMaterial;

    [Header("Audio feedback")]
    [Tooltip("Short loopable sound that plays while the box is growing.")]
    public AudioClip growSound;
    [Range(0f, 1f)] public float maxVolume = 0.6f;
    [Tooltip("How fast the sound fades in and out (volume per second).")]
    public float fadeSpeed = 6f;
    [Tooltip("Growth speed (total meters per second across x+y+z) that gives full volume.")]
    public float growSpeedForFullVolume = 0.5f;
    [Tooltip("Ignore tiny hand jitter below this growth speed.")]
    public float growThreshold = 0.02f;
    public bool pitchRisesWithSize = true;
    public float minPitch = 0.8f;
    public float maxPitch = 1.4f;
    [Tooltip("Box size (largest side, in meters) at which pitch reaches its maximum.")]
    public float sizeForMaxPitch = 2f;

    readonly Transform[] handles = new Transform[8];
    int heldIndex = -1;
    Vector3 anchorLocal; // fixed opposite corner while dragging (root local space)

    // starting state, used to scale followers proportionally
    Vector3 startBoxSize;
    Vector3[] followerStartScale;
    Vector3[] followerStartOffset; // follower position relative to box center

    // audio state
    AudioSource audioSource;
    float currentVolume;
    Vector3 lastSize;

    static Vector3 Sign(int i) => new Vector3(
        (i & 1) == 0 ? -1f : 1f,
        (i & 2) == 0 ? -1f : 1f,
        (i & 4) == 0 ? -1f : 1f);

    void Start()
    {
        RecordFollowers();
        SetupAudio();
        for (int i = 0; i < 8; i++)
            handles[i] = CreateHandle(i);
        PositionHandles();
    }

    void SetupAudio()
    {
        if (growSound == null) return;

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.clip = growSound;
        audioSource.loop = true;
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 1f; // 3D sound, comes from the box
        audioSource.volume = 0f;
    }

    void RecordFollowers()
    {
        startBoxSize = box.localScale;
        int n = followers != null ? followers.Length : 0;
        followerStartScale = new Vector3[n];
        followerStartOffset = new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            var f = followers[i];
            if (f == null) continue;

            if (f.IsChildOf(box))
            {
                Debug.LogWarning($"{f.name} is a child of the box, so it already scales " +
                                 "with it. Remove it from Followers to avoid double scaling.", f);
                followers[i] = null;
                continue;
            }

            followerStartScale[i] = f.localScale;
            followerStartOffset[i] = f.localPosition - box.localPosition;
        }
    }

    Transform CreateHandle(int index)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "CornerHandle_" + index;
        go.transform.SetParent(transform, false);
        go.transform.localScale = Vector3.one * handleSize;

        // Look of the dot. The collider stays active even when the renderer is off,
        // so hidden handles can still be pinched.
        var rend = go.GetComponent<Renderer>();
        if (handleMaterial != null) rend.sharedMaterial = handleMaterial;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;
        rend.enabled = handleVisibility == HandleVisibility.Always;

        var rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        var grab = go.AddComponent<XRGrabInteractable>();
        grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
        grab.trackRotation = false;
        grab.throwOnDetach = false;
        grab.useDynamicAttach = true; // handle stays where your pinch grabbed it

        int captured = index;
        grab.selectEntered.AddListener(_ => BeginDrag(captured));
        grab.selectExited.AddListener(_ => EndDrag(captured));

        if (handleVisibility == HandleVisibility.OnHover)
        {
            // show the dot only while a hand/ray is near it or holding it
            grab.hoverEntered.AddListener(_ => rend.enabled = true);
            grab.hoverExited.AddListener(_ => rend.enabled = grab.isSelected);
            grab.selectEntered.AddListener(_ => rend.enabled = true);
            grab.selectExited.AddListener(_ => rend.enabled = grab.isHovered);
        }
        return go.transform;
    }

    void BeginDrag(int index)
    {
        heldIndex = index;
        lastSize = box.localScale;
        // opposite corner = same box, mirrored signs
        anchorLocal = box.localPosition - Vector3.Scale(Sign(index), box.localScale * 0.5f);
    }

    void EndDrag(int index)
    {
        if (heldIndex == index) heldIndex = -1;
        PositionHandles(); // snap handle back onto its corner
    }

    void Update()
    {
        float targetVolume = 0f;

        if (heldIndex >= 0)
        {
            Vector3 s = Sign(heldIndex);
            Vector3 held = transform.InverseTransformPoint(handles[heldIndex].position);
            Vector3 delta = held - anchorLocal;

            Vector3 size = new Vector3(
                Mathf.Max(minSize, delta.x * s.x),
                Mathf.Max(minSize, delta.y * s.y),
                Mathf.Max(minSize, delta.z * s.z));

            if (uniformScale)
            {
                float u = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                size = Vector3.one * u;
            }

            box.localScale = size;
            box.localPosition = anchorLocal + Vector3.Scale(s, size * 0.5f);

            ApplyToFollowers(size);
            PositionHandles();

            // how fast is the box growing right now? (positive = growing)
            float growth = ((size.x + size.y + size.z) -
                            (lastSize.x + lastSize.y + lastSize.z)) / Mathf.Max(Time.deltaTime, 0.0001f);
            lastSize = size;

            if (growth > growThreshold)
                targetVolume = Mathf.Clamp01(growth / growSpeedForFullVolume) * maxVolume;

            if (audioSource != null && pitchRisesWithSize)
            {
                float biggest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                audioSource.pitch = Mathf.Lerp(minPitch, maxPitch,
                    Mathf.Clamp01(biggest / Mathf.Max(sizeForMaxPitch, 0.01f)));
            }
        }

        UpdateAudio(targetVolume);
    }

    void UpdateAudio(float targetVolume)
    {
        if (audioSource == null) return;

        currentVolume = Mathf.MoveTowards(currentVolume, targetVolume, fadeSpeed * Time.deltaTime);
        audioSource.volume = currentVolume;

        if (currentVolume > 0.001f && !audioSource.isPlaying)
            audioSource.Play();
        else if (currentVolume <= 0.001f && audioSource.isPlaying)
            audioSource.Pause();
    }

    void ApplyToFollowers(Vector3 size)
    {
        if (followers == null) return;

        // per-axis ratio of current size to starting size
        Vector3 ratio = new Vector3(
            startBoxSize.x != 0f ? size.x / startBoxSize.x : 1f,
            startBoxSize.y != 0f ? size.y / startBoxSize.y : 1f,
            startBoxSize.z != 0f ? size.z / startBoxSize.z : 1f);

        for (int i = 0; i < followers.Length; i++)
        {
            var f = followers[i];
            if (f == null) continue;

            f.localScale = Vector3.Scale(followerStartScale[i], ratio);
            f.localPosition = box.localPosition + Vector3.Scale(followerStartOffset[i], ratio);
        }
    }

    void PositionHandles()
    {
        for (int i = 0; i < 8; i++)
        {
            if (i == heldIndex) continue; // don't fight the hand
            handles[i].localPosition =
                box.localPosition + Vector3.Scale(Sign(i), box.localScale * 0.5f);
        }
    }
}