using System;

namespace Bannerlord.ReferenceAssemblies
{
    internal partial struct NuGetPackage : IComparable<NuGetPackage>, IComparable, IEquatable<NuGetPackage>
    {
        public bool Equals(NuGetPackage other) => Name == other.Name && PkgVersion == other.PkgVersion;
        public override bool Equals(object? obj) => obj is NuGetPackage other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Name, PkgVersion);

        public static bool operator ==(NuGetPackage left, NuGetPackage right) => left.Equals(right);
        public static bool operator !=(NuGetPackage left, NuGetPackage right) => !left.Equals(right);

        public int CompareTo(NuGetPackage other)
        {
            var nameComparison = string.Compare(Name, other.Name, StringComparison.OrdinalIgnoreCase);
            return nameComparison == 0
                ? PkgVersion.CompareTo(other.PkgVersion)
                : nameComparison;
        }
        public int CompareTo(object? obj)
        {
            if (obj is null)
                return 1;

            return obj is NuGetPackage other
                ? CompareTo(other)
                : throw new ArgumentException($"Object must be of type {nameof(NuGetPackage)}");
        }

        public static bool operator <(NuGetPackage left, NuGetPackage right) => left.CompareTo(right) < 0;
        public static bool operator >(NuGetPackage left, NuGetPackage right) => left.CompareTo(right) > 0;
        public static bool operator <=(NuGetPackage left, NuGetPackage right) => left.CompareTo(right) <= 0;
        public static bool operator >=(NuGetPackage left, NuGetPackage right) => left.CompareTo(right) >= 0;
    }
}