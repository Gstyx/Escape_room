using UnityEngine;
using UnityEngine.InputSystem;

namespace EscapeRoom
{
    /// <summary>First-person camera rig.
    ///
    /// The Starter Assets <c>FirstPersonController</c> splits look across two objects: yaw goes
    /// to the capsule (<c>transform.Rotate</c>) and pitch is written to whatever
    /// <c>CinemachineCameraTarget</c> happens to point at. Wiring a camera to that second object is
    /// fragile - the prefab already ships its own pivot (<c>PlayerCameraRoot</c>) with a baked
    /// 88.9 deg X rotation, so a rig pointed at a different pivot silently loses pitch while yaw
    /// keeps working. That is exactly the bug this replaces.
    ///
    /// So the rig owns pitch itself and only reads yaw from the body. It never reads
    /// <c>CinemachineCameraTarget</c>, which means it cannot desync from whatever the controller
    /// happens to be driving. The controller's field is still pointed at a valid object so its
    /// line 147 cannot throw (see DECISOES.md D-58 / D-59).
    /// </summary>
    public class EsFirstPersonCameraRig : MonoBehaviour
    {
        public Transform body;            // the PlayerCapsule
        public float eyeHeight = 1.375f;
        public float sensitivity = 0.12f;
        public float minPitch = -85f;
        public float maxPitch = 85f;
        public bool invertY = false;

        float _pitch;
        Camera _cam;

        public float Pitch => _pitch;

        void Start()
        {
            _cam = GetComponent<Camera>();
            if (_cam == null) _cam = Camera.main;
        }

        void LateUpdate()
        {
            var m = Mouse.current;
            if (m != null)
            {
                float dy = m.delta.ReadValue().y;
                if (Mathf.Abs(dy) > 0.0001f)
                    _pitch = Mathf.Clamp(_pitch + (invertY ? dy : -dy) * sensitivity, minPitch, maxPitch);
            }

            Apply();
        }

        void Apply()
        {
            if (body == null) return;
            transform.position = body.position + Vector3.up * eyeHeight;
            transform.rotation = Quaternion.Euler(0f, body.eulerAngles.y, 0f) * Quaternion.Euler(_pitch, 0f, 0f);
        }

        /// <summary>Aims the rig at explicit angles, owning both the body yaw and the pitch.
        /// Exists so a caller can aim the camera and have the result be immediately true, rather
        /// than waiting for the next LateUpdate - <c>EditorApplication.delayCall</c> was tried for
        /// this and does not fire while the editor window is unfocused. Keeping both angles owned
        /// here also means the rig stays coherent with whatever it would have computed itself.</summary>
        public void SetLook(float yaw, float pitch)
        {
            if (body != null) body.rotation = Quaternion.Euler(0f, yaw, 0f);
            _pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
            Apply();
        }

        /// <summary>Yaw/pitch that would put the crosshair on <paramref name="worldPoint"/>.</summary>
        public void LookAtPoint(Vector3 worldPoint, out float yaw, out float pitch)
        {
            Vector3 look = worldPoint - (body.position + Vector3.up * eyeHeight);
            Vector3 fwd = look.sqrMagnitude < 0.000001f ? Vector3.forward : look.normalized;
            yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            pitch = -Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
        }
    }
}
