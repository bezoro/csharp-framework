// Compile-time surface only. This assembly is never shipped with the Unity package.
using System;

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    public abstract class MonoBehaviour
    {
        public GameObject gameObject { get; } = new GameObject();
    }

    public sealed class GameObject
    {
        public bool activeSelf { get; private set; } = true;
        public void SetActive(bool value) => activeSelf = value;
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte red, byte green, byte blue, byte alpha)
        {
            r = red;
            g = green;
            b = blue;
            a = alpha;
        }
    }
}
