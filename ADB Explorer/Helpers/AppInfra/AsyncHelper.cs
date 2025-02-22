﻿namespace ADB_Explorer.Helpers;

public static class AsyncHelper
{
    public static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waitTask = Task.Run(async () =>
        {
            while (!condition()) await Task.Delay(10, cancellationToken);
        }, cancellationToken);

        await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken));
    }
}
