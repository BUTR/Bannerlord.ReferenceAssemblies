using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies
{
    internal static class IAsyncEnumerableExtensions
    {
        public static async IAsyncEnumerable<TResult> SelectParallel<TResult, TSource>(this IAsyncEnumerable<TSource> enumerable, int maxConcurrent, Func<TSource, Task<TResult>> func, [EnumeratorCancellation] CancellationToken cancellation = default)
        {
            var semaphore = new SemaphoreSlim(maxConcurrent);
            var returnVal = new List<TResult>();
            var tasks = await enumerable.Select(@enum => Task.Run(async () =>
                {
                    try
                    {
                        await semaphore.WaitAsync(cancellation).ConfigureAwait(false);
                        returnVal.Add(await func(@enum));
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellation))
                .ToListAsync(cancellation);
            await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var val in returnVal)
                yield return val;
        }
    }
}