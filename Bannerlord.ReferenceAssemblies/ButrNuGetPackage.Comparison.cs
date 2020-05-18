using System;

namespace Bannerlord.ReferenceAssemblies
{

    internal partial struct ButrNuGetPackage
        : IComparable<ButrNuGetPackage>, IComparable, IEquatable<ButrNuGetPackage>
    {

        public bool Equals(ButrNuGetPackage other)
            => Name == other.Name && PkgVersion == other.PkgVersion;

        public override bool Equals(object obj)
            => obj is ButrNuGetPackage other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(Name, PkgVersion);

        public static bool operator ==(ButrNuGetPackage left, ButrNuGetPackage right)
            => left.Equals(right);

        public static bool operator !=(ButrNuGetPackage left, ButrNuGetPackage right)
            => !left.Equals(right);

        public int CompareTo(ButrNuGetPackage other)
        {
            var nameComparison = string.Compare(Name, other.Name, StringComparison.OrdinalIgnoreCase);
            return nameComparison == 0
                ? PkgVersion.CompareTo(other.PkgVersion)
                : nameComparison;
        }

        public int CompareTo(object obj)
        {
            if (ReferenceEquals(null, obj))
                return 1;

            return obj is ButrNuGetPackage other
                ? CompareTo(other)
                : throw new ArgumentException($"Object must be of type {nameof(ButrNuGetPackage)}");
        }

        public static bool operator <(ButrNuGetPackage left, ButrNuGetPackage right)
            => left.CompareTo(right) < 0;

        public static bool operator >(ButrNuGetPackage left, ButrNuGetPackage right)
            => left.CompareTo(right) > 0;

        public static bool operator <=(ButrNuGetPackage left, ButrNuGetPackage right)
            => left.CompareTo(right) <= 0;

        public static bool operator >=(ButrNuGetPackage left, ButrNuGetPackage right)
            => left.CompareTo(right) >= 0;

    }

}