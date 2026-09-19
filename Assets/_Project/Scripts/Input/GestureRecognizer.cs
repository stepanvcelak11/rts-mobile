using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace RTS.Input
{
    /// <summary>
    /// Turns raw touches (or the mouse in the editor) into the gestures from
    /// docs/03-CONTROLS-CAMERA.md §1: tap, long press, long-press drag, pan, pinch.
    /// Thresholds are in dp so they feel identical on every screen density.
    /// </summary>
    public sealed class GestureRecognizer : MonoBehaviour
    {
        [SerializeField] private float tapMaxMs = 200f;
        [SerializeField] private float tapMaxMoveDp = 12f;
        [SerializeField] private float longPressMs = 350f;
        [SerializeField] private float pinchStartDp = 20f;

        public event Action<Vector2> Tap;
        public event Action<Vector2> LongPress;
        public event Action<Vector2, Vector2> LongPressDrag;      // start, current
        public event Action<Vector2, Vector2> LongPressDragEnd;   // start, end
        public event Action<Vector2> PanBegin;
        public event Action<Vector2, Vector2, float> Pan;         // from, to, dt
        public event Action PanEnd;
        public event Action<float, Vector2> Pinch;                // ratio, centre
        public event Action<float, Vector2> Scroll;               // wheel steps, cursor (editor)

        private enum State { Idle, Touching, LongPressed, LongPressDragging, Panning, Pinching }

        private State _state;
        private Vector2 _startPos, _lastPos;
        private float _startTime;
        private float _pinchLastDistance;
        private float _dpToPx;

        /// <summary>UI can set this to swallow gestures that start over HUD elements.</summary>
        public Func<Vector2, bool> IsPointerOverUI;

        private void OnEnable()
        {
            EnhancedTouchSupport.Enable();
            _dpToPx = (Screen.dpi > 0f ? Screen.dpi : 160f) / 160f;
        }

        private void OnDisable()
        {
            EnhancedTouchSupport.Disable();
        }

        private void Update()
        {
            if (Touch.activeTouches.Count > 0) UpdateTouch();
            else UpdateMouse();
        }

        // ---- touch ----------------------------------------------------------------------

        private void UpdateTouch()
        {
            var touches = Touch.activeTouches;
            if (touches.Count >= 2)
            {
                Vector2 a = touches[0].screenPosition, b = touches[1].screenPosition;
                float dist = Vector2.Distance(a, b);
                Vector2 center = (a + b) * 0.5f;
                if (_state != State.Pinching)
                {
                    EndCurrent();
                    _state = State.Pinching;
                    _pinchLastDistance = dist;
                }
                else if (Mathf.Abs(dist - _pinchLastDistance) > pinchStartDp * _dpToPx * 0.1f && _pinchLastDistance > 1f)
                {
                    Pinch?.Invoke(dist / _pinchLastDistance, center);
                    _pinchLastDistance = dist;
                }
                return;
            }

            Touch t = touches[0];
            switch (t.phase)
            {
                case UnityEngine.InputSystem.TouchPhase.Began:
                    Begin(t.screenPosition);
                    break;
                case UnityEngine.InputSystem.TouchPhase.Moved:
                case UnityEngine.InputSystem.TouchPhase.Stationary:
                    Move(t.screenPosition);
                    break;
                case UnityEngine.InputSystem.TouchPhase.Ended:
                case UnityEngine.InputSystem.TouchPhase.Canceled:
                    End(t.screenPosition);
                    break;
            }
        }

        // ---- mouse (editor / desktop testing) ------------------------------------------------

        private void UpdateMouse()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;
            Vector2 pos = mouse.position.ReadValue();

            if (mouse.leftButton.wasPressedThisFrame) Begin(pos);
            else if (mouse.leftButton.isPressed) Move(pos);
            else if (mouse.leftButton.wasReleasedThisFrame) End(pos);

            float wheel = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.01f) Scroll?.Invoke(Mathf.Sign(wheel), pos);
        }

        // ---- shared state machine ---------------------------------------------------------

        private void Begin(Vector2 pos)
        {
            if (IsPointerOverUI != null && IsPointerOverUI(pos)) { _state = State.Idle; return; }
            _state = State.Touching;
            _startPos = _lastPos = pos;
            _startTime = Time.unscaledTime;
        }

        private void Move(Vector2 pos)
        {
            float moved = Vector2.Distance(pos, _startPos) / _dpToPx;
            float heldMs = (Time.unscaledTime - _startTime) * 1000f;

            switch (_state)
            {
                case State.Touching:
                    if (moved > tapMaxMoveDp)
                    {
                        _state = State.Panning;
                        PanBegin?.Invoke(_startPos);
                        Pan?.Invoke(_lastPos, pos, Time.unscaledDeltaTime);
                    }
                    else if (heldMs >= longPressMs)
                    {
                        _state = State.LongPressed;
                        LongPress?.Invoke(pos);
                    }
                    break;
                case State.LongPressed:
                    if (moved > tapMaxMoveDp)
                    {
                        _state = State.LongPressDragging;
                        LongPressDrag?.Invoke(_startPos, pos);
                    }
                    break;
                case State.LongPressDragging:
                    LongPressDrag?.Invoke(_startPos, pos);
                    break;
                case State.Panning:
                    if (pos != _lastPos) Pan?.Invoke(_lastPos, pos, Time.unscaledDeltaTime);
                    break;
            }
            _lastPos = pos;
        }

        private void End(Vector2 pos)
        {
            float heldMs = (Time.unscaledTime - _startTime) * 1000f;
            switch (_state)
            {
                case State.Touching:
                    if (heldMs <= tapMaxMs) Tap?.Invoke(pos);
                    else if (heldMs < longPressMs) Tap?.Invoke(pos);   // slow tap still counts
                    break;
                case State.LongPressDragging:
                    LongPressDragEnd?.Invoke(_startPos, pos);
                    break;
                case State.Panning:
                    PanEnd?.Invoke();
                    break;
            }
            _state = State.Idle;
        }

        private void EndCurrent()
        {
            if (_state == State.Panning) PanEnd?.Invoke();
            else if (_state == State.LongPressDragging) LongPressDragEnd?.Invoke(_startPos, _lastPos);
            _state = State.Idle;
        }
    }
}
