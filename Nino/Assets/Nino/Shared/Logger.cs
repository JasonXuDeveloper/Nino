using UnityEngine;

namespace Nino.Shared
{
    public static class Logger
    {
        public static void D(object msg)
        {
            Debug.Log(msg);
        }

        public static void W(object msg)
        {
            Debug.LogWarning(msg);
        }

        public static void E(object msg)
        {
            Debug.LogError(msg);
        }

        public static void A(object msg)
        {
            Debug.LogAssertion(msg);
        }
        
        public static void D(string tag, object msg)
        {
            D($"[{tag}] {msg}");
        }

        public static void W(string tag, object msg)
        {
            W($"[{tag}] {msg}");
        }

        public static void E(string tag, object msg)
        {
            E($"[{tag}] {msg}");
        }

        public static void A(string tag, object msg)
        {
            A($"[{tag}] {msg}");
        }
    }
}