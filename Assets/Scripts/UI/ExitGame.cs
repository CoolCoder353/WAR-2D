using UnityEngine;


public class ExitGame : MonoBehaviour
{
    public void Exit()
    {
        // Check if we are running in the Unity Editor
#if UNITY_EDITOR
        // If so, stop playing the scene
        UnityEditor.EditorApplication.isPlaying = false;
#else
        // If not, quit the application
        Application.Quit();
#endif
    }



    public void ReturnToMainMenu()
    {

        GameManager.Instance.LeaveLobby();
    }
}