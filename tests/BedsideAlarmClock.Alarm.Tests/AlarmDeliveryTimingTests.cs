using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Alarm.Tests;

public sealed class AlarmDeliveryTimingTests
{
    private static readonly DateTimeOffset Trigger = DateTimeOffset.Parse("2025-01-02T07:30:00Z");

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 29, true)]
    [InlineData(30, 30, true)]
    [InlineData(1, 31, false)]
    [InlineData(31, 31, false)]
    public void StartDecisionUsesThirtySecondInclusiveCutoff(int callbackDelay, int startDelay, bool eligible)
    {
        var result = AlarmDeliveryTiming.Evaluate(
            Trigger, Trigger.AddSeconds(callbackDelay), Trigger.AddSeconds(startDelay), false);
        Assert.Equal(eligible ? AlarmDeliveryTimingDecision.EligibleToStart : AlarmDeliveryTimingDecision.Missed, result);
    }

    [Fact]
    public void EvenOneTickPastCutoffIsMissed()
    {
        Assert.Equal(AlarmDeliveryTimingDecision.Missed,
            AlarmDeliveryTiming.Evaluate(Trigger, Trigger, Trigger.AddSeconds(30).AddTicks(1), false));
    }

    [Fact]
    public void KnownOutageAtTriggerOverridesTimelyCallback()
    {
        Assert.Equal(AlarmDeliveryTimingDecision.Missed,
            AlarmDeliveryTiming.Evaluate(Trigger, Trigger.AddSeconds(1), Trigger.AddSeconds(2), true));
    }

    [Fact]
    public void InvalidTimeOrderingIsReported()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AlarmDeliveryTiming.Evaluate(Trigger, Trigger.AddTicks(-1), Trigger, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AlarmDeliveryTiming.Evaluate(Trigger, Trigger.AddSeconds(1), Trigger, false));
    }
}
