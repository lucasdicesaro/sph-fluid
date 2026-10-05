using UnityEngine;
using UnityEngine.InputSystem;

// Game-view camera navigation for the 3D scene: left-drag orbits around the target, scroll zooms.
public class OrbitCamera : MonoBehaviour
{
    public Vector3 target = new Vector3(0f, -1f, 0f);
    public float orbitSpeed = 0.25f;   // degrees per pixel
    public float zoomSpeed = 0.02f;    // distance fraction per scroll unit
    public float minDistance = 2f;
    public float maxDistance = 100f;

    private float _yaw, _pitch, _distance;

    void Start()
    {
        // Start from wherever the camera was placed in the scene
        Vector3 offset = transform.position - target;
        _distance = offset.magnitude;
        _yaw   = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
        _pitch = Mathf.Asin(offset.y / _distance) * Mathf.Rad2Deg;
    }

    void LateUpdate()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        if (mouse.leftButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue();
            _yaw   += delta.x * orbitSpeed;
            _pitch  = Mathf.Clamp(_pitch - delta.y * orbitSpeed, -89f, 89f);
        }
        float scroll = mouse.scroll.ReadValue().y;
        if (scroll != 0f) _distance = Mathf.Clamp(_distance * (1f - scroll * zoomSpeed), minDistance, maxDistance);

        Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.position = target + rot * new Vector3(0f, 0f, -_distance);
        transform.rotation = rot;
    }
}
