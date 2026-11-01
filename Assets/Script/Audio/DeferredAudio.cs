#nullable enable
using System.Collections.Generic;
using YARG.Core.Audio;

namespace YARG.Audio
{
    /// <summary>
    /// A mixer whose stems finish loading after <see cref="StemMixer.AddChannels"/> returns, because the backend
    /// decodes them asynchronously (the web build). <see cref="StemMixer.Length"/> is only final once
    /// <see cref="IsLoading"/> is false.
    /// </summary>
    public interface IDeferredStemMixer
    {
        bool IsLoading { get; }

        /// <summary>
        /// A message for the player when the song cannot play (for example, it does not fit in memory), or null.
        /// </summary>
        string? LoadError { get; }
    }

    /// <summary>
    /// The stems that can be muted in the song about to load (the ones its players play), set by the gameplay
    /// around its audio load. A backend short on memory (the web build) mixes every other stem except the crowd
    /// into the background stem. Null outside of gameplay (menu previews and music): nothing is muted there, so
    /// such a backend mixes everything but the crowd into one stem. The desktop backend ignores it.
    /// </summary>
    public static class StemUsageHint
    {
        public static HashSet<SongStem>? PlayedStems { get; set; }
    }
}
