using System;
using System.Threading.Tasks;

namespace Handy.Services;

public interface ITranscriptionService : IDisposable
{
    bool IsReady { get; }

    Task<string> TranscribeAsync(float[] samples);
}
