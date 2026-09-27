using System.Runtime.InteropServices;

namespace Bannerlord.ReferenceAssemblies;

internal static class DictionaryExtensions
{
    /// <summary>The value under the key, added from the factory when the key is missing.</summary>
    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, Func<TValue> create) where TKey : notnull
    {
        ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out var exists);
        if (!exists)
            value = create();
        return value!;
    }
}
