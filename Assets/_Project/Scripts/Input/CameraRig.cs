using UnityEngine;

namespace RTS.Input
{
    /// <summary>
    /// Pivot (on the ground plane) → Boom (pitch/yaw) → Camera. Pan keeps the ground point under
    /// the finger, zoom keeps the point under the pinch centre, and the visible rectangle is
    /// rubber-banded to the map bounds. Pure presentation: never touches the simulation.
    /// See docs/03-CONTROLS-CAMERA.md §2.
    /// </summary>
    public sealed class CameraRig : MonoBehaviour
    {
        [Header("Rig")]
        [SerializeField] private Camera cam;
        [SerializeField] private Transform boom;
        [SerializeField] private float yaw = 45f;
        [SerializeField] private float minDistance = 12f;
        [SerializeField] private float maxDistance = 40f;
        [SerializeField] private float pitchNear = 45f;
        [SerializeField] private float pitchFar = 58f;

        [Header("Feel")]
        [SerializeField] private float panDamping = 6f;
        [SerializeField] private float zoomSmoothing = 12f;
        [SerializeField] private float boundsMargin = 4f;
        [SerializeField] private float rubberBand = 0.4f;

        private Rect _mapBounds = new Rect(0, 0, 64, 64);
        private float _distance = 24f;
        private float _targetDistance = 24f;
        private Vector3 _velocity;
        private bool _fingerDown;
        private Vector3 _centerFrom, _centerTo;
        private float _centerT = 1f, _centerDuration;

        public Camera Camera => cam;
        public float Distance => _distance;

        public void SetMapBounds(float width, float height)
        {
            _mapBounds = new Rect(0f, 0f, width, height);
        }

        private void Reset()
        {
            cam = GetComponentInChildren<Camera>();
        }

        private void Awake()
        {
            if (cam == null) cam = GetComponentInChildren<Camera>();
            if (boom == null && cam != null) boom = cam.transform.parent;
            ApplyRig(true);
        }

        private void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;

            // Inertia after a pan.
            if (!_fingerDown && _velocity.sqrMagnitude > 0.0001f)
            {
                transform.position += _velocity * dt;
                _velocity *= Mathf.Max(0f, 1f - panDamping * dt);
            }

            // Eased centre-on.
            if (_centerT < 1f)
            {
                _centerT = Mathf.Min(1f, _centerT + dt / Mathf.Max(0.01f, _centerDuration));
                float t = 1f - Mathf.Pow(1f - _centerT, 3f);   // easeOutCubic
                transform.position = Vector3.Lerp(_centerFrom, _centerTo, t);
            }

            _distance = Mathf.Lerp(_distance, _targetDistance, 1f - Mathf.Exp(-zoomSmoothing * dt));
            ApplyRig(false);
            ClampToBounds(_fingerDown ? rubberBand : 1f);
        }

        private void ApplyRig(bool immediate)
        {
            if (immediate) _distance = _targetDistance;
            float t = Mathf.InverseLerp(minDistance, maxDistance, _distance);
            float pitch = Mathf.Lerp(pitchNear, pitchFar, t);
            boom.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            cam.transform.localPosition = new Vector3(0f, 0f, -_distance);
            cam.transform.localRotation = Quaternion.identity;
        }

        // ---- gestures → camera -------------------------------------------------------

        public void BeginPan()
        {
            _fingerDown = true;
            _velocity = Vector3.zero;
            _centerT = 1f;
        }

        /// <summary>Moves the pivot so the ground point that was under <paramref name="fromScreen"/> is now under <paramref name="toScreen"/>.</summary>
        public void Pan(Vector2 fromScreen, Vector2 toScreen, float dt)
        {
            if (!ScreenToGround(fromScreen, out Vector3 a) || !ScreenToGround(toScreen, out Vector3 b)) return;
            Vector3 delta = a - b;
            delta.y = 0f;
            transform.position += delta;
            if (dt > 0f) _velocity = Vector3.Lerp(_velocity, delta / dt, 0.5f);
        }

        public void EndPan()
        {
            _fingerDown = false;
        }

        /// <summary>Zooms by a ratio (pinch spread) keeping the ground point under the pinch centre fixed.</summary>
        public void Zoom(float ratio, Vector2 screenCenter)
        {
            if (ratio <= 0f) return;
            bool hadPoint = ScreenToGround(screenCenter, out Vector3 before);
            _targetDistance = Mathf.Clamp(_targetDistance / ratio, minDistance, maxDistance);
            _distance = _targetDistance;
            ApplyRig(true);
            if (hadPoint && ScreenToGround(screenCenter, out Vector3 after))
            {
                Vector3 delta = before - after;
                delta.y = 0f;
                transform.position += delta;
            }
        }

        /// <summary>Mouse wheel / buttons: multiplicative step.</summary>
        public void ZoomStep(float steps, Vector2 screenCenter) => Zoom(Mathf.Pow(1.15f, steps), screenCenter);

        public void CenterOn(Vector3 worldPoint, float seconds = 0.25f)
        {
            _centerFrom = transform.position;
            _centerTo = new Vector3(worldPoint.x, 0f, worldPoint.z);
            _centerDuration = seconds;
            _centerT = seconds <= 0f ? 1f : 0f;
            if (seconds <= 0f) transform.position = _centerTo;
            _velocity = Vector3.zero;
        }

        // ---- helpers -------------------------------------------------------------------

        /// <summary>Ray from a screen point to the y = 0 ground plane.</summary>
        public bool ScreenToGround(Vector2 screen, out Vector3 point)
        {
            Ray ray = cam.ScreenPointToRay(screen);
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(ray, out float enter))
            {
                point = ray.GetPoint(enter);
                return true;
            }
            point = Vector3.zero;
            return false;
        }

        private void ClampToBounds(float strength)
        {
            // Project the 4 frustum corners; if the visible rect leaves the inflated map, pull the pivot back.
            Rect bounds = new Rect(_mapBounds.xMin - boundsMargin, _mapBounds.yMin - boundsMargin,
                                   _mapBounds.width + 2f * boundsMargin, _mapBounds.height + 2f * boundsMargin);
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            Vector2[] corners = { new Vector2(0, 0), new Vector2(Screen.width, 0), new Vector2(0, Screen.height), new Vector2(Screen.width, Screen.height) };
            foreach (Vector2 c in corners)
            {
                if (!ScreenToGround(c, out Vector3 p)) continue;
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            }
            if (minX == float.MaxValue) return;

            Vector3 shift = Vector3.zero;
            float visW = maxX - minX, visH = maxZ - minZ;
            if (visW >= bounds.width) shift.x = bounds.center.x - (minX + maxX) * 0.5f;
            else if (minX < bounds.xMin) shift.x = bounds.xMin - minX;
            else if (maxX > bounds.xMax) shift.x = bounds.xMax - maxX;
            if (visH >= bounds.height) shift.z = bounds.center.y - (minZ + maxZ) * 0.5f;
            else if (minZ < bounds.yMin) shift.z = bounds.yMin - minZ;
            else if (maxZ > bounds.yMax) shift.z = bounds.yMax - maxZ;

            if (shift.sqrMagnitude > 0f)
            {
                transform.position += shift * strength;
                if (strength >= 1f) _velocity = Vector3.zero;
            }
        }
    }
}
