using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkyFrost.Base;

namespace MeshLoadTweak;

public class RecordPrefetcher : IDisposable
{
    private static object _staticLock = new();
    private static HashSet<RecordPrefetcher> _instances = new();
    
    private const int MAX_PARALLEL_PREFETCHES = 64;
    private const int MIN_DELAY = 4; // ms
    private readonly SkyFrostInterface _cloud;
    private readonly CancellationToken _token;
    private readonly CancellationTokenSource _tokenSource;
    private Dictionary<string, Task<CloudResult<SkyFrost.Base.AssetInfo>>> _prefetched = new();
    
    public RecordPrefetcher(SkyFrostInterface cloud, List<string> signatures, CancellationToken token)
    {
        _cloud = cloud;
        _token = token;

        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = source.Token;
        _tokenSource = source;
        
        Task.Run(() => DoPrefetch(signatures), token);

        lock(_staticLock) _instances.Add(this);
    }

    public void Dispose()
    {
        lock(_staticLock) _instances.Remove(this);
        _tokenSource.Cancel();
        _tokenSource.Dispose();
    }

    public static Task<CloudResult<SkyFrost.Base.AssetInfo>>? TryGetGlobalAssetInfo(
        AssetInterface iface,
        string signature
    ) {
        lock (_staticLock)
        {
            foreach (var inst in _instances)
            {
                if (inst._cloud.Assets == iface && inst._prefetched.TryGetValue(signature, out var task))
                {
                    if (task.IsCompleted)
                    {
                        return task;
                    }
                }
            }
        }

        return null;
    }

    private async Task DoPrefetch(List<string> signatures)
    {
        var semaphore = new System.Threading.SemaphoreSlim(MAX_PARALLEL_PREFETCHES);
        Task delay = Task.CompletedTask;
        foreach (var sig in signatures)
        {
            await delay;
            await semaphore.WaitAsync(cancellationToken: _token);

            delay = Task.Delay(MIN_DELAY, _token);
            
            if (_token.IsCancellationRequested) return;

            Task thisTask;
            
            lock (this)
            {
                if (_prefetched.ContainsKey(sig)) continue;
                thisTask = _prefetched[sig] = _cloud.Assets.GetGlobalAssetInfo(sig);
            }

            _ = thisTask.ContinueWith(_ => semaphore.Release(), _token);
        }
    }
}