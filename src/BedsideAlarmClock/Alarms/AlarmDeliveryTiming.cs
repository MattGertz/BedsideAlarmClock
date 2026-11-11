namespace BedsideAlarmClock.Alarms;

internal enum AlarmDeliveryTimingDecision
{
    EligibleToStart,
    Missed
}

internal static class AlarmDeliveryTiming
{
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);

    public static AlarmDeliveryTimingDecision Evaluate(
        DateTimeOffset triggerAt,
        DateTimeOffset observedAt,
        DateTimeOffset startDecisionAt,
        bool knownUnavailableAtTrigger)
    {
        if (observedAt < triggerAt)
            throw new ArgumentOutOfRangeException(nameof(observedAt), "A callback cannot be recorded before its trigger.");
        if (startDecisionAt < observedAt)
            throw new ArgumentOutOfRangeException(nameof(startDecisionAt), "The start decision cannot precede the callback.");

        return IsMissed(triggerAt, startDecisionAt, knownUnavailableAtTrigger)
            ? AlarmDeliveryTimingDecision.Missed
            : AlarmDeliveryTimingDecision.EligibleToStart;
    }

    public static bool IsMissed(DateTimeOffset triggerAt, DateTimeOffset decisionAt, bool knownUnavailableAtTrigger)
    {
        if (decisionAt < triggerAt)
            throw new ArgumentOutOfRangeException(nameof(decisionAt), "A missed decision cannot precede the trigger.");
        return knownUnavailableAtTrigger || decisionAt - triggerAt > MaximumDelay;
    }
}
