using System;
using System.Numerics;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace V12Studio.Core
{
    /// <summary>
    /// Read/write helpers for an element's local transform. Rotation is
    /// exposed in degrees (YXZ order, matching the engine's own euler
    /// decomposition) so the inspector can show and edit real numbers.
    /// </summary>
    public static class TransformEdit
    {
        private const float Rad2Deg = 180f / MathF.PI;
        private const float Deg2Rad = MathF.PI / 180f;

        public static (float X, float Y, float Z) PositionOf(IWorldElement el)
        {
            var p = el.LocalTransform.Position;
            return (p.X, p.Y, p.Z);
        }

        /// <summary>Local rotation as euler angles in degrees (yaw, pitch, roll → X=pitch, Y=yaw, Z=roll).</summary>
        public static (float X, float Y, float Z) RotationDegreesOf(IWorldElement el)
        {
            var (yaw, pitch, roll) = ToEulerAngles(el.LocalTransform.Rotation);
            return (pitch * Rad2Deg, yaw * Rad2Deg, roll * Rad2Deg);
        }

        public static (float X, float Y, float Z) ScaleOf(IWorldElement el)
        {
            var s = el.LocalTransform.Scale;
            return (s.X, s.Y, s.Z);
        }

        public static void SetPosition(IWorldElement el, float x, float y, float z)
        {
            var lt = el.LocalTransform;
            lt.Position = new Vector3(x, y, z);
            el.LocalTransform = lt;
        }

        /// <summary>Set local rotation from euler angles in degrees (applied Y·X·Z).</summary>
        public static void SetRotationDegrees(IWorldElement el, float xDeg, float yDeg, float zDeg)
        {
            var lt = el.LocalTransform;
            lt.Rotation = FromEulerDegrees(xDeg, yDeg, zDeg);
            el.LocalTransform = lt;
        }

        public static void SetScale(IWorldElement el, float x, float y, float z)
        {
            var lt = el.LocalTransform;
            lt.Scale = new Vector3(x, y, z);
            el.LocalTransform = lt;
        }

        public static Quaternion FromEulerDegrees(float xDeg, float yDeg, float zDeg)
        {
            return Quaternion.CreateFromYawPitchRoll(yDeg * Deg2Rad, xDeg * Deg2Rad, zDeg * Deg2Rad);
        }

        /// <summary>Same decomposition the engine uses for the gizmo (Element.ToEulerAngles).</summary>
        public static (float yaw, float pitch, float roll) ToEulerAngles(Quaternion q)
        {
            float siny_cosp = 2 * (q.W * q.Y + q.Z * q.X);
            float cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
            float yaw = MathF.Atan2(siny_cosp, cosy_cosp);
            float sinp = 2 * (q.W * q.X - q.Y * q.Z);
            float pitch = Math.Abs(sinp) >= 1 ? MathF.CopySign(MathF.PI / 2, sinp) : MathF.Asin(sinp);
            float sinr_cosp = 2 * (q.W * q.Z + q.X * q.Y);
            float cosr_cosp = 1 - 2 * (q.X * q.X + q.Z * q.Z);
            float roll = MathF.Atan2(sinr_cosp, cosr_cosp);
            return (yaw, pitch, roll);
        }
    }
}
