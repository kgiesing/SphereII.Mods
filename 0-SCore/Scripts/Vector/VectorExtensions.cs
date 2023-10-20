using UnityEngine;

public static class VectorExtensions
{
    // Used for calculating the compass direction: 2 * sin(45 deg)
    private const float sin45x2 = 1.4142135623730950488016887242097f;

    /// <summary>
    /// Returns the square of the distance between this vector and another.
    /// Functionally equivalent to:
    /// <code>(this - b).sqrMagnitude</code>
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    public static float SqrDistance(this Vector3 a, Vector3 b)
    {
        float dX = a.x - b.x;
        float dY = a.y - b.y;
        float dZ = a.z - b.z;
        return dX * dX + dY * dY + dZ * dZ;
    }

    /// <summary>
    /// Returns a vector representing the closest "compass direction" of this vector.
    /// This is a normalized vector, where the x/z coordinate axis represents a compass direction
    /// (N, NE, E, SE...) and the y coordinate is 0.
    /// </summary>
    /// <param name="direction"></param>
    /// <returns></returns>
    public static Vector3 ToCompassDirection(this Vector3 direction)
    {
        // To visualize how this works: https://www.desmos.com/calculator/vlsq3jgrhp
        var normalized = direction.normalized;
        return new Vector3(
                Mathf.Round(normalized.x * sin45x2) / sin45x2,
                0,
                Mathf.Round(normalized.z * sin45x2) / sin45x2)
            .normalized;
    }

    /// <summary>
    /// Converts to a vector where the x, y, and z coordinates are centered.
    /// Functionally equivalent to:
    /// <code>new Vector3i(this).ToVector3Center()</code>
    /// </summary>
    /// <param name="v"></param>
    /// <returns></returns>
    public static Vector3 ToVector3Center(this Vector3 v)
    {
        return new Vector3((int)v.x + 0.5f, (int)v.y + 0.5f, (int)v.z + 0.5f);
    }

    /// <summary>
    /// Helper method to center a vector's x and z coordinates and floor the y coordinate.
    /// Converts to a vector where the x and z coordinates are centered, and the y coordinate is
    /// floored. Functionally equivalent to:
    /// <code>new Vector3i(this).ToVector3CenterXZ()</code>
    /// </summary>
    /// <param name="v"></param>
    /// <returns></returns>
    public static Vector3 ToVector3CenterXZ(this Vector3 v)
    {
        return new Vector3((int)v.x + 0.5f, (int)v.y, (int)v.z + 0.5f);
    }
}
