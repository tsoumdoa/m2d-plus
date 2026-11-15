using System.Globalization;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

public static class GeometryFingerprint
{
    /// <summary>SHA-256 over Rhino's complete binary serialization, including user data and archive metadata.</summary>
    public static string Compute(GeometryBase geometry)
    {
        // RhinoCommon exposes its binary archive through ISerializable. No formatter serializes or
        // deserializes objects here; suppress only the legacy SerializationInfo construction warning.
#pragma warning disable SYSLIB0050
        var info = new SerializationInfo(geometry.GetType(), new FormatterConverter());
        geometry.GetObjectData(info, new StreamingContext());
#pragma warning restore SYSLIB0050
        var fields = new List<SerializationEntry>(); var enumerator = info.GetEnumerator();
        while (enumerator.MoveNext()) fields.Add(enumerator.Current);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var binaryFound = false;
        foreach (var field in fields.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(field.Name + ":" + field.ObjectType.FullName + ":"));
            if (field.Value is byte[] bytes) { hash.AppendData(bytes); binaryFound = true; }
            else if (field.Value is IConvertible scalar) hash.AppendData(Encoding.UTF8.GetBytes(scalar.ToString(CultureInfo.InvariantCulture) ?? ""));
            else throw new InvalidOperationException("Unexpected Rhino archive metadata; content fingerprint refused.");
        }
        if (!binaryFound) throw new InvalidOperationException("Rhino binary geometry archive missing; content fingerprint refused.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
