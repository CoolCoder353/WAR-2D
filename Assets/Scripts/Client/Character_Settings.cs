using NaughtyAttributes;
using UnityEngine;

namespace Character
{
    [CreateAssetMenu]
    public class Character_Settings : ScriptableObject
    {
        [Foldout("Pan"), Tooltip("World units per second at orthographic size 5.")]
        public float speed = 5f;
        [Foldout("Pan")]
        public float shiftSpeedMultiplyer = 2.5f;

        [Foldout("Zoom"), Tooltip("Orthographic size change per mouse-wheel notch.")]
        public float zoomStep = 1.5f;
        [Foldout("Zoom"), Tooltip("Min and max orthographic size.")]
        public Vector2 zoomScale = new Vector2(3f, 30f);
    }
}
