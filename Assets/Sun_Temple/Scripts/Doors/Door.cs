using System.Collections;
using UnityEngine;

public class SimpleDoorInteract : MonoBehaviour
{
    public string playerTag = "Player";
    public float openRadius = 2f;
    public float openAngleY = 90f;
    public float rotationSpeed = 4f;
    public float closeAfterSeconds = 3f;

    private Transform player;
    private Quaternion closedRot;
    private Quaternion openRot;
    private bool isOpen = false;

    void Start()
    {
        // Door ki shuru wali aur khuli hui rotation save karein
        closedRot = transform.localRotation;
        openRot = closedRot * Quaternion.Euler(0f, openAngleY, 0f);

        // Player tag ("Player") se player dhoondein
        GameObject playerObj = GameObject.FindGameObjectWithTag(playerTag);
        if (playerObj != null)
        {
            player = playerObj.transform;
        }
    }

    void Update()
    {
        if (player == null || isOpen) return;

        // Player aur door ke darmiyan faasla check karein
        float distance = Vector3.Distance(player.position, transform.position);

        if (distance <= openRadius)
        {
            StartCoroutine(OpenAndCloseDoor());
        }
    }

    IEnumerator OpenAndCloseDoor()
    {
        isOpen = true;

        // 1. Door ko smoothly open karein
        while (Quaternion.Angle(transform.localRotation, openRot) > 0.5f)
        {
            transform.localRotation = Quaternion.Slerp(
                transform.localRotation,
                openRot,
                Time.deltaTime * rotationSpeed
            );
            yield return null;
        }
        transform.localRotation = openRot;

        // 2. 3 seconds rukien
        yield return new WaitForSeconds(closeAfterSeconds);

        // 3. Door ko smoothly close karein
        while (Quaternion.Angle(transform.localRotation, closedRot) > 0.5f)
        {
            transform.localRotation = Quaternion.Slerp(
                transform.localRotation,
                closedRot,
                Time.deltaTime * rotationSpeed
            );
            yield return null;
        }
        transform.localRotation = closedRot;

        isOpen = false;
    }

    // Scene view mein 2f ka radius circle/sphere dikhane ke liye
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, openRadius);
    }
}