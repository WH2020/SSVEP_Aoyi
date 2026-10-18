using System.Text.Json;
using Ssvep.Speller.Core.Classification;
using Ssvep.Speller.Core.Research;

namespace Ssvep.Speller.Tests;

public sealed class TerminalTrialFeedbackTests
{
    private static SessionEvent Event(string kind,object data)=>new(1,kind,null,JsonSerializer.SerializeToElement(data,CanonicalJson.Options));
    private static SessionEvent Mark(string phase,double seconds)=>Event("trial-marker",
        new TrialMarker("t",phase,"target","T2",seconds,seconds,"model",seconds*250,"Estimated",DrawHostSeconds:seconds,AlignmentMarginSamples:8));
    private static List<SessionEvent> Events()=>
    [
        Event("calibration-plan",new {targetFeedbackPolicy=TrialFeedbackSelection.Policy}),
        Mark("observe-start",0),Mark("observe-end",4),Mark("feedback-start",4.02),Mark("feedback-end",4.52)
    ];
    private static ReplayDecision Decision(long first,long last,string? target)
    {
        var scores=Enumerable.Range(1,6).ToDictionary(i=>$"T{i}",i=>$"T{i}"==target?.9:.1);
        var result=new ClassificationResult(target??"T1",target is null?.1:.9,"T6",.1,scores);
        return new(1,1,last,first,last,last/250.0,"model",new(result,scores,"weighted-rho2-normalized-v1"),SelectionPolicy.Evaluate(result,new()));
    }

    [Theory]
    [InlineData(250)] [InlineData(500)]
    public void TerminalWindowUsesTheLastCompleteSampleGridIntervalWithAlignmentMargins(int rate)
    {
        int window=rate*5/2,step=rate/5;
        var result=TrialDecisionWindow.LatestWithin(13,window,step,8,4*rate-8)!;
        Assert.True(result.FirstSampleIndex>=8&&result.LastSampleIndex<=4*rate-8);
        Assert.Equal(window,result.LastSampleIndex-result.FirstSampleIndex+1);
        Assert.True(result.LastSampleIndex+step>4*rate-8);
        Assert.Equal(0,(result.FirstSampleIndex-13)%step);
        Assert.Equal(result,TrialDecisionWindow.LatestWithin(13,window,step,8,result.LastSampleIndex));
        Assert.Null(TrialDecisionWindow.LatestWithin(13,window,step,8,rate));
        Assert.Null(TrialDecisionWindow.LatestWithin(13,window,step,double.NaN,4*rate));
    }

    [Theory]
    [InlineData("T2",true)] [InlineData("T3",false)] [InlineData(null,false)]
    public void ReplayMakesOnlyTheTerminalDecisionEvenWhenEarlierWindowsWereAccepted(string? terminal,bool correct)
    {
        var report=ReplayEvaluator.Evaluate([Decision(50,674,"T1"),Decision(350,974,terminal),Decision(400,1024,"T2")],Events(),250);
        var trial=report.Trials.Single();
        Assert.Equal(terminal,trial.FirstSelection); Assert.Equal(correct,trial.Correct);
        Assert.Equal(terminal is not null,trial.Covered);
        Assert.Equal(terminal is null?null:(double?)4,trial.DelaySeconds);
        Assert.Equal("evaluated",trial.Status);
    }

    [Fact]
    public void RecordedTerminalFeedbackUsesItsPostStimulusDrawAndCommitTime()
    {
        var events=Events();
        events.Add(Event("trial-feedback",new TrialFeedbackSelection("t","T2",350,974,3.95,4.01)));
        events.Add(Event("frame",new {hostSeconds=3.96,targetId="T1"}));
        events.Add(Event("frame",new {hostSeconds=4.03,targetId="T2"}));
        var trial=RecordedFeedbackEvaluator.Evaluate(events,"hash").Trials.Single();
        Assert.Equal("T2",trial.FirstSelection); Assert.True(trial.Correct);
        Assert.Equal(4.01,trial.DecisionDelaySeconds); Assert.Equal(4.03,trial.DrawDelaySeconds);
    }

    [Theory]
    [InlineData(3.9,4.03)] [InlineData(4.01,3.99)] [InlineData(4.01,4.53)]
    public void EarlyCommitsAndFramesOutsideFeedbackCannotCountAsTheTerminalResult(double committed,double drawn)
    {
        var events=Events();
        events.Add(Event("trial-feedback",new TrialFeedbackSelection("t","T2",350,974,3.8,committed)));
        events.Add(Event("frame",new {hostSeconds=drawn,targetId="T2"}));
        var trial=RecordedFeedbackEvaluator.Evaluate(events,"hash").Trials.Single();
        Assert.Null(trial.FirstSelection); Assert.False(trial.Correct);
    }

    [Fact]
    public void RejectionDoesNotInheritAnEarlierBorder()
    {
        var events=Events();
        events.Add(Event("trial-feedback",new TrialFeedbackSelection("t",null,350,974,3.95,4.01,"LowScore")));
        events.Add(Event("frame",new {hostSeconds=2.8,targetId="T2"}));
        events.Add(Event("frame",new {hostSeconds=4.03,targetId=(string?)null}));
        var trial=RecordedFeedbackEvaluator.Evaluate(events,"hash").Trials.Single();
        Assert.Null(trial.FirstSelection); Assert.False(trial.Correct); Assert.Equal("LowScore",trial.Reason);
    }
}
