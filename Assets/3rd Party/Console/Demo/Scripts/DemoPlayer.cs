using NaughtyAttributes;
using UnityEngine;

namespace Demo
{
    public class DemoPlayer : MonoBehaviour
    {
        [BoxGroup("Params")] public float Speed = 3;

        [BoxGroup("Links")][SerializeField] private Rigidbody2D _rigidbody;
        [SerializeField] private SpriteRenderer _spriteRenderer;

        public static DemoPlayer Instance { private set; get; }

        private void Awake()
        {
            Instance = this;
        }

        private void FixedUpdate()
        {
            float y = _rigidbody.linearVelocity.y;
            _rigidbody.linearVelocity = new Vector2(Input.GetAxis("Horizontal") * Speed, y);
        }

        public void ChangeColor(int colorIndex)
        {
            Color color;
            if (colorIndex == 0)
                color = Color.white;
            else if (colorIndex == 1)
                color = Color.red;
            else if (colorIndex == 2)
                color = Color.green;
            else if (colorIndex == 3)
                color = Color.blue;
            else if (colorIndex == 4)
                color = Color.yellow;
            else
                color = Color.white;

            _spriteRenderer.color = color;
            print("Color successfully changed!");
        }
    }
}