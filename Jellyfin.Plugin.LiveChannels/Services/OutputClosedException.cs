using System;
using System.IO;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Thrown when a producer's output (the session's segmenter) stops accepting data. It ends the producer run
/// outright: the producer is not at fault, and nothing else the session could start would be delivered either.
/// </summary>
public sealed class OutputClosedException : IOException
{
    /// <summary>Initializes a new instance of the <see cref="OutputClosedException"/> class.</summary>
    public OutputClosedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OutputClosedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public OutputClosedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OutputClosedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The write failure.</param>
    public OutputClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
