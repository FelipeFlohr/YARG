using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace YARG.Helpers
{
    /// <summary>
    /// Runs work off the main thread where threads exist. On the web there are no threads,
    /// so the work is run inline on the main thread after yielding one frame.
    /// </summary>
    public static class PlatformTask
    {
        public static async UniTask Run(Action action, CancellationToken cancellationToken = default)
        {
            if (PlatformInfo.IsWeb)
            {
                await UniTask.Yield(cancellationToken);
                action();
                return;
            }

            await UniTask.RunOnThreadPool(action, cancellationToken: cancellationToken);
        }

        public static async UniTask<T> Run<T>(Func<T> func, CancellationToken cancellationToken = default)
        {
            if (PlatformInfo.IsWeb)
            {
                await UniTask.Yield(cancellationToken);
                return func();
            }

            return await UniTask.RunOnThreadPool(func, cancellationToken: cancellationToken);
        }
    }
}
