using System;
using System.Threading.Tasks;

namespace FinPlat.TestContainers.Assertions;

/// <summary>
/// Provides polling-based assertion extension methods for test accessors.
/// These methods retry until a condition is met or a timeout expires.
/// </summary>
public static class AssertionExtensions
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Polls the queue until a message matching the predicate is found, or the timeout expires.
    /// </summary>
    /// <param name="queue">The queue accessor to poll.</param>
    /// <param name="predicate">A function that returns true when the desired message is found.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <exception cref="TimeoutException">Thrown when no matching message is found within the timeout.</exception>
    public static async Task WaitForQueueMessageAsync(
        this QueueAccessor queue,
        Func<string, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (DateTime.UtcNow < deadline)
        {
            var messages = await queue.PeekMessagesAsync(32);
            foreach (var message in messages)
            {
                if (predicate(message))
                    return;
            }

            await Task.Delay(PollInterval);
        }

        throw new TimeoutException(
            $"No matching message found in queue within {(timeout ?? DefaultTimeout).TotalSeconds}s.");
    }

    /// <summary>
    /// Polls the blob container until the specified blob exists, or the timeout expires.
    /// </summary>
    /// <param name="blob">The blob accessor to poll.</param>
    /// <param name="blobName">Name of the blob to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <exception cref="TimeoutException">Thrown when the blob is not found within the timeout.</exception>
    public static async Task WaitForBlobAsync(
        this BlobAccessor blob,
        string blobName,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (DateTime.UtcNow < deadline)
        {
            if (await blob.ExistsAsync(blobName))
                return;

            await Task.Delay(PollInterval);
        }

        throw new TimeoutException(
            $"Blob '{blobName}' was not found within {(timeout ?? DefaultTimeout).TotalSeconds}s.");
    }

    /// <summary>
    /// Polls the mock API until the specified path reaches the expected call count, or the timeout expires.
    /// </summary>
    /// <param name="mock">The mock API accessor to poll.</param>
    /// <param name="path">URL path to monitor.</param>
    /// <param name="expectedCount">Expected number of calls.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <exception cref="TimeoutException">Thrown when the expected call count is not reached within the timeout.</exception>
    public static async Task WaitForCallCountAsync(
        this MockApiAccessor mock,
        string path,
        int expectedCount,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        int lastCount = 0;

        while (DateTime.UtcNow < deadline)
        {
            lastCount = await mock.GetCallCountAsync(path);
            if (lastCount >= expectedCount)
                return;

            await Task.Delay(PollInterval);
        }

        throw new TimeoutException(
            $"Expected {expectedCount} call(s) to '{path}' but only got {lastCount} within {(timeout ?? DefaultTimeout).TotalSeconds}s.");
    }
}
