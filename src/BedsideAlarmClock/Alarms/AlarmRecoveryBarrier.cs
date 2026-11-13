namespace BedsideAlarmClock.Alarms;

internal sealed class AlarmRecoveryBarrier
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int preparingDeliveries;
    private bool recoveryDeferred;

    public async Task<bool> BeginDeliveryAsync(Func<bool> beginDelivery)
    {
        ArgumentNullException.ThrowIfNull(beginDelivery);
        await gate.WaitAsync();
        try
        {
            if (!beginDelivery()) return false;
            preparingDeliveries++;
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RecoverAsync(Func<Task> recover)
    {
        ArgumentNullException.ThrowIfNull(recover);
        await gate.WaitAsync();
        try
        {
            recoveryDeferred = true;
            if (preparingDeliveries != 0) return false;
            await recover();
            recoveryDeferred = false;
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task FinishDeliveryAsync(Func<Task> recover)
    {
        ArgumentNullException.ThrowIfNull(recover);
        await gate.WaitAsync();
        try
        {
            if (preparingDeliveries == 0)
                throw new InvalidOperationException("No alarm delivery is being prepared.");
            preparingDeliveries--;
            if (preparingDeliveries == 0 && recoveryDeferred)
            {
                await recover();
                recoveryDeferred = false;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
