using UnityEngine;
using UnityEngine.Video;

public class VideoPlayerController : MonoBehaviour
{
    public VideoClip videoClip;
    public VideoPlayer videoPlayer;
    public RenderTexture videoRenderTexture;
    public Material targetAlphaMaterial;
    public GameObject videoDisplayPlaneObject;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    void OnValidate()
    {
        // Assign the VideoClip to the VideoPlayer
        if (videoPlayer != null && videoClip != null)
        {
            videoPlayer.clip = videoClip;
        }

        // Find the VideoPlayer component if not assigned
        if (videoPlayer == null)
        {
            videoPlayer = GetComponent<VideoPlayer>();
        }

        // Assign the RenderTexture to the VideoPlayer and set rendering mode to RenderTexture, or create a new one if not set.
        if (videoPlayer != null)
        {
            if (videoRenderTexture == null)
            {
                // Create a new RenderTexture with video clip dimensions if available, otherwise default to 1920x1080
                if (videoClip != null)
                {
                    videoRenderTexture = new RenderTexture((int)videoClip.width, (int)videoClip.height, 0);
                }
                else
                {
                    videoRenderTexture = new RenderTexture(1920, 1080, 0);
                }
            }
            videoPlayer.targetTexture = videoRenderTexture;
            videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        }

        // Assign material to the display plane, and set the main texture to the RenderTexture
        if (videoDisplayPlaneObject != null && targetAlphaMaterial != null && videoRenderTexture != null)
        {
            MeshRenderer meshRenderer = videoDisplayPlaneObject.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                meshRenderer.material = targetAlphaMaterial;
                meshRenderer.material.mainTexture = videoRenderTexture;
            }
        }
    }
}
