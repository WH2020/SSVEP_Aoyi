using System.Text.Json;
using Ssvep.Speller.Core.Research;

namespace Ssvep.Speller.Tests;

public sealed class TrialFeedbackTests
{
    private static SessionEvent Event(string kind,object data)=>new(1,kind,null,JsonSerializer.SerializeToElement(data,CanonicalJson.Options));
    private static List<SessionEvent> Trial()=>
    [
        Event("calibration-plan",new {targetFeedbackPolicy=TrialFeedbackSelection.Policy}),
        Event("trial-marker",new TrialMarker("t","observe-start","target","T2",0,0,"m",0,"Estimated",DrawHostSeconds:0)),
        Event("trial-marker",new TrialMarker("t","observe-end","target","T2",4,4,"m",1000,"Estimated",DrawHostSeconds:4)),
        Event("recognition",new {hostSeconds=2.6,firstSampleIndex=0,lastSampleIndex=624,decision=new {accepted=true,targetId="T1"}}),
        Event("recognition",new {hostSeconds=2.8,firstSampleIndex=50,lastSampleIndex=674,decision=new {accepted=true,targetId="T2"}})
    ];

    [Fact]
    public void ReportUsesCommittedFeedbackInsteadOfAnEarlierUndrawnDecision()
    {
        var events=Trial();
        events.Add(Event("trial-feedback",new TrialFeedbackSelection("t","T2",50,674,2.8,2.81)));
        events.Add(Event("frame",new {hostSeconds=2.82,targetId="T2"}));
        events.Add(Event("recognition",new {hostSeconds=3.0,firstSampleIndex=100,lastSampleIndex=724,decision=new {accepted=true,targetId="T4"}}));
        events.Add(Event("frame",new {hostSeconds=3.02,targetId="T2"}));
        var feedback=RecordedFeedbackEvaluator.Evaluate(events,"hash").Trials.Single();
        Assert.Equal("T2",feedback.FirstSelection); Assert.True(feedback.Correct);
        Assert.Equal(2.8,feedback.DecisionDelaySeconds); Assert.Equal(2.82,feedback.DrawDelaySeconds);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UndrawnOrUncommittedResultsDoNotBecomeDisplayedFeedback(bool committed)
    {
        var events=Trial();
        if(committed) events.Add(Event("trial-feedback",new TrialFeedbackSelection("t","T2",50,674,2.8,2.81)));
        events.Add(Event("frame",new {hostSeconds=2.82,targetId=(string?)null}));
        var feedback=RecordedFeedbackEvaluator.Evaluate(events,"hash").Trials.Single();
        Assert.Null(feedback.FirstSelection); Assert.False(feedback.Correct);
        Assert.Null(feedback.DecisionDelaySeconds); Assert.Null(feedback.DrawDelaySeconds);
    }

    [Fact]
    public void HoldingAWrongResultCannotBeRewrittenByLaterCorrectWindows()
    {
        var events=Trial();
        events.Add(Event("trial-feedback",new TrialFeedbackSelection("t","T1",0,624,2.6,2.61)));
        events.Add(Event("frame",new {hostSeconds=2.62,targetId="T1"}));
        events.Add(Event("frame",new {hostSeconds=3.02,targetId="T1"}));
        var feedback=RecordedFeedbackEvaluator.Evaluate(events,"hash").Trials.Single();
        Assert.Equal("T1",feedback.FirstSelection); Assert.False(feedback.Correct);
    }
}
