using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Elements.Core;

namespace MeshLoadTweak;

public class TaskSemaphore
{
    private readonly object _lock = new object();

    private int _available;
    private int _minimumDelayMS;
    private Queue<TaskCompletionSource<object>> _waitingTasks = new Queue<TaskCompletionSource<object>>();

    private DateTime _nextReleaseTime;
    
    public TaskSemaphore(int initialCount, int minimumDelayMS)
    {
        _available = initialCount;
        _minimumDelayMS = minimumDelayMS;

        _nextReleaseTime = DateTime.UtcNow;
    }

    public async Task<Token> WaitAsync()
    {
        Task waitForPermit;
        lock (_lock)
        {
            UniLog.Log("[SlowSync] WaitAsync: available permits: " + _available + " queued tasks: " + _waitingTasks.Count);
            if (_available > 0)
            {
                _available--;
                waitForPermit = Task.CompletedTask;
            }
            else
            {
                var tcs = new TaskCompletionSource<object>();
                _waitingTasks.Enqueue(tcs);
                waitForPermit = tcs.Task;
            }
        }
        
        await waitForPermit;
        await WaitForSlot();

        return new Token(this);
    }

    private async Task WaitForSlot()
    {
        // Wait until the next release time if necessary
        var now = DateTime.UtcNow;
        var curReleaseTime = _nextReleaseTime;

        if (curReleaseTime <= now)
        {
            _nextReleaseTime = now.AddMilliseconds(_minimumDelayMS);
        }
        else
        {
            _nextReleaseTime = curReleaseTime.AddMilliseconds(_minimumDelayMS);
            
            // Wait for our release time
            await Task.Delay((int)(curReleaseTime - now).TotalMilliseconds);
        }
    }
    
    private async Task<Token> WaitForSlotAfter(Task task)
    {
        await task;
        await WaitForSlot();
        
        return new Token(this);
    }

    public class Token : IDisposable
    {
        private readonly TaskSemaphore _semaphore;

        public Token(TaskSemaphore semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            lock (_semaphore._lock)
            {
                UniLog.Log("[SlowSync] Released token: " + _semaphore + " current permits: " + _semaphore._available + " current waiting tasks: " + _semaphore._waitingTasks.Count);
                if (_semaphore._waitingTasks.Count > 0)
                {
                    _semaphore._waitingTasks.Dequeue().SetResult(null);
                }
                else
                {
                    _semaphore._available++;
                }
            }
        }
    }
}