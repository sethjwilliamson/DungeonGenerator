using UnityEngine;

public class CameraHolder : MonoBehaviour
{
    public float speed = 10.0f;
    public float fastSpeed = 50.0f;
    public float sensitivity = 2.0f;
    public float maxVerticalAngle = 80.0f;

    private float currentSpeed;
    private float verticalRotation = 0.0f;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        // Unlock cursor on Esc
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (Cursor.lockState == CursorLockMode.None && Input.GetMouseButtonDown(0))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // Movement speed
        currentSpeed = Input.GetKey(KeyCode.LeftShift) ? fastSpeed : speed;

        // Movement directions relative to camera
        Vector3 direction = transform.right * Input.GetAxis("Horizontal") + transform.forward * Input.GetAxis("Vertical");
        direction.y = 0; // Ignore vertical movement from forward direction
        if (Input.GetKey(KeyCode.Space))
        {
            direction.y += 1;
        }
        if (Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand))
        {
            direction.y -= 1;
        }

        // Apply movement
        transform.Translate(direction * currentSpeed * Time.deltaTime, Space.World);

        // Mouse look
        float mouseX = Input.GetAxis("Mouse X") * sensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * sensitivity;

        // Horizontal rotation
        transform.Rotate(Vector3.up, mouseX, Space.World);

        // Vertical rotation with clamping
        verticalRotation -= mouseY;
        verticalRotation = Mathf.Clamp(verticalRotation, -maxVerticalAngle, maxVerticalAngle);
        transform.localEulerAngles = new Vector3(verticalRotation, transform.localEulerAngles.y, 0);
    }
}